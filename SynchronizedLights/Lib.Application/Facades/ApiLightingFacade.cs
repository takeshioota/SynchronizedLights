using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Lib.Application.Interfaces;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;

// API側型のエイリアス
using ApiRgb = SynchrolightAPI.Domain.Rgb;
using ApiCommandBuilder = SynchrolightAPI.Protocol.CommandBuilder;
using ApiLightingService = SynchrolightAPI.Services.LightingService;
using ApiMultiPortTransport = SynchrolightAPI.Transport.MultiPortTransport;
using ApiTxWorkerService = SynchrolightAPI.Transport.TxWorkerService;

namespace Lib.Application.Facades
{
    /// <summary>
    /// 実API実装
    /// 概要：SynchrolightAPI.Core の LightingService / MultiPortTransport / TxWorkerService を
    ///       統合し、ILightingFacade を提供する。実機2.4GHz発信機制御時に使用。
    ///       QueuePolicy=DropNewest 指定時は、MultiPortTransport の QueueLength が
    ///       閾値を超えた段階で、Transport に送り込む前に Drop する（API 側 BoundedChannel
    ///       に負荷をかけないための前段フィルタ）。
    /// </summary>
    public class ApiLightingFacade : ILightingFacade, IDisposable
    {
        #region フィールド

        private readonly ILoggerFactory _loggerFactory;
        private readonly ApiMultiPortTransport _transport;
        private readonly ApiLightingService _lightingService;
        private readonly ApiTxWorkerService _worker;
        private readonly CancellationTokenSource _workerCts;

        /// <summary>本来接続しておくべきポート一覧</summary>
        private readonly List<string> _intendedPorts = new();
        private readonly object _intendedLock = new();

        private string? _lastError;
        private bool _disposed;

        /// <summary>キュー容量（Drop 判定用に保持）</summary>
        private readonly int _queueCapacity;

        /// <summary>キュー満杯ポリシー ("Wait" or "DropNewest")</summary>
        private readonly string _queuePolicy;

        /// <summary>ドロップ判定しきい値（0.0〜1.0）</summary>
        private readonly double _dropThreshold;

        /// <summary>累積ドロップ回数</summary>
        private long _droppedCount;
        private long _reconnectCount;

        /// <summary>遅延計測（nullable：注入されなければ計測しない）</summary>
        private readonly LatencyTracker? _latency;

        #endregion フィールド

        #region プロパティ (ILightingFacade)

        public bool IsConnected
            => _transport.ListPorts().Any(p => p.IsConnected);

        public IReadOnlyList<string> ConnectedPorts
            => _transport.ListPorts()
                .Where(p => p.IsConnected)
                .Select(p => p.Name)
                .ToList()
                .AsReadOnly();

        public int QueueLength
            => _transport.GetStatus().QueueLength;

        public string? LastError
            => _lastError ?? _transport.GetStatus().LastError;

        /// <summary>累積ドロップ回数（KPI 用）</summary>
        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        /// <summary>累積自動再接続成功回数（KPI 用）</summary>
        public long ReconnectCount => Interlocked.Read(ref _reconnectCount);

        /// <summary>遅延トラッカー</summary>
        public LatencyTracker? Latency => _latency;

        /// <summary>本来接続しておくべきポート一覧（デバッグ表示用）</summary>
        public IReadOnlyList<string> IntendedPorts
        {
            get
            {
                lock (_intendedLock)
                {
                    return _intendedPorts.ToList().AsReadOnly();
                }
            }
        }

        public event EventHandler? StatusChanged;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// ApiLightingFacade を生成する
        /// </summary>
        /// <param name="queueCapacity">送信キュー容量（既定 256）</param>
        /// <param name="sendIntervalMs">送信間隔 ms（既定 5）</param>
        /// <param name="queuePolicy">"Wait" or "DropNewest"（既定 Wait）</param>
        /// <param name="dropThreshold">ドロップ判定の使用率閾値（既定 0.95）</param>
        /// <param name="latency">遅延計測トラッカー（任意）</param>
        public ApiLightingFacade(
            int queueCapacity = 256,
            int sendIntervalMs = 5,
            string queuePolicy = "Wait",
            double dropThreshold = 0.95,
            LatencyTracker? latency = null)
        {
            _queueCapacity = queueCapacity;
            _queuePolicy = queuePolicy;
            _dropThreshold = dropThreshold;
            _latency = latency;

            // Logger
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddDebug();
            });

            // Transport
            _transport = new ApiMultiPortTransport(
                _loggerFactory.CreateLogger<ApiMultiPortTransport>(),
                queueCapacity);

            // Protocol CommandBuilder
            var cmdBuilder = new ApiCommandBuilder();

