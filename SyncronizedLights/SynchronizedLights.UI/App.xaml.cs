using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Serilog;
using Lib.Application.Facades;
using Lib.Application.Interfaces;
using Lib.Application.Models;
using Lib.Application.Services;
using SynchronizedLights.UI.ViewModels;

namespace SynchronizedLights.UI
{
    /// <summary>
    /// アプリケーション本体
    /// 概要：起動時に appsettings.json を読み込み、
    /// Lighting:Mode に応じて DummyLightingFacade か ApiLightingFacade を生成する。
    /// 同時に Serilog を初期化して、ファイル＋デバッグ出力の2経路でログを記録する。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>アプリ全体で共有するライティング制御ファセード</summary>
        public static ILightingFacade? LightingFacade { get; private set; }

        /// <summary>現在の動作モード（"Dummy" or "Real"）</summary>
        public static string LightingMode { get; private set; } = "Dummy";

        /// <summary>
        /// データ保存ディレクトリ（シーケンス・色設定・UserState 等）。
        /// appsettings.json の Lighting:DataDirectory で指定可能。
        /// 未指定時は %LocalAppData%\SynchronizedLights。
        /// </summary>
        public static string DataDirectory { get; private set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SynchronizedLights");

        /// <summary>UserState 管理サービス</summary>
        public static UserStateService UserStateService { get; private set; } = null!;

        /// <summary>起動時に読み込んだ UserState</summary>
        public static UserState? LoadedUserState { get; private set; }

        /// <summary>
        /// appsettings.json で指定された送信機チャネルの既定値（FA、1〜4）
        /// 概要：未指定時は 1 を使用。UserState に保存値があればそれを優先。
        /// </summary>
        public static byte DefaultChannel { get; private set; } = 1;

        /// <summary>
        /// appsettings.json で指定された送信機電力の既定値（FB、0〜3）
        /// 概要：未指定時は 3 を使用。UserState に保存値があればそれを優先。
        /// </summary>
        public static byte DefaultPower { get; private set; } = 3;

        /// <summary>MainWindowViewModel への静的参照</summary>
        public static MainWindowViewModel? MainVm { get; set; }

        /// <summary>アプリ全体で共有する遅延計測トラッカー</summary>
        public static LatencyTracker LatencyTracker { get; private set; } = new LatencyTracker(windowSize: 1000);

        /// <summary>操作ミス計測トラッカー</summary>
        public static MisOperationTracker MisOpTracker => MisOperationTracker.Instance;

        /// <summary>KPI 定期ログ出力タイマー</summary>
        private DispatcherTimer? _kpiTimer;

        /// <summary>自動再接続タイマー</summary>
        private DispatcherTimer? _reconnectTimer;

        /// <summary>再接続試行中の重複防止フラグ</summary>
        private bool _reconnectInFlight;

        /// <summary>ハートビートタイマー</summary>
        private DispatcherTimer? _heartbeatTimer;

        /// <summary>KeepAlive タイマー（SNO端末セルフモード復帰防止）</summary>
        private DispatcherTimer? _keepAliveTimer;

        /// <summary>KeepAlive 送信中の重複防止フラグ</summary>
        private bool _keepAliveInFlight;

        /// <summary>自動起動した API サーバープロセス</summary>
        private Process? _apiProcess;

        /// <summary>API 実行ファイルのパス（プロセス再起動用に保持）</summary>
        private string? _apiExePath;

        /// <summary>二重起動防止用 Mutex</summary>
        private Mutex? _singleInstanceMutex;

        /// <summary>二重起動検出による終了中フラグ（IntegratedWindow の Closing ダイアログ抑止用）</summary>
        public static bool IsShuttingDownDueToMutex { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // ----- 二重起動防止 -----
            _singleInstanceMutex = new Mutex(true, "SynchronizedLights.UI.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                IsShuttingDownDueToMutex = true;
                MessageBox.Show(
                    "シンクロライト制御アプリは既に起動しています。",
                    "二重起動",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            // ----- Serilog 初期化 -----
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    path: Path.Combine(logDir, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Debug(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}")
                .CreateLogger();

            Log.Information("=== アプリ起動 ===");

            // ----- 設定読込 -----
            var config = LoadConfiguration();
            LightingMode = config["Lighting:Mode"] ?? "Dummy";

            // ----- データ保存ディレクトリ -----
            var dataDir = config["Lighting:DataDirectory"];
            if (!string.IsNullOrWhiteSpace(dataDir))
            {
                // 相対パスなら exe 本体の場所を基準に解決
                // （SingleFile publish では AppContext.BaseDirectory が一時展開先になるため ProcessPath を使う）
                if (!Path.IsPathRooted(dataDir))
                {
                    var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                    dataDir = Path.Combine(exeDir, dataDir);
                }
                DataDirectory = Path.GetFullPath(dataDir);
            }
            try { Directory.CreateDirectory(DataDirectory); }
            catch (Exception ex) { Log.Warning(ex, "DataDirectory 作成失敗: {Dir}", DataDirectory); }
            Log.Information("DataDirectory = {Dir}", DataDirectory);
            UserStateService = new UserStateService(DataDirectory);
            var queueCapacity = int.TryParse(config["Lighting:SendQueueCapacity"], out var q) ? q : 256;
            var sendIntervalMs = int.TryParse(config["Lighting:SendIntervalMs"], out var s) ? s : 5;
            var apiBaseUrl = config["Lighting:ApiBaseUrl"] ?? "http://localhost:5100";

            // ----- キュー間引きポリシー-----
            var queuePolicy = config["Lighting:QueuePolicy"] ?? "Wait";
            var dropThreshold = double.TryParse(
                config["Lighting:QueueDropThreshold"],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var th) ? th : 0.95;
            var dummySendDelayMs = int.TryParse(
                config["Lighting:DummySendDelayMs"], out var d) ? d : 100;

            // 遅延計測
            var latencyWindow = int.TryParse(
                config["Lighting:LatencyWindowSize"], out var lw) ? lw : 1000;
            var latencyLogSec = int.TryParse(
                config["Lighting:LatencyLogIntervalSec"], out var li) ? li : 30;
            var latencyEnabled = !string.Equals(
                config["Lighting:LatencyEnabled"], "false", StringComparison.OrdinalIgnoreCase);

            // 自動再接続
            var autoReconnectEnabled = !string.Equals(
                config["Lighting:AutoReconnectEnabled"], "false", StringComparison.OrdinalIgnoreCase);
            var reconnectIntervalSec = int.TryParse(
                config["Lighting:ReconnectIntervalSec"], out var rs) ? rs : 5;
            // ハートビート
            var heartbeatEnabled = !string.Equals(
                config["Lighting:HeartbeatEnabled"], "false", StringComparison.OrdinalIgnoreCase);
            var heartbeatIntervalSec = int.TryParse(
                config["Lighting:HeartbeatIntervalSec"], out var hb) ? hb : 30;
            // KeepAlive（SNO端末セルフモード復帰防止）
            var keepAliveEnabled = !string.Equals(
                config["Lighting:KeepAliveEnabled"], "false", StringComparison.OrdinalIgnoreCase);
            var keepAliveIntervalMs = int.TryParse(
                config["Lighting:KeepAliveIntervalMs"], out var kaMs) ? kaMs : 800;

            // 送信機初期化の既定値（Channel 1〜4 / Power 0〜3）
            if (byte.TryParse(config["Lighting:DefaultChannel"], out var defCh) && defCh >= 1 && defCh <= 4)
            {
                DefaultChannel = defCh;
            }
            if (byte.TryParse(config["Lighting:DefaultPower"], out var defPw) && defPw <= 3)
            {
                DefaultPower = defPw;
            }
            Log.Information("Default Transmitter: Channel={Ch}, Power={Pwr}", DefaultChannel, DefaultPower);
            // 循環バッファを設定値のサイズで再作成
            LatencyTracker = new LatencyTracker(windowSize: latencyWindow);

            Log.Information(
                "LightingMode={Mode}, ApiBaseUrl={Url}, QueueCapacity={Queue}, SendIntervalMs={Interval}, " +
                "QueuePolicy={Policy}, DropThreshold={Thr}, DummySendDelayMs={Delay}, " +
                "LatencyEnabled={LatEn}, LatencyWindow={LatWin}, LatencyLogIntervalSec={LatInt}, " +
                "AutoReconnectEnabled={RcEn}, ReconnectIntervalSec={RcInt}, " +
                "HeartbeatEnabled={HbEn}, HeartbeatIntervalSec={HbInt}",
                LightingMode, apiBaseUrl, queueCapacity, sendIntervalMs,
                queuePolicy, dropThreshold, dummySendDelayMs,
                latencyEnabled, latencyWindow, latencyLogSec,
                autoReconnectEnabled, reconnectIntervalSec,
                heartbeatEnabled, heartbeatIntervalSec);

            // ----- UserState 読込 -----
            LoadedUserState = UserStateService.Load();
            Log.Information(
                "UserState loaded: Speed={Speed}ms, Color=({R},{G},{B}), Target={Target}, Channel={Ch}, Power={Pwr}",
                LoadedUserState.SpeedValueMs,
                LoadedUserState.ColorR, LoadedUserState.ColorG, LoadedUserState.ColorB,
                LoadedUserState.SelectedTarget,
                LoadedUserState.TransmitterChannel, LoadedUserState.TransmitterPower);

            // ----- API プロセス自動起動 -----
            if (LightingMode == "Real")
            {
                _apiExePath = config["Lighting:ApiExePath"] ?? "";
                // 相対パスの場合はアプリ基準ディレクトリからの相対として解決
                if (!string.IsNullOrEmpty(_apiExePath) && !Path.IsPathRooted(_apiExePath))
                {
                    _apiExePath = Path.GetFullPath(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _apiExePath));
                }
                if (!string.IsNullOrEmpty(_apiExePath) && File.Exists(_apiExePath))
                {
                    try
                    {
                        _apiProcess = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = _apiExePath,
                                WorkingDirectory = Path.GetDirectoryName(_apiExePath)!,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            }
                        };
                        _apiProcess.Start();
                        Log.Information("API プロセス自動起動: PID={Pid}, Path={Path}", _apiProcess.Id, _apiExePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "API プロセスの起動に失敗: {Path}", _apiExePath);
                    }
                }
                else if (!string.IsNullOrEmpty(_apiExePath))
                {
                    Log.Warning("API 実行ファイルが見つかりません: {Path}", _apiExePath);
                }
            }

            // ----- Facade 生成 -----
            var latencyInjected = latencyEnabled ? LatencyTracker : null;

            // sendIntervalMs は API サーバ内部の TxWorkerService が使う値（API 側 appsettings で設定）
            LightingFacade = LightingMode switch
            {
                "Real" => new ApiLightingFacade(
                    queueCapacity: queueCapacity,
                    baseUrl: apiBaseUrl,                  // ← sendIntervalMs から変更
                    queuePolicy: queuePolicy,
                    dropThreshold: dropThreshold,
                    latency: latencyInjected),
                _ => new DummyLightingFacade(
                    sendDelayMs: dummySendDelayMs,
                    queueCapacity: queueCapacity,
                    queuePolicy: queuePolicy,
                    dropThreshold: dropThreshold,
                    latency: latencyInjected)
            };

            // ----- KPI 定期ログ -----
            if (latencyEnabled && latencyLogSec > 0)
            {
                _kpiTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(latencyLogSec)
                };
                _kpiTimer.Tick += KpiTimer_Tick;
                _kpiTimer.Start();
            }

            // ----- 自動再接続タイマー-----
            if (autoReconnectEnabled && reconnectIntervalSec > 0)
            {
                _reconnectTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(reconnectIntervalSec)
                };
                _reconnectTimer.Tick += ReconnectTimer_Tick;
                _reconnectTimer.Start();
            }
            // ----- ハートビートタイマー-----
            if (heartbeatEnabled && heartbeatIntervalSec > 0)
            {
                _heartbeatTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(heartbeatIntervalSec)
                };
                _heartbeatTimer.Tick += HeartbeatTimer_Tick;
                _heartbeatTimer.Start();
                Log.Information("Heartbeat enabled (interval={Interval}s)", heartbeatIntervalSec);
            }
            // ----- KeepAlive タイマー -----
            if (keepAliveEnabled && keepAliveIntervalMs > 0)
            {
                _keepAliveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(keepAliveIntervalMs)
                };
                _keepAliveTimer.Tick += KeepAliveTimer_Tick;
                _keepAliveTimer.Start();
                Log.Information("KeepAlive enabled (interval={Interval}ms)", keepAliveIntervalMs);
            }
            // ----- セッション終了時の保険 -----
            SessionEnding += (sender, args) =>
            {
                Log.Information("SessionEnding detected: {Reason}", args.ReasonSessionEnding);
                SaveUserStateSafely();
            };

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("OnExit called");

            // タイマー停止
            try { _kpiTimer?.Stop(); } catch { /* ignore */ }
            try { _reconnectTimer?.Stop(); } catch { /* ignore */ }
            try { _heartbeatTimer?.Stop(); } catch { /* ignore */ }
            try { _keepAliveTimer?.Stop(); } catch { /* ignore */ }

            // UserState 保存
            SaveUserStateSafely();

            // KPI 最終出力
            LogDroppedCount();
            LogReconnectCount();
            LogLatencyStats(final: true);
            LogMisOpCount();

            // Facade 破棄
            if (LightingFacade is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                    Log.Information("LightingFacade disposed.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Dispose failed");
                }
            }
            LightingFacade = null;

            // API プロセス停止
            if (_apiProcess != null)
            {
                try
                {
                    if (!_apiProcess.HasExited)
                    {
                        _apiProcess.Kill(entireProcessTree: true);
                        Log.Information("API プロセスを停止しました (PID={Pid})", _apiProcess.Id);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "API プロセス停止中にエラー");
                }
                _apiProcess.Dispose();
                _apiProcess = null;
            }

            // Mutex 解放
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;

            Log.Information("=== アプリ終了 ===");
            Log.CloseAndFlush();

            base.OnExit(e);
        }

        #region タイマーハンドラ

        private void KpiTimer_Tick(object? sender, EventArgs e)
        {
            LogLatencyStats(final: false);
        }

        /// <summary>
        /// 再接続タイマー発火ハンドラ
        /// 概要：重複実行を防ぐため _reconnectInFlight フラグで排他制御。
        ///       dynamic で TryReconnectMissingPortsAsync を呼び出す（ILightingFacade 外メソッド）。
        /// </summary>
        private async void ReconnectTimer_Tick(object? sender, EventArgs e)
        {
            if (_reconnectInFlight) return;
            _reconnectInFlight = true;

            try
            {
                // API プロセスが落ちていたら自動再起動
                RestartApiProcessIfDead();

                dynamic? facade = LightingFacade;
                if (facade == null) return;

                // 動的ディスパッチで Dummy / Api 両対応
                int reconnected = await facade.TryReconnectMissingPortsAsync();
                if (reconnected > 0)
                {
                    Log.Information("KPI: Auto-reconnect succeeded ({N} port(s))", reconnected);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auto-reconnect error");
            }
            finally
            {
                _reconnectInFlight = false;
            }
        }

        /// <summary>
        /// API プロセスが終了していたら自動で再起動する
        /// </summary>
        private void RestartApiProcessIfDead()
        {
            if (string.IsNullOrEmpty(_apiExePath) || !File.Exists(_apiExePath)) return;
            if (_apiProcess != null && !_apiProcess.HasExited) return;

            // 前のプロセスを破棄
            _apiProcess?.Dispose();
            _apiProcess = null;

            try
            {
                _apiProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _apiExePath,
                        WorkingDirectory = Path.GetDirectoryName(_apiExePath)!,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                _apiProcess.Start();
                Log.Warning("API プロセスが停止していたため自動再起動しました: PID={Pid}", _apiProcess.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "API プロセスの自動再起動に失敗");
            }
        }

        /// <summary>
        /// ハートビートタイマー発火ハンドラ
        /// 概要：接続状態・キュー長・エラーを定期的にログに記録する。
        ///       武道館本番中に「いつ何が起きたか」を事後解析するための
        ///       生存確認ログとして機能する。
        /// </summary>
        private void HeartbeatTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var facade = LightingFacade;
                if (facade == null)
                {
                    Log.Information("Heartbeat: facade=null");
                    return;
                }

                Log.Information(
                    "Heartbeat: Connected={Connected}, Ports=[{Ports}], QueueLength={Queue}, LastError={Err}",
                    facade.IsConnected,
                    string.Join(",", facade.ConnectedPorts),
                    facade.QueueLength,
                    facade.LastError ?? "-");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Heartbeat tick error");
            }
        }

        /// <summary>
        /// KeepAlive タイマー Tick：最後に送信した色を再送して SNO 端末の
        /// セルフモード復帰（信号断 1〜3 秒）を防止する。
        /// </summary>
        private async void KeepAliveTimer_Tick(object? sender, EventArgs e)
        {
            if (_keepAliveInFlight) return;
            _keepAliveInFlight = true;
            try
            {
                dynamic? facade = LightingFacade;
                if (facade == null) return;
                await facade.SendKeepAliveAsync(CancellationToken.None);
            }
            catch { /* KeepAlive 失敗はアクション不要 */ }
            finally
            {
                _keepAliveInFlight = false;
            }
        }

        #endregion タイマーハンドラ

        #region KPI ログ出力

        private static void LogLatencyStats(bool final)
        {
            try
            {
                var snap = LatencyTracker.Snapshot();
                if (!final && snap.Count == 0) return;

                if (final)
                {
                    Log.Information(
                        "KPI Final: Latency {Stats}, TotalSamples={Total}, MaxEver={MaxEver}ms",
                        snap, LatencyTracker.TotalCount, LatencyTracker.MaxEverMs);
                }
                else
                {
                    Log.Information("KPI: Latency {Stats}", snap);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "LatencyStats log failed");
            }
        }

        /// <summary>
        /// ドロップ累計をログに出力する
        /// </summary>
        private static void LogDroppedCount()
        {
            try
            {
                dynamic? facade = LightingFacade;
                if (facade == null) return;
                long dropped = facade.DroppedCount;
                Log.Information("KPI: TotalDropped={Dropped}", dropped);
            }
            catch
            {
                // DroppedCount プロパティが無い実装では無視
            }
        }

        /// <summary>
        /// 再接続累計をログに出力する
        /// </summary>
        private static void LogReconnectCount()
        {
            try
            {
                dynamic? facade = LightingFacade;
                if (facade == null) return;
                long rc = facade.ReconnectCount;
                Log.Information("KPI: TotalReconnect={Reconnect}", rc);
            }
            catch
            {
                // ReconnectCount プロパティが無い実装では無視
            }
        }

        /// <summary>
        /// 操作ミス累計をログに出力する
        /// </summary>
        private static void LogMisOpCount()
        {
            try
            {
                Log.Information("KPI: MisOperation {Stats}", MisOpTracker.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MisOp log failed");
            }
        }
        #endregion KPI ログ出力

        #region UserState / Config

        private static void SaveUserStateSafely()
        {
            try
            {
                var mvm = MainVm;
                Lib.Ui.Screens.ViewModels.SequenceEditorViewModel? svm = null;
                if (Current?.MainWindow?.DataContext is IntegratedWindowDataContext ctx)
                {
                    // IntegratedWindow のコンポジション DataContext から取得
                    mvm ??= ctx.CommandVm;
                    svm = ctx.SequenceVm;
                }
                else
                {
                    mvm ??= Current?.MainWindow?.DataContext as MainWindowViewModel;
                }

                if (mvm == null)
                {
                    Log.Warning("UserState: MainWindowViewModel 参照取得失敗、保存スキップ");
                    return;
                }

                var currentState = mvm.CaptureUserState();
                // Rainbow パネル設定（SequenceEditorViewModel 側）を書き出してから保存（OnExit の再保存で上書き消失を防ぐ）
                svm?.SaveRainbowSettings(currentState);
                UserStateService.Save(currentState);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "UserState 保存中に例外");
            }
        }

        /// <summary>
        /// appsettings.json を読み込む
        /// </summary>
        private static IConfiguration LoadConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            return builder.Build();
        }

        #endregion UserState / Config
    }
}
