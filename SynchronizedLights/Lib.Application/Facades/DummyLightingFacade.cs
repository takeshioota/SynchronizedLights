using System.Diagnostics;
using System.Threading;
using Lib.Application.Interfaces;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;

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

        /// <summary>本来接続しておくべきポート一覧</summary>
        private readonly List<string> _intendedPorts = new();

        private string? _lastError;
        private int _queueLength;
        private long _droppedCount;

        /// <summary>累積再接続成功回数（KPI）</summary>
        private long _reconnectCount;

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

        /// <summary>累積自動再接続成功回数（KPI 用）</summary>
        public long ReconnectCount => Interlocked.Read(ref _reconnectCount);

        /// <summary>遅延トラッカー（外部からスナップショットを取るため公開）</summary>
        public LatencyTracker? Latency => _latency;

        /// <summary>本来接続しておくべきポート一覧（デバッグ表示用）</summary>
        public IReadOnlyList<string> IntendedPorts => _intendedPorts.AsReadOnly();

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
            var ports = portNames.ToList();

            // intended と connected を更新
            _intendedPorts.Clear();
            _intendedPorts.AddRange(ports);
            _connectedPorts.Clear();
            _connectedPorts.AddRange(ports);

            _lastError = null;
            Log.Information(
                "[Dummy] Connect: {Ports} (intended saved for auto-reconnect)",
                string.Join(", ", _connectedPorts));
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _connectedPorts.Clear();
            _intendedPorts.Clear();
            Log.Information("[Dummy] Disconnect: all ports closed, intended cleared");
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

        #region 自動再接続

        /// <summary>
        /// 物理切断を擬似的に発生させる（Dummy モード専用、自動再接続の試験用）
        /// 概要：_connectedPorts のみクリアし、_intendedPorts は保持する。
        ///       次回の TryReconnectMissingPortsAsync 呼び出しで復旧する。
        /// </summary>
        public void SimulateDisconnect()
        {
            if (_intendedPorts.Count == 0)
            {
                Log.Information("[Dummy] SimulateDisconnect: no intended ports (no-op)");
                return;
            }

            var lost = string.Join(", ", _connectedPorts);
            _connectedPorts.Clear();
            _lastError = "ポート切断を検知（Dummy シミュレート）";
            Log.Warning("[Dummy] SimulateDisconnect: ports lost [{Ports}]", lost);
            RaiseStatusChanged();
        }

        /// <summary>
        /// intended だが現在 connected でないポートを再接続試行する
        /// 戻り値：再接続に成功したポート数
        /// </summary>
        public Task<int> TryReconnectMissingPortsAsync(CancellationToken ct = default)
        {
            var missing = _intendedPorts
                .Where(p => !_connectedPorts.Contains(p))
                .ToList();

            if (missing.Count == 0)
            {
                return Task.FromResult(0);
            }

            Log.Warning(
                "[Dummy] Missing ports detected: {Ports}. Attempting reconnect...",
                string.Join(", ", missing));

            // Dummy なので必ず成功
            foreach (var p in missing)
            {
                _connectedPorts.Add(p);
            }

            Interlocked.Add(ref _reconnectCount, missing.Count);
            _lastError = null;
            Log.Information(
                "[Dummy] Reconnected {Count} port(s): {Ports}. TotalReconnect={Total}",
                missing.Count, string.Join(", ", missing), ReconnectCount);
            RaiseStatusChanged();

            return Task.FromResult(missing.Count);
        }

        #endregion 自動再接続

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

        public async Task BreathAsync(Target target, int cycleMs, Rgb color, int cycles = 3, CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                _lastError = $"送信失敗: 未接続状態で Breath が呼ばれました";
                RaiseStatusChanged();
                throw new InvalidOperationException(_lastError);
            }

            if (ShouldDropCommand("Breath")) return;

            Log.Information(
                "[Dummy] Breath target={Target} cycle={Cycle}ms cycles={Cycles} color=({R},{G},{B})",
                target, cycleMs, cycles, color.R, color.G, color.B);

            // FadeIn + FadeOut を cycles 回繰り返す（疑似実装）
            var halfMs = Math.Max(50, cycleMs / 2);
            for (var i = 0; i < cycles; i++)
            {
                if (ct.IsCancellationRequested) break;

                // 明るくなる（10ステップ）
                const int steps = 10;
                var stepMs = Math.Max(5, halfMs / steps);
                for (var s = 1; s <= steps; s++)
                {
                    if (ct.IsCancellationRequested) break;
                    SimulateSend();
                    if (s < steps) await Task.Delay(stepMs, ct);
                }

                // 暗くなる（10ステップ）
                for (var s = steps - 1; s >= 0; s--)
                {
                    if (ct.IsCancellationRequested) break;
                    SimulateSend();
                    if (s > 0) await Task.Delay(stepMs, ct);
                }
            }
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