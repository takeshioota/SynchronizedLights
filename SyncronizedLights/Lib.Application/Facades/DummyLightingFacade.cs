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

        public Task SetReceiverChannelAsync(byte channel, CancellationToken ct = default)
        {
            Log.Information("[Dummy] SetReceiverChannel (A6): channel={Channel}", channel);
            _lastError = null;
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        public Task SetReceiverChannelColAsync(byte channel, CancellationToken ct = default)
        {
            Log.Information("[Dummy] SetReceiverChannelCol (AD): channel={Channel}", channel);
            _lastError = null;
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        public void ClearError()
        {
            _lastError = null;
            RaiseStatusChanged();
        }

        /// <summary>BUG-20260729-08: Dummy では抑止フラグは動作に影響しないため no-op。</summary>
        public void SetEmergencyHold(bool active)
        {
            Log.Information("[Dummy] SetEmergencyHold: {Active}", active);
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

        /// <summary>色→色スムーズ遷移（Dummy 疑似実装：ログ出力のみ）</summary>
        public Task FadeColorAsync(Rgb from, Rgb to, int durationMs, int fadeSteps = 20, CancellationToken ct = default)
        {
            Log.Information(
                "[Dummy] Fade: ({FR},{FG},{FB})→({TR},{TG},{TB}), duration={Ms}ms, fadeSteps={FS}",
                from.R, from.G, from.B, to.R, to.G, to.B, durationMs, fadeSteps);
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        // 旧 FlashAsync / FadeInAsync / FadeOutAsync / BreathAsync は削除。
        // 理由：API 担当者からの指摘により方式 A（UI 側 HTTP ループ）は廃止し、
        //       方式 B（StartEffectAsync = API 側で 0.2 秒間隔の継続送信）に統一する。
        //       MainWindowViewModel.ToggleEffectAsync は既に StartEffectAsync を使用済み。

        public Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default)
        {
            if (ShouldDropCommand("Sequence")) return Task.CompletedTask;

            Log.Information("[Dummy] Sequence target={Target} id={Id}", target, sequenceId);
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task PlayInternalProgramAsync(uint frameNo, CancellationToken ct = default)
        {
            if (ShouldDropCommand("InternalProgram")) return Task.CompletedTask;

            Log.Information("[Dummy] InternalProgram frame={FrameNo}", frameNo);
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task StopInternalProgramAsync(CancellationToken ct = default)
        {
            if (ShouldDropCommand("InternalProgramStop")) return Task.CompletedTask;

            Log.Information("[Dummy] InternalProgram stopped");
            SimulateSend();
            return Task.CompletedTask;
        }

        // --- レインボー（V4.5: 3.15-3.22） ---

        public Task StartRainbowAsync(
            RainbowMode mode,
            IReadOnlyList<Rgb> colors,
            int cycleDurationMs = 1000,
            int? blinkPeriodMs = null,
            byte? dutyRatio = null,
            int? fadeInMs = null,
            int? fadeOutMs = null,
            CancellationToken ct = default)
        {
            if (ShouldDropCommand("Rainbow")) return Task.CompletedTask;

            Log.Information(
                "[Dummy] Rainbow start: mode={Mode}, colors={N}, cycle={Cycle}ms, " +
                "blink={Blink}, duty={Duty}, fadeIn={FI}, fadeOut={FO}",
                mode, colors.Count, cycleDurationMs,
                blinkPeriodMs?.ToString() ?? "null",
                dutyRatio?.ToString() ?? "null",
                fadeInMs?.ToString() ?? "null",
                fadeOutMs?.ToString() ?? "null");
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task StopRainbowAsync(CancellationToken ct = default)
        {
            if (ShouldDropCommand("RainbowStop")) return Task.CompletedTask;

            Log.Information("[Dummy] Rainbow stopped");
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task PauseRainbowAsync(CancellationToken ct = default)
        {
            if (ShouldDropCommand("RainbowPause")) return Task.CompletedTask;

            Log.Information("[Dummy] Rainbow paused");
            SimulateSend();
            return Task.CompletedTask;
        }

        public Task SendColorTableAsync(
            IReadOnlyList<Rgb> colors,
            int cycleDurationMs = 1000,
            CancellationToken ct = default)
        {
            if (ShouldDropCommand("ColorTable")) return Task.CompletedTask;

            Log.Information("[Dummy] Color table sent: colors={N}, cycle={Cycle}ms",
                colors.Count, cycleDurationMs);
            SimulateSend();
            return Task.CompletedTask;
        }

        /// <summary>KeepAlive（Dummy: 何もしない）</summary>
        public Task SendKeepAliveAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>
        /// Effect 開始（Dummy 疑似実装：ログ出力のみ）
        /// </summary>
        public Task StartEffectAsync(
            string effectType,
            Rgb color,
            int cycleDurationMs = 1000,
            int? flashIntervalMs = null,
            int fadeSteps = 20,
            bool continuous = true,
            CancellationToken ct = default)
        {
            Log.Information(
                "[Dummy] StartEffect: type={Type}, color=({R},{G},{B}), cycle={Cycle}ms, " +
                "flashInterval={FI}, fadeSteps={FS}, continuous={Cont}",
                effectType, color.R, color.G, color.B, cycleDurationMs,
                flashIntervalMs?.ToString() ?? "null", fadeSteps, continuous);
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Effect 停止（Dummy 疑似実装：ログ出力のみ）
        /// </summary>
        public Task StopEffectAsync(CancellationToken ct = default)
        {
            Log.Information("[Dummy] StopEffect");
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 信号送出を完全停止する（Dummy 疑似実装：ログ出力のみ）
        /// </summary>
        public Task StopSignalAsync(CancellationToken ct = default)
        {
            Log.Information("[Dummy] StopSignal: 端末はセルフモードに復帰します");
            _dummyPlayingSequence = null;
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        // Dummy 内で「再生中シーケンス名」を保持して GetSequencePlayStatusAsync で返す
        private string? _dummyPlayingSequence;
        // Dummy 上の「サーバー登録済みシーケンス名」を疑似的に保持
        private readonly System.Collections.Generic.HashSet<string> _dummyRegistered = new();

        /// <summary>
        /// シーケンスを登録/上書き（Dummy 疑似実装：ログのみ）
        /// </summary>
        public Task UpsertSequenceAsync(
            string name,
            IReadOnlyList<Lib.Application.Models.SequenceApiStep> steps,
            CancellationToken ct = default)
        {
            Log.Information("[Dummy] UpsertSequence: name={Name}, steps={Count}", name, steps.Count);
            _dummyRegistered.Add(name);
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        /// <summary>
        /// シーケンス再生開始（Dummy 疑似実装：ログのみ）
        /// </summary>
        public Task PlaySequenceAsync(string name, CancellationToken ct = default)
        {
            Log.Information("[Dummy] PlaySequence: name={Name}", name);
            _dummyPlayingSequence = name;
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        /// <summary>
        /// シーケンス停止（Dummy 疑似実装：ログのみ）
        /// </summary>
        public Task StopSequenceAsync(CancellationToken ct = default)
        {
            Log.Information("[Dummy] StopSequence");
            _dummyPlayingSequence = null;
            RaiseStatusChanged();
            return Task.CompletedTask;
        }

        /// <summary>
        /// シーケンス一時停止（Dummy 疑似実装：ログのみ）
        /// </summary>
        public Task<bool> PauseSequenceAsync(CancellationToken ct = default)
        {
            Log.Information("[Dummy] PauseSequence");
            RaiseStatusChanged();
            return Task.FromResult(_dummyPlayingSequence != null);
        }

        /// <summary>
        /// シーケンス再開（Dummy 疑似実装：ログのみ）
        /// </summary>
        public Task<bool> ResumeSequenceAsync(CancellationToken ct = default)
        {
            Log.Information("[Dummy] ResumeSequence");
            RaiseStatusChanged();
            return Task.FromResult(false);
        }

        /// <summary>
        /// 再生状態取得（Dummy 疑似実装）
        /// </summary>
        public Task<(bool IsPlaying, string? Name)> GetSequencePlayStatusAsync(
            CancellationToken ct = default)
        {
            return Task.FromResult((_dummyPlayingSequence != null, _dummyPlayingSequence));
        }

        /// <summary>
        /// 再生状態取得（詳細版・Dummy 疑似実装）
        /// 概要：再生中フラグ + 名前のみ返し、ステップ位置は常に -1（Dummy はステップ追跡しない）
        /// </summary>
        public Task<(bool IsPlaying, string? Name, int CurrentStepIndex, int TotalStepCount)>
            GetSequencePlayStatusDetailedAsync(CancellationToken ct = default)
        {
            return Task.FromResult((_dummyPlayingSequence != null, _dummyPlayingSequence, -1, 0));
        }

        /// <summary>
        /// 登録済みシーケンス名一覧（Dummy 疑似実装）
        /// </summary>
        public Task<IReadOnlyList<string>> ListSequenceNamesAsync(CancellationToken ct = default)
        {
            IReadOnlyList<string> list = new System.Collections.Generic.List<string>(_dummyRegistered);
            return Task.FromResult(list);
        }

        /// <summary>
        /// シーケンス削除（Dummy 疑似実装）
        /// </summary>
        public Task DeleteSequenceFromApiAsync(string name, CancellationToken ct = default)
        {
            Log.Information("[Dummy] DeleteSequence: name={Name}", name);
            _dummyRegistered.Remove(name);
            if (_dummyPlayingSequence == name) _dummyPlayingSequence = null;
            RaiseStatusChanged();
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

        #region ファイル転送（Dummy）

        public Task WriteFileVia24GAsync(byte[] rgbData, uint frameNo, CancellationToken ct = default)
        {
            Log.Information("[Dummy] FileWrite24G frame={FrameNo}, size={Size}bytes", frameNo, rgbData.Length);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string Address, string Name, int Rssi)>> ScanBleDevicesAsync(
            int timeoutSeconds = 5, CancellationToken ct = default)
        {
            Log.Information("[Dummy] BLE scan ({Timeout}s)", timeoutSeconds);
            var devices = new List<(string, string, int)>
            {
                ("00:00:00:00:00:01", "BLELight-001", -50),
                ("00:00:00:00:00:02", "BLELight-002", -60),
            };
            return Task.FromResult<IReadOnlyList<(string, string, int)>>(devices);
        }

        public Task ConnectBleAsync(string deviceAddress, CancellationToken ct = default)
        {
            Log.Information("[Dummy] BLE connect: {Address}", deviceAddress);
            return Task.CompletedTask;
        }

        public Task DisconnectBleAsync(CancellationToken ct = default)
        {
            Log.Information("[Dummy] BLE disconnect");
            return Task.CompletedTask;
        }

        public Task TransferFileBleAsync(byte[] rgbData, CancellationToken ct = default)
        {
            Log.Information("[Dummy] BLE file transfer: {Size}bytes", rgbData.Length);
            return Task.CompletedTask;
        }

        #endregion ファイル転送（Dummy）
    }
}