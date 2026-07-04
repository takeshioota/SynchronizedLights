// HttpClient で http://localhost:5100/api/* を呼び出す構成
//
//   - QueuePolicy / DropNewest / DroppedCount
//   - LatencyTracker 注入
//   - _intendedPorts による自動再接続（TryReconnectMissingPortsAsync）
//   - ReconnectCount
//   - Breath / Flash / FadeIn / FadeOut のクライアント側組立
//   - StatusChanged イベント

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Lib.Application.Interfaces;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;

namespace Lib.Application.Facades
{
    /// <summary>
    /// 実 API 実装（REST API / HTTP 版）
    /// 概要：SynchrolightAPI.Api（http://localhost:5100）に対して HTTP リクエストを送信する。
    /// </summary>
    public class ApiLightingFacade : ILightingFacade, IDisposable
    {
        #region フィールド

        // ===== HTTP 関連 =====
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        // ===== ステータスキャッシュ（HTTP のため同期取得不可、API 呼び出し後にキャッシュ更新） =====
        private bool _isConnected;
        private List<string> _connectedPorts = new();
        private int _queueLength;
        private int _highPriorityQueueLength;
        private int _disconnectedPortsCount;
        private string? _lastError;

        /// <summary>本来接続しておくべきポート一覧（自動再接続のため保持）</summary>
        private readonly List<string> _intendedPorts = new();
        private readonly object _intendedLock = new();

        /// <summary>キュー容量（Drop 判定用に保持）</summary>
        private readonly int _queueCapacity;

        /// <summary>キュー満杯ポリシー ("Wait" or "DropNewest")</summary>
        private readonly string _queuePolicy;

        /// <summary>ドロップ判定しきい値（0.0〜1.0）</summary>
        private readonly double _dropThreshold;

        /// <summary>累積ドロップ回数</summary>
        private long _droppedCount;

        /// <summary>累積自動再接続回数</summary>
        private long _reconnectCount;

        /// <summary>遅延計測（nullable：注入されなければ計測しない）</summary>
        private readonly LatencyTracker? _latency;

        private bool _disposed;

        /// <summary>KeepAlive 用：最後に送信した色（null = 未送信）</summary>
        private Rgb? _lastSentColor;

        /// <summary>KeepAlive 用：最後にコマンドを送信した時刻</summary>
        private DateTime _lastCommandTime = DateTime.MinValue;

        /// <summary>
        /// API 側エフェクト実行中フラグ。
        /// true の間は KeepAlive（api/light/global）をスキップする。
        /// エフェクトは API 側で独自に連続送信するため、KeepAlive は不要であり、
        /// 送信すると StartColorHold 経由でエフェクトが停止してしまう。
        /// </summary>
        private bool _apiEffectRunning;

        #endregion フィールド

        #region プロパティ (ILightingFacade)

        public bool IsConnected => _isConnected;

        public IReadOnlyList<string> ConnectedPorts => _connectedPorts.AsReadOnly();

        public int QueueLength => _queueLength;

        public string? LastError => _lastError;

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
        /// ApiLightingFacade を生成する（REST API 版）
        /// </summary>
        /// <param name="queueCapacity">送信キュー容量（Drop 判定用、API側と合わせる）（既定 256）</param>
        /// <param name="baseUrl">SynchrolightAPI.Api のベースURL（例：http://localhost:5100）</param>
        /// <param name="queuePolicy">"Wait" or "DropNewest"（既定 Wait）</param>
        /// <param name="dropThreshold">ドロップ判定の使用率閾値（既定 0.95）</param>
        /// <param name="latency">遅延計測トラッカー（任意）</param>
        public ApiLightingFacade(
            int queueCapacity = 256,
            string baseUrl = "http://localhost:5100",
            string queuePolicy = "Wait",
            double dropThreshold = 0.95,
            LatencyTracker? latency = null)
        {
            _queueCapacity = queueCapacity;
            _queuePolicy = queuePolicy;
            _dropThreshold = dropThreshold;
            _latency = latency;

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
                // 短すぎると本番中の一時的遅延で誤エラーになるため 10 秒
                Timeout = TimeSpan.FromSeconds(10)
            };

