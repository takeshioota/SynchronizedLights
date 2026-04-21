using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Lib.Application.Facades;
using Lib.Application.Interfaces;

namespace SynchronizedLights.UI
{
    /// <summary>
    /// アプリケーション本体
    /// 概要：起動時に appsettings.json を読み込み、
    /// Lighting:Mode に応じて DummyLightingFacade か ApiLightingFacade を生成する。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>アプリ全体で共有するライティング制御ファセード</summary>
        public static ILightingFacade? LightingFacade { get; private set; }

        /// <summary>現在の動作モード（"Dummy" or "Real"）</summary>
        public static string LightingMode { get; private set; } = "Dummy";

        protected override void OnStartup(StartupEventArgs e)
        {
            var config = LoadConfiguration();
            LightingMode = config["Lighting:Mode"] ?? "Dummy";
            var queueCapacity = int.TryParse(config["Lighting:SendQueueCapacity"], out var q) ? q : 256;
            var sendIntervalMs = int.TryParse(config["Lighting:SendIntervalMs"], out var s) ? s : 5;

            System.Diagnostics.Debug.WriteLine(
                $"[App] LightingMode={LightingMode}, QueueCapacity={queueCapacity}, SendIntervalMs={sendIntervalMs}");

            LightingFacade = LightingMode switch
            {
                "Real" => new ApiLightingFacade(queueCapacity, sendIntervalMs),
                _ => new DummyLightingFacade()
            };

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (LightingFacade is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            LightingFacade = null;
            base.OnExit(e);
        }

        private static IConfiguration LoadConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            return builder.Build();
        }
    }
}