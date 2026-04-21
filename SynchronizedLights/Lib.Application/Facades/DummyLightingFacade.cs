using Lib.Application.Interfaces;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;
using System.Diagnostics;

namespace Lib.Application.Facades
{
    /// <summary>
    /// Dummy実装
    /// 概要：SynchrolightAPIに接続する前の開発用実装。
    ///       QueuePolicy（Wait / DropNewest）に応じて満杯時の挙動を変える。
    ///       Dummy モードでは SendDelayMs を長くしてキュー肥大をシミュレートできる。
    /// </summary>
    public class DummyLightingFacade : ILightingFacade
    {
        #region フィールド

        private readonly List<string> _connectedPorts = new();
        private string? _lastError;
        private int _queueLength;
        private long _droppedCount;

        /// <summary>送信遅延（ms）シミュレート用。大きくするとキュー肥大観察可能</summary>
        private readonly int _sendDelayMs;

        /// <summary>最大キュー容量</summary>
        private readonly int _queueCapacity;

        /// <summary>キュー満杯ポリシー ("Wait" or "DropNewest")</summary>
        private readonly string _queuePolicy;

        /// <summary>ドロップ判定しきい値（0.0〜1.0、0.95 なら 95% で Drop）</summary>
        private readonly double _dropThreshold;

        /// <summary>遅延計測（nullable：注入されなければ計測しない）</summary>
        private readonly LatencyTracker? _latency;

        #endregion フィールド

        #region プロパティ (ILightingFacade)

        public bool IsConnected => _connectedPorts.Count > 0;
        public IReadOnlyList<string> ConnectedPorts => _connectedPorts.AsReadOnly();
        public int QueueLength => _queueLength;
        public string? LastError => _lastError;

        /// <summary>累積ドロップ回数</summary>
        public long DroppedCount => _droppedCount;

        /// <summary>遅延トラッカー</summary>
        public LatencyTracker? Latency => _latency;

        public event EventHandler? StatusChanged;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// DummyLightingFacade を生成する
        /// </summary>
        /// <param name="sendDelayMs">送信1件あたりのシミュレート遅延（既定 100ms）</param>
        /// <param name="queueCapacity">シミュレートキュー容量（既定 256）</param>
        /// <param name="queuePolicy">"Wait" or "DropNewest"（既定 Wait）</param>
        /// <param name="dropThreshold">ドロップ判定の使用率閾値（既定 0.95）</param>
        /// <param name="latency">遅延計測トラッカー（任意）</param>
        public DummyLightingFacade(
            int sendDelayMs = 100,
            int queueCapacity = 256,
            string queuePolicy = "Wait",
            double dropThreshold = 0.95,
            LatencyTracker? latency = null)
        {
            _sendDelayMs = sendDelayMs;
            _queueCapacity = queueCapacity;
            _queuePolicy = queuePolicy;
            _dropThreshold = dropThreshold;
            _latency = latency;

            Log.Information(
                "[Dummy] DummyLightingFacade initialized (delay={Delay}ms, capacity={Cap}, policy={Policy}, threshold={Thr}, latency={HasLat})",
                _sendDelayMs, _queueCapacity, _queuePolicy, _dropThreshold, _latency != null);
        }

        #endregion コンストラクタ

        #region ポリシー判定

