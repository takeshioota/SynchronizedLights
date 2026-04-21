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
        /// 起動時に読み込んだ UserState（MainWindow に反映するために保持）
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

            Log.Information(
                "LightingMode={Mode}, QueueCapacity={Queue}, SendIntervalMs={Interval}",
                LightingMode, queueCapacity, sendIntervalMs);

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
                "Real" => new ApiLightingFacade(queueCapacity, sendIntervalMs),
                _ => new DummyLightingFacade()
            };

            // ----- セッション終了時-----
            // Windows ログオフ・シャットダウン時の保存保険
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
        /// UserState を安全に保存する
        /// 概要：MainVm 参照を優先して使い、null の場合は MainWindow.DataContext をフォールバック。
        /// OnExit / SessionEnding のどちらからでも呼び出せる。
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