            // LightingService
            _lightingService = new ApiLightingService(
                cmdBuilder,
                _transport,
                _loggerFactory.CreateLogger<ApiLightingService>());

            // TxWorkerService
            var workerConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SerialPort:SendIntervalMs"] = sendIntervalMs.ToString()
                })
                .Build();

            _worker = new ApiTxWorkerService(
                _transport,
                _loggerFactory.CreateLogger<ApiTxWorkerService>(),
                workerConfig);

            // BackgroundService.StartAsync 内部で ExecuteAsync がタスクとして起動する
            _workerCts = new CancellationTokenSource();
            _ = _worker.StartAsync(_workerCts.Token);

            Log.Information(
                "[Api] ApiLightingFacade initialized (queue={Queue}, interval={Interval}ms, policy={Policy}, threshold={Thr}, latency={HasLat})",
                queueCapacity, sendIntervalMs, queuePolicy, dropThreshold, _latency != null);
        }

        #endregion コンストラクタ

        #region ポリシー判定

        /// <summary>
        /// 現在のキュー状況とポリシーから、コマンドをドロップすべきか判定する
        /// 概要：QueuePolicy が "DropNewest" のときだけ有効。
        ///       Transport の QueueLength が容量 × 閾値を超えていたら Drop して true を返す。
        /// </summary>
        private bool ShouldDropCommand(string commandName)
        {
            if (!string.Equals(_queuePolicy, "DropNewest", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var threshold = (int)(_queueCapacity * _dropThreshold);
            var current = _transport.GetStatus().QueueLength;
            if (current >= threshold)
            {
                Interlocked.Increment(ref _droppedCount);
                Log.Warning(
                    "[Api] Queue full ({Length}/{Capacity}), dropping {Command}. TotalDropped={Total}",
                    current, _queueCapacity, commandName, _droppedCount);
                _lastError = $"送信キュー満杯：{commandName} コマンドをドロップ（累積 {_droppedCount} 件）";
                RaiseStatusChanged();
                return true;
            }
            return false;
        }

        #endregion ポリシー判定

        #region 接続管理 (ILightingFacade)

        public async Task<IReadOnlyList<string>> GetAvailablePortsAsync()
        {
            // OSから実COMポート一覧を取得
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            Array.Sort(ports);
            return await Task.FromResult<IReadOnlyList<string>>(ports);
        }

        public async Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
        {
            var ports = portNames.ToList();

            lock (_intendedLock)
            {
                _intendedPorts.Clear();
                _intendedPorts.AddRange(ports);
            }

            try
            {
                _lastError = null;
                await _transport.ConnectAsync(ports, ct);
                Log.Information(
                    "[Api] Connect: {Ports} (intended saved for auto-reconnect)",
                    string.Join(", ", ports));
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Warning("[Api] Connect failed: {Err}", ex.Message);
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        public async Task DisconnectAsync()
        {
            lock (_intendedLock)
            {
                _intendedPorts.Clear();
            }

            await _transport.DisconnectAsync();
            Log.Information("[Api] Disconnect: all ports closed, intended cleared");
            RaiseStatusChanged();
        }

        public async Task InitializeTransmitterAsync(byte channel, byte power, CancellationToken ct = default)
        {
            try
            {
                _lastError = null;
                await _lightingService.InitializeTransmitterAsync(channel, power, ct);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                throw;
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        public void ClearError()
        {
            _lastError = null;
            RaiseStatusChanged();
        }

        #endregion 接続管理

        #region 自動再接続

        /// <summary>
        /// intended だが現在 connected でないポートを再接続試行する
        /// 概要：MultiPortTransport.ListPorts() で現在の接続状態を取得し、
        ///       intended に含まれるが IsConnected=false または存在しないポートを
        ///       _transport.ConnectAsync() で再接続する。
        /// 戻り値：再接続に成功したポート数（失敗時 0）
        /// </summary>
        public async Task<int> TryReconnectMissingPortsAsync(CancellationToken ct = default)
        {
            List<string> intendedSnapshot;
            lock (_intendedLock)
            {
                if (_intendedPorts.Count == 0) return 0;
                intendedSnapshot = _intendedPorts.ToList();
            }

            // 現在の接続状態を取得（IsConnected=true のものだけ生きてると判定）
            var liveConnected = _transport.ListPorts()
                .Where(p => p.IsConnected)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = intendedSnapshot
                .Where(p => !liveConnected.Contains(p))
                .ToList();

            if (missing.Count == 0) return 0;

            Log.Warning(
                "[Api] Missing ports detected: {Ports}. Attempting reconnect...",
                string.Join(", ", missing));

            try
            {
                await _transport.ConnectAsync(missing, ct);

                // 再接続後にもう一度状態を確認
                var afterLive = _transport.ListPorts()
                    .Where(p => p.IsConnected)
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var reconnected = missing.Count(p => afterLive.Contains(p));
                if (reconnected > 0)
                {
                    Interlocked.Add(ref _reconnectCount, reconnected);
                    _lastError = null;
                    Log.Information(
                        "[Api] Reconnected {Count} port(s). TotalReconnect={Total}",
                        reconnected, ReconnectCount);
                    RaiseStatusChanged();
                }
                else
                {
                    Log.Warning(
                        "[Api] Reconnect attempt returned but no ports came back online");
                }

                return reconnected;
            }
            catch (Exception ex)
            {
                _lastError = $"再接続失敗: {ex.Message}";
                Log.Warning("[Api] Reconnect failed: {Err}", ex.Message);
                RaiseStatusChanged();
                return 0;
            }
        }

        #endregion 自動再接続

        #region 制御 (ILightingFacade)

        public async Task SetColorAsync(Target target, Rgb color, CancellationToken ct = default)
        {
            if (ShouldDropCommand("SetColor")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                await InternalSetColorAsync(target, color, ct);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                throw;
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task FlashAsync(Target target, int speedMs, Rgb color, CancellationToken ct = default)
        {
            if (ShouldDropCommand("Flash")) return;

            var halfMs = Math.Max(50, speedMs / 2);
            var black = new Rgb(0, 0, 0);

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                await InternalSetColorAsync(target, color, ct);
                await Task.Delay(halfMs, ct);
                await InternalSetColorAsync(target, black, ct);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task FadeInAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
        {
            if (ShouldDropCommand("FadeIn")) return;

            const int steps = 10;
            var stepMs = Math.Max(50, timeMs / steps);

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                for (var i = 1; i <= steps; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var t = i / (double)steps;
                    var faded = new Rgb(
                        (byte)(color.R * t),
                        (byte)(color.G * t),
                        (byte)(color.B * t));

                    await InternalSetColorAsync(target, faded, ct);

                    if (i < steps) await Task.Delay(stepMs, ct);
                }
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task FadeOutAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
        {
            if (ShouldDropCommand("FadeOut")) return;

            const int steps = 10;
            var stepMs = Math.Max(50, timeMs / steps);

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                for (var i = steps - 1; i >= 0; i--)
                {
                    if (ct.IsCancellationRequested) break;

                    var t = i / (double)steps;
                    var faded = new Rgb(
                        (byte)(color.R * t),
                        (byte)(color.G * t),
                        (byte)(color.B * t));

                    await InternalSetColorAsync(target, faded, ct);

                    if (i > 0) await Task.Delay(stepMs, ct);
                }
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default)
        {
            if (ShouldDropCommand("Sequence")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                Log.Information(
                    "[Api] Sequence execution pending API support. target={Target}, id={Id}",
                    target, sequenceId);
                await Task.CompletedTask;
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        #endregion 制御

        #region 内部メソッド

        /// <summary>
        /// UI側 Target を API 呼び出しに振り分ける。
        /// ALL → SetGlobalColorAsync (A2)
        /// Group01〜08 → SetRowColorAsync (A3)
        /// </summary>
        private async Task InternalSetColorAsync(Target target, Rgb color, CancellationToken ct)
        {
            var apiColor = ToApiRgb(color);

            if (target == Target.All)
            {
                await _lightingService.SetGlobalColorAsync(apiColor, ct);
            }
            else
            {
                var (startRow, len) = GroupToRowRange(target);
                await _lightingService.SetRowColorAsync(
                    field: 0,
                    startRow: startRow,
                    len: len,
                    color: apiColor,
                    ct: ct);
            }
        }

        /// <summary>
        /// 遅延を LatencyTracker に記録する
        /// </summary>
        private void RecordLatency(long startTimestamp)
        {
            if (_latency == null) return;
            var elapsedMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            _latency.Record(elapsedMs);
        }

        private static ApiRgb ToApiRgb(Rgb color) => new(color.R, color.G, color.B);

        private static (ushort startRow, byte len) GroupToRowRange(Target group)
        {
            return group switch
            {
                Target.Group01 => (1, 25),
                Target.Group02 => (26, 25),
                Target.Group03 => (51, 25),
                Target.Group04 => (76, 25),
                Target.Group05 => (101, 25),
                Target.Group06 => (126, 25),
                Target.Group07 => (151, 25),
                Target.Group08 => (176, 25),
                _ => (1, 25)
            };
        }

        private void RaiseStatusChanged()
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion 内部メソッド

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _workerCts?.Cancel(); } catch { /* ignore */ }
            try { _worker?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { /* ignore */ }
            try { _transport?.Dispose(); } catch { /* ignore */ }

            _workerCts?.Dispose();
            _loggerFactory?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion Dispose
    }
}