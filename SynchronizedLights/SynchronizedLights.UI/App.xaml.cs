using System;
using System.IO;
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

        /// <summary>UserState 管理サービス</summary>
        public static UserStateService UserStateService { get; private set; } = new UserStateService();

        /// <summary>起動時に読み込んだ UserState</summary>
        public static UserState? LoadedUserState { get; private set; }

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

        protected override void OnStartup(StartupEventArgs e)
        {
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
            var queueCapacity = int.TryParse(config["Lighting:SendQueueCapacity"], out var q) ? q : 256;
            var sendIntervalMs = int.TryParse(config["Lighting:SendIntervalMs"], out var s) ? s : 5;

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
            // 循環バッファを設定値のサイズで再作成
            LatencyTracker = new LatencyTracker(windowSize: latencyWindow);

            Log.Information(
                "LightingMode={Mode}, QueueCapacity={Queue}, SendIntervalMs={Interval}, " +
                "QueuePolicy={Policy}, DropThreshold={Thr}, DummySendDelayMs={Delay}, " +
                "LatencyEnabled={LatEn}, LatencyWindow={LatWin}, LatencyLogIntervalSec={LatInt}, " +
                "AutoReconnectEnabled={RcEn}, ReconnectIntervalSec={RcInt}, " +
                "HeartbeatEnabled={HbEn}, HeartbeatIntervalSec={HbInt}",
                LightingMode, queueCapacity, sendIntervalMs,
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

            // ----- Facade 生成 -----
            var latencyInjected = latencyEnabled ? LatencyTracker : null;

            LightingFacade = LightingMode switch
            {
                "Real" => new ApiLightingFacade(
                    queueCapacity: queueCapacity,
                    sendIntervalMs: sendIntervalMs,
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
        /// 
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
                if (mvm == null)
                {
                    mvm = Current?.MainWindow?.DataContext as MainWindowViewModel;
                }

                if (mvm == null)
                {
                    Log.Warning("UserState: MainWindowViewModel 参照取得失敗、保存スキップ");
                    return;
                }

                var currentState = mvm.CaptureUserState();
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