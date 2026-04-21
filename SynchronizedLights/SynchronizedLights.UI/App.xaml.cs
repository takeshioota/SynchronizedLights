using System;
using System.IO;
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

        /// <summary>KPI 定期ログ出力タイマー</summary>
        private DispatcherTimer? _kpiTimer;

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

            // Dummy 用シミュレート送信遅延
            var dummySendDelayMs = int.TryParse(
                config["Lighting:DummySendDelayMs"], out var d) ? d : 100;

            // 遅延計測
            var latencyWindow = int.TryParse(
                config["Lighting:LatencyWindowSize"], out var lw) ? lw : 1000;
            var latencyLogSec = int.TryParse(
                config["Lighting:LatencyLogIntervalSec"], out var li) ? li : 30;
            var latencyEnabled = !string.Equals(
                config["Lighting:LatencyEnabled"], "false", StringComparison.OrdinalIgnoreCase);

            // 循環バッファを設定値のサイズで再作成
            LatencyTracker = new LatencyTracker(windowSize: latencyWindow);

            Log.Information(
                "LightingMode={Mode}, QueueCapacity={Queue}, SendIntervalMs={Interval}, " +
                "QueuePolicy={Policy}, DropThreshold={Thr}, DummySendDelayMs={Delay}, " +
                "LatencyEnabled={LatEn}, LatencyWindow={LatWin}, LatencyLogIntervalSec={LatInt}",
                LightingMode, queueCapacity, sendIntervalMs,
                queuePolicy, dropThreshold, dummySendDelayMs,
                latencyEnabled, latencyWindow, latencyLogSec);

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

            // KPI タイマー停止
            try { _kpiTimer?.Stop(); } catch { /* ignore */ }

            // UserState 保存
            SaveUserStateSafely();

            // KPI 最終出力（ドロップ・遅延）
            LogDroppedCount();
            LogLatencyStats(final: true);

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

        /// <summary>
        /// 定期 KPI ログ出力タイマーのハンドラ
        /// </summary>
        private void KpiTimer_Tick(object? sender, EventArgs e)
        {
            LogLatencyStats(final: false);
        }

        /// <summary>
        /// 遅延統計をログに出力する
        /// </summary>
        /// <param name="final">true: 終了時（必ず出す）、false: 定期（サンプル無ければ出さない）</param>
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
                // DroppedCount プロパティが無い実装でも無視
            }
        }

        /// <summary>
        /// UserState を安全に保存する
        /// </summary>
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
    }
}