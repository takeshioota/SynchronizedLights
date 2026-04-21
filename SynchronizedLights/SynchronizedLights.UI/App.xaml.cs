using Lib.Application.Facades;
using Lib.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.IO;
using System.Windows;
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
        /// UserState 管理サービス
        /// </summary>
        public static UserStateService UserStateService { get; private set; } = new UserStateService();

        /// <summary>
        /// 起動時に読み込んだ UserState
        /// </summary>
        public static UserState? LoadedUserState { get; private set; }

        /// <summary>
        /// MainWindowViewModel への参照
        /// 概要：OnExit 時に MainWindow が破棄されていても確実に CaptureUserState を
        /// 呼び出せるよう、静的プロパティとして保持する。
        /// MainWindow コンストラクタから設定される。
        /// </summary>
        public static MainWindowViewModel? MainVm { get; set; }

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

            Log.Information(
                "LightingMode={Mode}, QueueCapacity={Queue}, SendIntervalMs={Interval}, " +
                "QueuePolicy={Policy}, DropThreshold={Thr}, DummySendDelayMs={Delay}",
                LightingMode, queueCapacity, sendIntervalMs,
                queuePolicy, dropThreshold, dummySendDelayMs);

            // ----- UserState 読込 -----
            LoadedUserState = UserStateService.Load();
            Log.Information(
                "UserState loaded: Speed={Speed}ms, Color=({R},{G},{B}), Target={Target}, Channel={Ch}, Power={Pwr}",
                LoadedUserState.SpeedValueMs,
                LoadedUserState.ColorR, LoadedUserState.ColorG, LoadedUserState.ColorB,
                LoadedUserState.SelectedTarget,
                LoadedUserState.TransmitterChannel, LoadedUserState.TransmitterPower);

            // ----- Facade 生成 -----
            LightingFacade = LightingMode switch
            {
                "Real" => new ApiLightingFacade(
                    queueCapacity: queueCapacity,
                    sendIntervalMs: sendIntervalMs,
                    queuePolicy: queuePolicy,
                    dropThreshold: dropThreshold),
                _ => new DummyLightingFacade(
                    sendDelayMs: dummySendDelayMs,
                    queueCapacity: queueCapacity,
                    queuePolicy: queuePolicy,
                    dropThreshold: dropThreshold)
            };

            // ----- セッション終了時-----
            SessionEnding += (sender, args) =>
            {
                Log.Information("SessionEnding detected: {Reason}", args.ReasonSessionEnding);
                SaveUserStateSafely();
            };

            // ----- ベース処理（MainWindow 起動） -----
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("OnExit called");

            // ----- UserState 保存 -----
            SaveUserStateSafely();

            // ----- ドロップ累積の最終ログ出力-----
            LogDroppedCount();

            // ----- Facade 破棄 -----
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
        /// ドロップ累計をログに出力する
        /// 概要：Dummy / Api どちらの実装でも DroppedCount プロパティがあれば値を出力する。
        ///       ILightingFacade のインタフェース追加を避けるため dynamic で参照。
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
                // 優先：静的参照の MainVm
                var mvm = MainVm;

                // フォールバック：MainWindow の DataContext
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