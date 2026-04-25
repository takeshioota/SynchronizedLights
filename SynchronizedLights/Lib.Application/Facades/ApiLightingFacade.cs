// 2026-04-25 REST API統合: SynchrolightAPI.Core 直接参照 → HTTP REST API 呼び出しに全面変更
// 変更前: MultiPortTransport / LightingService / TxWorkerService をインプロセスで直接生成・利用
// 変更後: HttpClient で http://localhost:5100/api/* を呼び出す構成
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

        public async Task BreathAsync(Target target, int cycleMs, Rgb color, int cycles = 3, CancellationToken ct = default)
        {
            if (ShouldDropCommand("Breath")) return;

            // 1 サイクル = cycleMs、半分ずつ FadeIn / FadeOut に割り当て
            var halfMs = Math.Max(100, cycleMs / 2);

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                for (var i = 0; i < cycles; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    // 明るくなる（既存 FadeIn ロジックと同じ 10 ステップ補間）
                    const int steps = 10;
                    var stepMs = Math.Max(50, halfMs / steps);

                    for (var s = 1; s <= steps; s++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var t = s / (double)steps;
                        var faded = new Rgb(
                            (byte)(color.R * t),
                            (byte)(color.G * t),
                            (byte)(color.B * t));
                        await InternalSetColorAsync(target, faded, ct);
                        if (s < steps) await Task.Delay(stepMs, ct);
                    }

                    // 暗くなる
                    for (var s = steps - 1; s >= 0; s--)
                    {
                        if (ct.IsCancellationRequested) break;
                        var t = s / (double)steps;
                        var faded = new Rgb(
                            (byte)(color.R * t),
                            (byte)(color.G * t),
                            (byte)(color.B * t));
                        await InternalSetColorAsync(target, faded, ct);
                        if (s > 0) await Task.Delay(stepMs, ct);
                    }
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

        #endregion 制御

        #region 内部メソッド

        /// <summary>
        /// UI側 Target を HTTP API 呼び出しに振り分ける。
        /// ALL          → POST /api/light/global (A2)
        /// Group01〜08  → POST /api/light/rows   (AA, 多行同色)
        ///
        /// 2026-04-25 hotfix-2: グループ制御を A3（行個別色）→ AA（多行同色）に変更
        ///   理由：グループ制御は本質的に「範囲を1色で塗る」用途であり、
        ///         実機ファームでは A3 が反応しないため AA を使う必要がある
        ///         （API 側 /api/light/rows が AA コマンドを生成 — LightController.cs 確認済）
        ///   変更：エンドポイント /api/light/rows/each → /api/light/rows
        ///         ペイロードフィールド名 len → rowLen
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