        /// <summary>
        /// 現在のキュー状況とポリシーから、コマンドをドロップすべきか判定する
        /// </summary>
        private bool ShouldDropCommand(string commandName)
        {
            if (!string.Equals(_queuePolicy, "DropNewest", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var threshold = (int)(_queueCapacity * _dropThreshold);
            if (_queueLength >= threshold)
            {
                _droppedCount++;
                Log.Warning(
                    "[Dummy] Queue full ({Length}/{Capacity}), dropping {Command}. TotalDropped={Total}",
                    _queueLength, _queueCapacity, commandName, _droppedCount);
                _lastError = $"送信キュー満杯：{commandName} コマンドをドロップ（累積 {_droppedCount} 件）";
                RaiseStatusChanged();
                return true;
            }
            return false;
        }

        #endregion ポリシー判定

        #region 接続管理

        public Task<IReadOnlyList<string>> GetAvailablePortsAsync()
        {
            IReadOnlyList<string> ports = new[] { "COM3 (Dummy)", "COM4 (Dummy)", "COM5 (Dummy)" };
            Log.Information("[Dummy] GetAvailablePorts: {Ports}", string.Join(", ", ports));
            return Task.FromResult(ports);
        }

        public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
        {
            _connectedPorts.Clear();
            _connectedPorts.AddRange(portNames);
            _lastError = null;
            Log.Information("[Dummy] Connect: {Ports}", string.Join(", ", _connectedPorts));
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _connectedPorts.Clear();
            Log.Information("[Dummy] Disconnect: all ports closed");
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        public Task InitializeTransmitterAsync(byte channel, byte power, CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                _lastError = "発信機未接続 (ポートが接続されていません)";
                Log.Warning("[Dummy] InitializeTransmitter failed: {Err}", _lastError);
                RaiseStatusChanged();
                throw new InvalidOperationException(_lastError);
            }

            Log.Information("[Dummy] InitializeTransmitter: channel={Channel}, power={Power}", channel, power);
            _lastError = null;
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        public void ClearError()
        {
            _lastError = null;
            RaiseStatusChanged();
        }

        #endregion 接続管理

        #region 即時制御

        public Task SetColorAsync(Target target, Rgb color, CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                _lastError = $"送信失敗: 未接続状態で SetColor が呼ばれました";
                RaiseStatusChanged();
                throw new InvalidOperationException(_lastError);
            }

            if (ShouldDropCommand("SetColor")) return Task.CompletedTask;

            Log.Information("[Dummy] SetColor target={Target} color=({R},{G},{B})",
                target, color.R, color.G, color.B);
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task FlashAsync(Target target, int speedMs, Rgb color, CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                _lastError = $"送信失敗: 未接続状態で Flash が呼ばれました";
                RaiseStatusChanged();
                throw new InvalidOperationException(_lastError);
            }

            if (ShouldDropCommand("Flash")) return Task.CompletedTask;

            Log.Information("[Dummy] Flash target={Target} speed={Speed}ms color=({R},{G},{B})",
                target, speedMs, color.R, color.G, color.B);
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task FadeInAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                _lastError = $"送信失敗: 未接続状態で FadeIn が呼ばれました";
                RaiseStatusChanged();
                throw new InvalidOperationException(_lastError);
            }

            if (ShouldDropCommand("FadeIn")) return Task.CompletedTask;

            Log.Information("[Dummy] FadeIn target={Target} time={Time}ms color=({R},{G},{B})",
                target, timeMs, color.R, color.G, color.B);
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task FadeOutAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                _lastError = $"送信失敗: 未接続状態で FadeOut が呼ばれました";
                RaiseStatusChanged();
                throw new InvalidOperationException(_lastError);
            }

            if (ShouldDropCommand("FadeOut")) return Task.CompletedTask;

            Log.Information("[Dummy] FadeOut target={Target} time={Time}ms color=({R},{G},{B})",
                target, timeMs, color.R, color.G, color.B);
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default)
        {
            if (ShouldDropCommand("Sequence")) return Task.CompletedTask;

            Log.Information("[Dummy] Sequence target={Target} id={Id}", target, sequenceId);
            SimulateSend();
            return Task.CompletedTask;
        }

        #endregion 即時制御

        #region 内部処理

        /// <summary>
        /// 送信シミュレート：キュー長を増減させ、SendDelayMs 後に減る
        /// </summary>
        private void SimulateSend()
        {
            var startTs = Stopwatch.GetTimestamp();
            _queueLength++;
            RaiseStatusChanged();

            Task.Run(async () =>
            {
                await Task.Delay(_sendDelayMs);
                _queueLength = Math.Max(0, _queueLength - 1);

                // 遅延計測（注入時のみ）
                if (_latency != null)
                {
                    var elapsedMs = (long)Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
                    _latency.Record(elapsedMs);
                }

                RaiseStatusChanged();
            });
        }

        private void RaiseStatusChanged()
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion 内部処理
    }
}