            // API 側の JSON 設定 (camelCase) に合わせる
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            Log.Information(
                "[Api] ApiLightingFacade initialized (HTTP mode, baseUrl={BaseUrl}, queue={Queue}, policy={Policy}, threshold={Thr}, latency={HasLat})",
                baseUrl, queueCapacity, queuePolicy, dropThreshold, _latency != null);
        }

        #endregion コンストラクタ

        #region ポリシー判定

        /// <summary>
        /// 現在のキュー状況とポリシーから、コマンドをドロップすべきか判定する
        /// 概要：QueuePolicy が "DropNewest" のときだけ有効。
        ///       キャッシュされた _queueLength が容量 × 閾値を超えていたら Drop して true を返す。
        /// </summary>
        private bool ShouldDropCommand(string commandName)
        {
            if (!string.Equals(_queuePolicy, "DropNewest", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var threshold = (int)(_queueCapacity * _dropThreshold);
            var current = _queueLength;
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
            // OS から実 COM ポート一覧を取得（API 側に同等エンドポイントなし）
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
                var request = new { portNames = ports.ToArray() };
                var response = await _httpClient.PostAsJsonAsync("api/transport/connect", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "接続に失敗しました";
                    Log.Warning("[Api] Connect failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information(
                        "[Api] Connect: {Ports} (intended saved for auto-reconnect): {Msg}",
                        string.Join(", ", ports), result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] Connect HTTP error: {Err}", ex.Message);
            }
            finally
            {
                await RefreshStatusAsync();
                RaiseStatusChanged();
            }
        }

        public async Task DisconnectAsync()
        {
            lock (_intendedLock)
            {
                _intendedPorts.Clear();
            }

            try
            {
                var response = await _httpClient.PostAsync("api/transport/disconnect", null);
                var result = await ReadApiResponseAsync(response);
                Log.Information("[Api] Disconnect: all ports closed, intended cleared. {Msg}", result.Message);
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] Disconnect HTTP error: {Err}", ex.Message);
            }
            finally
            {
                await RefreshStatusAsync();
                RaiseStatusChanged();
            }
        }

        public async Task InitializeTransmitterAsync(byte channel, byte power, CancellationToken ct = default)
        {
            try
            {
                _lastError = null;
                var request = new { channel = (int)channel, power = (int)power };
                var response = await _httpClient.PostAsJsonAsync("api/transmitter/init", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "送信機初期化に失敗しました";
                    throw new InvalidOperationException(_lastError);
                }

                Log.Information("[Api] InitializeTransmitter: ch={Ch}, pwr={Pwr}, {Msg}", channel, power, result.Message);
            }
            catch (HttpRequestException ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Error(ex, "[Api] InitializeTransmitter HTTP error");
                throw;
            }
            finally
            {
                await RefreshStatusAsync();
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
        /// 概要：API の GET /api/transport/status で現在の接続状態を取得し、
        ///       intended に含まれるが connectedPorts に含まれないポートを
        ///       POST /api/transport/connect で再接続する。
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

            // API から現在の接続状態を取得
            await RefreshStatusAsync();

            var liveConnected = _connectedPorts
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = intendedSnapshot
                .Where(p => !liveConnected.Contains(p))
                .ToList();

            if (missing.Count == 0) return 0;

            Log.Warning(
                "[Api] Missing ports detected: {Ports}. Attempting reconnect via HTTP...",
                string.Join(", ", missing));

            try
            {
                // 不足分のみ再接続
                var request = new { portNames = missing.ToArray() };
                var response = await _httpClient.PostAsJsonAsync("api/transport/connect", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                // 再接続後にもう一度状態を確認
                await RefreshStatusAsync();
                var afterLive = _connectedPorts
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
                        "[Api] Reconnect attempt returned but no ports came back online. {Err}",
                        result.Error ?? "(no error)");
                }

                return reconnected;
            }
            catch (Exception ex)
            {
                _lastError = $"再接続失敗: {ex.Message}";
                Log.Warning("[Api] Reconnect HTTP error: {Err}", ex.Message);
                RaiseStatusChanged();
                return 0;
            }
        }

        #endregion 自動再接続

        #region 制御 (ILightingFacade)

        public async Task SetColorAsync(Target target, Rgb color, CancellationToken ct = default)
        {
            if (ShouldDropCommand("SetColor")) return;

            _apiEffectRunning = false;
            var startTs = Stopwatch.GetTimestamp();
            try
            {
                await InternalSetColorAsync(target, color, ct);
                _lastSentColor = color;
                _lastCommandTime = DateTime.UtcNow;
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

        // 旧 FlashAsync / FadeInAsync / FadeOutAsync / BreathAsync は削除。
        // 理由：API 担当者からの指摘により方式 A（UI 側 HTTP ループ）は廃止し、
        //       方式 B（StartEffectAsync = API 側で 0.2 秒間隔の継続送信）に統一する。
        //       MainWindowViewModel.ToggleEffectAsync は既に StartEffectAsync を使用済み。

        public async Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default)
        {
            if (ShouldDropCommand("Sequence")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                // 仕様：POST /api/light/sequence  body: { "frameNo": <uint> }
                // target 引数は現状未使用（API 側は frameNo のみ受領）
                var request = new { frameNo = (uint)sequenceId };
                var response = await _httpClient.PostAsJsonAsync("api/light/sequence", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? $"Sequence {sequenceId} 実行失敗";
                    Log.Warning("[Api] Sequence failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information(
                        "[Api] Sequence executed. target={Target}, id={Id}, msg={Msg}",
                        target, sequenceId, result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] Sequence HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task PlayInternalProgramAsync(uint frameNo, CancellationToken ct = default)
        {
            if (ShouldDropCommand("InternalProgram")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var request = new { frameNo };
                var response = await _httpClient.PostAsJsonAsync("api/light/internal-program", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? $"InternalProgram frame={frameNo} 実行失敗";
                    Log.Warning("[Api] InternalProgram failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] InternalProgram started. frame={FrameNo}, msg={Msg}", frameNo, result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] InternalProgram HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task StopInternalProgramAsync(CancellationToken ct = default)
        {
            if (ShouldDropCommand("InternalProgramStop")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var response = await _httpClient.PostAsync("api/light/internal-program/stop", null, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "InternalProgram 停止失敗";
                    Log.Warning("[Api] InternalProgramStop failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] InternalProgram stopped. msg={Msg}", result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] InternalProgramStop HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        // --- レインボー（V4.5: 3.15-3.22） ---

        public async Task StartRainbowAsync(
            RainbowMode mode,
            IReadOnlyList<Rgb> colors,
            int cycleDurationMs = 1000,
            int? blinkPeriodMs = null,
            byte? dutyRatio = null,
            int? fadeInMs = null,
            int? fadeOutMs = null,
            CancellationToken ct = default)
        {
            if (ShouldDropCommand("Rainbow")) return;

            _apiEffectRunning = true;
            _lastCommandTime = DateTime.UtcNow;
            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var colorArray = colors.Select(c => new { r = (int)c.R, g = (int)c.G, b = (int)c.B }).ToArray();
                var request = new
                {
                    mode = (int)mode,
                    colors = colorArray,
                    cycleDurationMs,
                    blinkPeriodMs,
                    dutyRatio = dutyRatio.HasValue ? (int?)dutyRatio.Value : null,
                    fadeInMs,
                    fadeOutMs,
                };
                var response = await _httpClient.PostAsJsonAsync("api/light/rainbow/start", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _apiEffectRunning = false;
                    _lastError = result.Error ?? $"Rainbow {mode} 開始失敗";
                    Log.Warning("[Api] Rainbow start failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] Rainbow started. mode={Mode}, colors={N}, cycle={Cycle}ms",
                        mode, colors.Count, cycleDurationMs);
                }
            }
            catch (Exception ex)
            {
                _apiEffectRunning = false;
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] Rainbow start HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task StopRainbowAsync(CancellationToken ct = default)
        {
            if (ShouldDropCommand("RainbowStop")) return;

            _apiEffectRunning = false;
            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var response = await _httpClient.PostAsync("api/light/rainbow/stop", null, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "Rainbow 停止失敗";
                    Log.Warning("[Api] Rainbow stop failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] Rainbow stopped. msg={Msg}", result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] Rainbow stop HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task PauseRainbowAsync(CancellationToken ct = default)
        {
            if (ShouldDropCommand("RainbowPause")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var response = await _httpClient.PostAsync("api/light/rainbow/pause", null, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "Rainbow 一時停止失敗";
                    Log.Warning("[Api] Rainbow pause failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] Rainbow paused. msg={Msg}", result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] Rainbow pause HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task SendColorTableAsync(
            IReadOnlyList<Rgb> colors,
            int cycleDurationMs = 1000,
            CancellationToken ct = default)
        {
            if (ShouldDropCommand("ColorTable")) return;

            _lastCommandTime = DateTime.UtcNow;
            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var colorArray = colors.Select(c => new { r = (int)c.R, g = (int)c.G, b = (int)c.B }).ToArray();
                var request = new
                {
                    colors = colorArray,
                    cycleDurationMs,
                };
                var response = await _httpClient.PostAsJsonAsync("api/light/rainbow/color-table", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "色テーブル送信失敗";
                    Log.Warning("[Api] Color table send failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] Color table sent. colors={N}, cycle={Cycle}ms",
                        colors.Count, cycleDurationMs);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] Color table HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// Effect 開始（POST /api/effect/start）
        /// 概要：Flash/FadeIn/FadeOut/Breathing/SevenColor を
        ///       continuous=true で連続再生、false で単発実行する。
        /// </summary>
        public async Task StartEffectAsync(
            string effectType,
            Rgb color,
            int cycleDurationMs = 1000,
            int? flashIntervalMs = null,
            int fadeSteps = 20,
            bool continuous = true,
            CancellationToken ct = default)
        {
            if (ShouldDropCommand($"Effect.{effectType}")) return;

            _apiEffectRunning = true;
            // StartRainbowAsync と同様に更新する。これを怠ると、エフェクトの各ステップ間
            // （事前停止で _apiEffectRunning が一瞬 false になる隙間）で KeepAlive が
            // api/light/global に古い色を送り、StartColorHold 経由で走行中エフェクトを
            // 止めてしまう（Chase 途中で色が落ちる一因）。
            _lastCommandTime = DateTime.UtcNow;
            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var colorObj = new { r = (int)color.R, g = (int)color.G, b = (int)color.B };
                object request;

                if (flashIntervalMs.HasValue)
                {
                    request = new
                    {
                        type = effectType,
                        color = colorObj,
                        field = 0,
                        cycleDurationMs = cycleDurationMs,
                        flashIntervalMs = flashIntervalMs.Value,
                        fadeSteps = fadeSteps,
                        continuous = continuous
                    };
                }
                else
                {
                    request = new
                    {
                        type = effectType,
                        color = colorObj,
                        field = 0,
                        cycleDurationMs = cycleDurationMs,
                        fadeSteps = fadeSteps,
                        continuous = continuous
                    };
                }

                var response = await _httpClient.PostAsJsonAsync("api/effect/start", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _apiEffectRunning = false;
                    _lastError = result.Error ?? $"Effect {effectType} 開始失敗";
                    Log.Warning("[Api] StartEffect failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information(
                        "[Api] StartEffect: type={Type}, continuous={Cont}, msg={Msg}",
                        effectType, continuous, result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] StartEffect HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// Effect 停止（POST /api/effect/stop）
        /// </summary>
        public async Task StopEffectAsync(CancellationToken ct = default)
        {
            _apiEffectRunning = false;
            try
            {
                var response = await _httpClient.PostAsync("api/effect/stop", null, ct);
                var result = await ReadApiResponseAsync(response, ct);
                Log.Information("[Api] StopEffect: {Msg}", result.Message);
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] StopEffect HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// シーケンスを登録/上書き（POST /api/sequence）
        /// </summary>
        public async Task UpsertSequenceAsync(
            string name,
            IReadOnlyList<Lib.Application.Models.SequenceApiStep> steps,
            CancellationToken ct = default)
        {
            if (ShouldDropCommand("Sequence.Upsert")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var request = new
                {
                    name = name,
                    steps = steps,
                };
                var response = await _httpClient.PostAsJsonAsync("api/sequence", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? $"Sequence {name} 登録失敗";
                    Log.Warning("[Api] UpsertSequence failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] UpsertSequence: name={Name}, steps={Count}",
                        name, steps.Count);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] UpsertSequence HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// シーケンス再生開始（POST /api/sequence/play）
        /// </summary>
        public async Task PlaySequenceAsync(string name, CancellationToken ct = default)
        {
            if (ShouldDropCommand("Sequence.Play")) return;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var request = new { name = name };
                var response = await _httpClient.PostAsJsonAsync("api/sequence/play", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? $"Sequence {name} 再生失敗";
                    Log.Warning("[Api] PlaySequence failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] PlaySequence: name={Name}, msg={Msg}",
                        name, result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] PlaySequence HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// シーケンス停止（POST /api/sequence/stop）
        /// </summary>
        public async Task StopSequenceAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _httpClient.PostAsync("api/sequence/stop", null, ct);
                var result = await ReadApiResponseAsync(response, ct);
                Log.Information("[Api] StopSequence: {Msg}", result.Message);
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] StopSequence HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// シーケンス一時停止（POST /api/sequence/play/pause）
        /// 停止位置は API 側で保持され、ResumeSequenceAsync で続きから再開できる。
        /// </summary>
        public async Task<bool> PauseSequenceAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _httpClient.PostAsync("api/sequence/play/pause", null, ct);
                var result = await ReadApiResponseAsync(response, ct);
                Log.Information("[Api] PauseSequence: {Msg}", result.Message ?? result.Error);
                return result.Success;
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] PauseSequence HTTP error: {Err}", ex.Message);
                return false;
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// シーケンス再開（POST /api/sequence/play/resume）
        /// PauseSequenceAsync で保存された位置からシーケンス再生を再開する。
        /// </summary>
        public async Task<bool> ResumeSequenceAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _httpClient.PostAsync("api/sequence/play/resume", null, ct);
                var result = await ReadApiResponseAsync(response, ct);
                Log.Information("[Api] ResumeSequence: {Msg}", result.Message ?? result.Error);
                return result.Success;
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] ResumeSequence HTTP error: {Err}", ex.Message);
                return false;
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        /// <summary>
        /// 再生状態取得（GET /api/sequence/play/status）
        /// </summary>
        public async Task<(bool IsPlaying, string? Name)> GetSequencePlayStatusAsync(
            CancellationToken ct = default)
        {
            var (playing, name, _, _) = await GetSequencePlayStatusDetailedAsync(ct);
            return (playing, name);
        }

        /// <summary>
        /// 再生状態取得（詳細版）：再生中フラグ + 名前 + 現在ステップ位置 + 総ステップ数
        /// </summary>
        public async Task<(bool IsPlaying, string? Name, int CurrentStepIndex, int TotalStepCount)>
            GetSequencePlayStatusDetailedAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync("api/sequence/play/status", ct);
                if (!response.IsSuccessStatusCode) return (false, null, -1, 0);

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("data", out var data)) return (false, null, -1, 0);

                var isPlaying = data.TryGetProperty("isPlaying", out var ip) && ip.GetBoolean();
                string? name = null;
                if (data.TryGetProperty("sequenceName", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    name = n.GetString();
                }

                int currentStepIndex = -1;
                if (data.TryGetProperty("currentStepIndex", out var cs) && cs.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    currentStepIndex = cs.GetInt32();
                }

                int totalStepCount = 0;
                if (data.TryGetProperty("totalStepCount", out var ts) && ts.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    totalStepCount = ts.GetInt32();
                }

                return (isPlaying, name, currentStepIndex, totalStepCount);
            }
            catch (Exception ex)
            {
                Log.Debug("[Api] GetSequencePlayStatusDetailed failed: {Err}", ex.Message);
                return (false, null, -1, 0);
            }
        }

        /// <summary>
        /// 登録済みシーケンス名一覧（GET /api/sequence）
        /// </summary>
        public async Task<IReadOnlyList<string>> ListSequenceNamesAsync(
            CancellationToken ct = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync("api/sequence", ct);
                if (!response.IsSuccessStatusCode) return Array.Empty<string>();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("data", out var data)) return Array.Empty<string>();
                if (!data.TryGetProperty("names", out var names)) return Array.Empty<string>();

                var list = new List<string>();
                foreach (var item in names.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s)) list.Add(s);
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                Log.Debug("[Api] ListSequenceNames failed: {Err}", ex.Message);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// シーケンス削除（DELETE /api/sequence/{name}）
        /// </summary>
        public async Task DeleteSequenceFromApiAsync(string name, CancellationToken ct = default)
        {
            try
            {
                var encoded = Uri.EscapeDataString(name);
                using var response = await _httpClient.DeleteAsync($"api/sequence/{encoded}", ct);
                var result = await ReadApiResponseAsync(response, ct);
                Log.Information("[Api] DeleteSequence: name={Name}, msg={Msg}",
                    name, result.Message);
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] DeleteSequence HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RaiseStatusChanged();
            }
        }
        /// <summary>
        /// 信号送出を完全停止する（セルフモード復帰用）
        /// 概要：KeepAlive を停止し、実行中のエフェクト/シーケンスも停止する。
        ///       SNO端末は信号途絶後 1〜3 秒でセルフモードに復帰する。
        /// </summary>
        public async Task StopSignalAsync(CancellationToken ct = default)
        {
            _lastSentColor = null;
            _lastCommandTime = DateTime.MinValue;

            try { await StopEffectAsync(ct); } catch { }
            try { await StopSequenceAsync(ct); } catch { }

            Log.Information("[Api] StopSignal: KeepAlive 停止、端末はセルフモードに復帰します");
            RaiseStatusChanged();
        }

        /// <summary>
        /// KeepAlive: 最後に送信した色を再送する。
        /// SNO端末は 2.4GHz 信号途絶後 1〜3 秒でセルフモードに復帰するため、
        /// 定期的に再送して制御モードを維持する。
        /// ログ出力・ステータス更新は行わない（透過的動作）。
        /// </summary>
        public async Task SendKeepAliveAsync(CancellationToken ct = default)
        {
            if (!_isConnected) return;
            if (_lastSentColor == null) return;
            // API 側エフェクト実行中は KeepAlive 不要（エフェクト自身が連続送信している）
            // KeepAlive が api/light/global を叩くと StartColorHold でエフェクトが停止してしまう
            if (_apiEffectRunning) return;
            // 直近 800ms 以内にコマンド送信済みなら再送不要
            if ((DateTime.UtcNow - _lastCommandTime).TotalMilliseconds < 800) return;

            try
            {
                var color = _lastSentColor!;
                var colorObj = new { r = (int)color.R, g = (int)color.G, b = (int)color.B };
                var request = new { color = colorObj };
                await _httpClient.PostAsJsonAsync("api/light/global", request, _jsonOptions, ct);
                _lastCommandTime = DateTime.UtcNow;
            }
            catch
            {
                // KeepAlive 失敗はアクション不要（次回リトライで回復）
            }
        }

        #endregion 制御

        #region 内部メソッド

        /// <summary>
        /// UI側 Target を HTTP API 呼び出しに振り分ける。
        /// ALL          → POST /api/light/global (A2)
        /// Group01〜08  → POST /api/light/rows   (AA, 多行同色)
        　　    /// エンドポイント /api/light/rows/each → /api/light/rows
        /// ペイロードフィールド名 len → rowLen
        /// </summary>
        private async Task InternalSetColorAsync(Target target, Rgb color, CancellationToken ct)
        {
            var colorObj = new { r = (int)color.R, g = (int)color.G, b = (int)color.B };

            HttpResponseMessage response;
            string operation;

            if (target == Target.All)
            {
                var request = new { color = colorObj };
                response = await _httpClient.PostAsJsonAsync("api/light/global", request, _jsonOptions, ct);
                operation = "SetGlobalColor";
            }
            else
            {
                var (startRow, len) = GroupToRowRange(target);
                var request = new
                {
                    field = 0,
                    startRow = (int)startRow,
                    rowLen = (int)len,        // ← API 仕様書 /api/light/rows のフィールド名は rowLen
                    color = colorObj
                };
                // /api/light/rows は AA コマンドを生成（多行同色）
                response = await _httpClient.PostAsJsonAsync("api/light/rows", request, _jsonOptions, ct);
                operation = $"SetRowsColor({target})";
            }

            await EnsureApiSuccessAsync(response, operation, ct);
        }

        /// <summary>
        /// API レスポンスを読み取り、ApiResponseDto に変換する。
        /// </summary>
        private async Task<ApiResponseDto> ReadApiResponseAsync(HttpResponseMessage response, CancellationToken ct = default)
        {
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // HTTP エラー時もJSON解析を試みる（API側は BadRequest でも ApiResponse を返す）
                try
                {
                    var errorResult = JsonSerializer.Deserialize<ApiResponseDto>(json, _jsonOptions);
                    if (errorResult != null) return errorResult;
                }
                catch { /* JSONパース失敗時は下で汎用エラーを返す */ }

                return new ApiResponseDto
                {
                    Success = false,
                    Error = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                };
            }

            var result = JsonSerializer.Deserialize<ApiResponseDto>(json, _jsonOptions);
            return result ?? new ApiResponseDto { Success = false, Error = "レスポンスのパースに失敗しました" };
        }

        /// <summary>
        /// API 呼び出し結果を確認し、失敗時は例外をスローする。
        /// </summary>
        private async Task EnsureApiSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
        {
            var result = await ReadApiResponseAsync(response, ct);
            if (!result.Success)
            {
                var errorMsg = result.Error ?? $"{operation} に失敗しました";
                Log.Warning("[Api] {Operation} failed: {Error}", operation, errorMsg);
                throw new InvalidOperationException(errorMsg);
            }
            // 成功時は冗長なのでログ出さない（高頻度呼び出しのため）
        }

        /// <summary>
        /// GET /api/transport/status を呼び出してステータスキャッシュを更新する。
        /// 概要：queueLength / highPriorityQueueLength / connectedPorts /
        ///       disconnectedPorts / lastError / ports[] を取得してキャッシュに反映。
        /// </summary>
        private async Task RefreshStatusAsync()
        {
            var wasConnected = _isConnected;
            try
            {
                var response = await _httpClient.GetAsync("api/transport/status");
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ApiResponseDto>(json, _jsonOptions);

                if (result?.Success == true && result.Data != null)
                {
                    var data = (JsonElement)result.Data;

                    if (data.TryGetProperty("queueLength", out var ql))
                        _queueLength = ql.GetInt32();

                    if (data.TryGetProperty("highPriorityQueueLength", out var hql))
                        _highPriorityQueueLength = hql.GetInt32();

                    if (data.TryGetProperty("disconnectedPorts", out var dcp))
                        _disconnectedPortsCount = dcp.GetInt32();

                    // ports 配列から接続中ポート名を取得
                    if (data.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                    {
                        var connectedNames = new List<string>();
                        foreach (var port in ports.EnumerateArray())
                        {
                            var isConnected = port.TryGetProperty("isConnected", out var ic) && ic.GetBoolean();
                            var name = port.TryGetProperty("name", out var n) ? n.GetString() : null;
                            if (isConnected && name != null)
                                connectedNames.Add(name);
                        }
                        _connectedPorts = connectedNames;
                    }
                    else if (data.TryGetProperty("connectedPorts", out var cpInt) && cpInt.ValueKind == JsonValueKind.Number)
                    {
                        // ports 配列が無い旧フォーマット fallback
                        var n = cpInt.GetInt32();
                        _connectedPorts = n > 0
                            ? Enumerable.Range(1, n).Select(i => $"(connected#{i})").ToList()
                            : new List<string>();
                    }

                    _isConnected = _connectedPorts.Count > 0;

                    if (data.TryGetProperty("lastError", out var le) && le.ValueKind != JsonValueKind.Null)
                        _lastError = le.GetString();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Api] RefreshStatus failed: {Err} — ステータスキャッシュ更新スキップ", ex.Message);
            }

            // 接続状態が変化したら UI に通知（ケーブル抜け等で待機中に切断された場合も ● を即時更新）
            if (wasConnected != _isConnected)
            {
                Log.Information("[Api] 接続状態変化: Connected={Connected}", _isConnected);
                RaiseStatusChanged();
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

        /// <summary>
        /// UI Group → (startRow, len) マッピング（25台×8グループ＝200台）
        /// </summary>
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

        #region 内部DTO

        /// <summary>
        /// API レスポンス用 DTO（SynchrolightAPI.Api の ApiResponse に対応）
        /// </summary>
        private class ApiResponseDto
        {
            public bool Success { get; set; }
            public string? Message { get; set; }
            public string? Error { get; set; }
            public object? Data { get; set; }
        }

        #endregion 内部DTO

        #region ファイル転送

        public async Task WriteFileVia24GAsync(byte[] rgbData, uint frameNo, CancellationToken ct = default)
        {
            var startTs = Stopwatch.GetTimestamp();
            try
            {
                var request = new { frameNo, data = Convert.ToBase64String(rgbData) };
                var response = await _httpClient.PostAsJsonAsync("api/light/file-write/24g", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "2.4Gファイル書き込み失敗";
                    Log.Warning("[Api] FileWrite24G failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] FileWrite24G completed. frame={FrameNo}, size={Size}bytes",
                        frameNo, rgbData.Length);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] FileWrite24G HTTP error: {Err}", ex.Message);
            }
            finally
            {
                RecordLatency(startTs);
                RaiseStatusChanged();
            }
        }

        public async Task<IReadOnlyList<(string Address, string Name, int Rssi)>> ScanBleDevicesAsync(
            int timeoutSeconds = 5, CancellationToken ct = default)
        {
            try
            {
                var response = await _httpClient.GetAsync($"api/ble/scan?timeout={timeoutSeconds}", ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                var devices = new List<(string, string, int)>();
                if (doc.RootElement.TryGetProperty("data", out var data)
                    && data.TryGetProperty("devices", out var arr))
                {
                    foreach (var dev in arr.EnumerateArray())
                    {
                        var addr = dev.GetProperty("address").GetString() ?? "";
                        var name = dev.GetProperty("name").GetString() ?? "";
                        var rssi = dev.GetProperty("rssi").GetInt32();
                        devices.Add((addr, name, rssi));
                    }
                }
                return devices;
            }
            catch (Exception ex)
            {
                Log.Warning("[Api] BLE scan error: {Err}", ex.Message);
                return Array.Empty<(string, string, int)>();
            }
        }

        public async Task ConnectBleAsync(string deviceAddress, CancellationToken ct = default)
        {
            try
            {
                var request = new { deviceAddress };
                var response = await _httpClient.PostAsJsonAsync("api/ble/connect", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);
                if (!result.Success)
                {
                    _lastError = result.Error ?? "BLE接続失敗";
                    Log.Warning("[Api] BLE connect failed: {Err}", _lastError);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] BLE connect error: {Err}", ex.Message);
            }
        }

        public async Task DisconnectBleAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _httpClient.PostAsync("api/ble/disconnect", null, ct);
                await ReadApiResponseAsync(response, ct);
            }
            catch (Exception ex)
            {
                Log.Warning("[Api] BLE disconnect error: {Err}", ex.Message);
            }
        }

        public async Task TransferFileBleAsync(byte[] rgbData, CancellationToken ct = default)
        {
            try
            {
                var request = new { data = Convert.ToBase64String(rgbData) };
                var response = await _httpClient.PostAsJsonAsync("api/ble/file-write", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);
                if (!result.Success)
                {
                    _lastError = result.Error ?? "BLEファイル転送失敗";
                    Log.Warning("[Api] BLE file transfer failed: {Err}", _lastError);
                }
                else
                {
                    Log.Information("[Api] BLE file transfer completed. size={Size}bytes", rgbData.Length);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Warning("[Api] BLE file transfer error: {Err}", ex.Message);
            }
        }

        #endregion ファイル転送

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _httpClient?.Dispose(); } catch { /* ignore */ }

            GC.SuppressFinalize(this);
        }

        #endregion Dispose
    }
}
