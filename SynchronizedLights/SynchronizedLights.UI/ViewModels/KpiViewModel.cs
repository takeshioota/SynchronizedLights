using System;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lib.Application.Facades;
using Lib.Application.Services;

namespace SynchronizedLights.UI.ViewModels
{
    /// <summary>
    /// KPI バー用 ViewModel
    /// 概要：App.LightingFacade / App.LatencyTracker を 1 秒おきにポーリングし、
    ///       Queue 長／Dropped 数／Reconnect 数／Latency (P95)／送信成功率 を表示する。
    ///       しきい値に応じて色を変え、武道館本番でオペレータが目視で異常に気付けるようにする。
    /// </summary>
    public partial class KpiViewModel : ObservableObject, IDisposable
    {
        #region ブラシ（色）の定義

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // 緑
        private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); // 黄
        private static readonly Brush CriticalBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)); // 赤
        private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)); // 白

        static KpiViewModel()
        {
            OkBrush.Freeze();
            WarningBrush.Freeze();
            CriticalBrush.Freeze();
            NeutralBrush.Freeze();
        }

        #endregion ブラシ

        #region フィールド

        private readonly DispatcherTimer _timer;
        private readonly int _queueCapacity;

        #endregion フィールド

        #region プロパティ（表示文字列 + ブラシ）

        [ObservableProperty] private string queueText = "0";
        [ObservableProperty] private string droppedText = "0";
        [ObservableProperty] private string reconnectText = "0";
        [ObservableProperty] private string latencyText = "P95: -- ms";
        [ObservableProperty] private string successRateText = "100.0 %";
        [ObservableProperty] private string totalSentText = "0";

        [ObservableProperty] private Brush queueBrush = NeutralBrush;
        [ObservableProperty] private Brush droppedBrush = NeutralBrush;
        [ObservableProperty] private Brush reconnectBrush = NeutralBrush;
        [ObservableProperty] private Brush latencyBrush = NeutralBrush;
        [ObservableProperty] private Brush successRateBrush = NeutralBrush;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// KpiViewModel を生成する
        /// </summary>
        /// <param name="pollIntervalMs">ポーリング間隔（ms、既定 1000）</param>
        /// <param name="queueCapacity">キュー容量（使用率計算用、既定 256）</param>
        public KpiViewModel(int pollIntervalMs = 1000, int queueCapacity = 256)
        {
            _queueCapacity = Math.Max(1, queueCapacity);

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(100, pollIntervalMs))
            };
            _timer.Tick += (_, _) => Poll();
            _timer.Start();

            // 即時 1 回反映
            Poll();
        }

        #endregion コンストラクタ

        #region ポーリング

        /// <summary>
        /// App.LightingFacade / App.LatencyTracker から値を取得し、表示プロパティを更新する
        /// </summary>
        private void Poll()
        {
            try
            {
                PollQueue();
                PollDroppedAndReconnect();
                PollLatencyAndSuccess();
            }
            catch (Exception ex)
            {
                // ポーリング中の例外は飲み込む（UI は壊さない）
                System.Diagnostics.Debug.WriteLine($"[KpiViewModel] Poll error: {ex.Message}");
            }
        }

        private void PollQueue()
        {
            var facade = App.LightingFacade;
            if (facade == null)
            {
                QueueText = "--";
                QueueBrush = NeutralBrush;
                return;
            }

            var qlen = facade.QueueLength;
            QueueText = qlen.ToString();

            // Queue 使用率 50% 未満=緑、50-80%=黄、80%以上=赤
            var ratio = (double)qlen / _queueCapacity;
            QueueBrush = ratio >= 0.80 ? CriticalBrush
                       : ratio >= 0.50 ? WarningBrush
                       : OkBrush;
        }

        private void PollDroppedAndReconnect()
        {
            long dropped = 0;
            long reconnect = 0;

            try
            {
                // dynamic ディスパッチ（Dummy / Api 両対応）
                dynamic? facade = App.LightingFacade;
                if (facade != null)
                {
                    dropped = (long)facade.DroppedCount;
                    reconnect = (long)facade.ReconnectCount;
                }
            }
            catch
            {
                // プロパティが存在しない旧実装への互換性
            }

            DroppedText = dropped.ToString();
            DroppedBrush = dropped == 0 ? OkBrush
                          : dropped <= 10 ? WarningBrush
                          : CriticalBrush;

            ReconnectText = reconnect.ToString();
            ReconnectBrush = reconnect == 0 ? OkBrush
                            : reconnect <= 2 ? WarningBrush
                            : CriticalBrush;
        }

        private void PollLatencyAndSuccess()
        {
            var tracker = App.LatencyTracker;
            if (tracker == null)
            {
                LatencyText = "P95: -- ms";
                LatencyBrush = NeutralBrush;
                SuccessRateText = "-- %";
                SuccessRateBrush = NeutralBrush;
                TotalSentText = "0";
                return;
            }

            var snap = tracker.Snapshot();
            var totalSent = tracker.TotalCount;

            if (snap.Count == 0)
            {
                LatencyText = "P95: -- ms";
                LatencyBrush = NeutralBrush;
            }
            else
            {
                LatencyText = $"P95: {snap.P95} ms";
                // P95 < 100ms=緑、100-300=黄、>300=赤
                LatencyBrush = snap.P95 < 100 ? OkBrush
                             : snap.P95 < 300 ? WarningBrush
                             : CriticalBrush;
            }

            TotalSentText = totalSent.ToString();

            // 成功率 = TotalSent / (TotalSent + Dropped)
            long dropped = 0;
            try
            {
                dynamic? facade = App.LightingFacade;
                if (facade != null) dropped = (long)facade.DroppedCount;
            }
            catch { /* ignore */ }

            var attempted = totalSent + dropped;
            if (attempted <= 0)
            {
                SuccessRateText = "100.0 %";
                SuccessRateBrush = OkBrush;
            }
            else
            {
                var rate = (double)totalSent / attempted * 100.0;
                SuccessRateText = $"{rate:F1} %";
                SuccessRateBrush = rate >= 99.0 ? OkBrush
                                 : rate >= 95.0 ? WarningBrush
                                 : CriticalBrush;
            }
        }

        #endregion ポーリング

        #region Dispose

        public void Dispose()
        {
            try
            {
                _timer.Stop();
            }
            catch
            {
                // ignore
            }
            GC.SuppressFinalize(this);
        }

        #endregion Dispose
    }
}