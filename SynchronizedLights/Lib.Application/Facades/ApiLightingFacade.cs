// 2026-04-21 Nakazawa: SynchrolightAPI.Core 直接参照 → HTTP API (SynchrolightAPI.Api) 呼び出しに全面変更
// 変更前: MultiPortTransport / LightingService / TxWorkerService をインプロセスで直接生成・利用
// 変更後: HttpClient で http://localhost:5100/api/* を呼び出す構成に変更

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lib.Application.Interfaces;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;

namespace Lib.Application.Facades
{
    /// <summary>
    /// HTTP API経由のライティング制御ファセード
    /// 2026-04-21 Nakazawa: SynchrolightAPI.Core 直接利用 → REST API 呼び出しに変更
    /// SynchrolightAPI.Api (http://localhost:5100) に対して HTTP リクエストを送信する。
    /// </summary>
    public class ApiLightingFacade : ILightingFacade, IDisposable
    {
        #region フィールド

        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        // 2026-04-21 Nakazawa: Transport直接アクセス → ステータスキャッシュに変更
        // HTTP経由ではプロパティの同期取得ができないため、API呼び出し後にキャッシュを更新する方式
        private bool _isConnected;
        private List<string> _connectedPorts = new();
        private int _queueLength;
        private string? _lastError;
        private bool _disposed;

        #endregion フィールド

        #region プロパティ (ILightingFacade)

        // 2026-04-21 Nakazawa: _transport.ListPorts() 直接参照 → キャッシュ参照に変更
        public bool IsConnected => _isConnected;

        public IReadOnlyList<string> ConnectedPorts => _connectedPorts.AsReadOnly();

        public int QueueLength => _queueLength;

        public string? LastError => _lastError;

        public event EventHandler? StatusChanged;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// ApiLightingFacade を生成する。
        /// 2026-04-21 Nakazawa: コンストラクタ引数を (queueCapacity, sendIntervalMs) → (baseUrl) に変更
        /// MultiPortTransport / LightingService / TxWorkerService の生成を削除し、HttpClient を生成
        /// </summary>
        /// <param name="baseUrl">SynchrolightAPI.Api のベースURL（例: http://localhost:5100）</param>
        public ApiLightingFacade(string baseUrl)
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
            };

            // 2026-04-21 Nakazawa: API側の JSON 設定 (camelCase) に合わせる
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            Log.Information("[Api] ApiLightingFacade initialized (HTTP mode, baseUrl={BaseUrl})", baseUrl);
        }

        #endregion コンストラクタ

        #region 接続管理 (ILightingFacade)

        public async Task<IReadOnlyList<string>> GetAvailablePortsAsync()
        {
            // 2026-04-21 Nakazawa: ローカル SerialPort.GetPortNames() をそのまま維持
            // API側に利用可能ポート一覧エンドポイントが未実装のため、ローカル取得で代替
            // TODO: API側に GET /api/transport/ports エンドポイントが追加されたら切り替える
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            Array.Sort(ports);
            return await Task.FromResult<IReadOnlyList<string>>(ports);
        }

        // 2026-04-21 Nakazawa: _transport.ConnectAsync() → POST /api/transport/connect に変更
        public async Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
        {
            try
            {
                _lastError = null;
                var request = new { portNames = portNames.ToArray() };
                var response = await _httpClient.PostAsJsonAsync("api/transport/connect", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);

                if (!result.Success)
                {
                    _lastError = result.Error ?? "接続に失敗しました";
                    Log.Error("[Api] Connect failed: {Error}", _lastError);
                }
                else
                {
                    Log.Information("[Api] Connect succeeded: {Message}", result.Message);
                }
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Error(ex, "[Api] Connect HTTP error");
            }
            finally
            {
                await RefreshStatusAsync();
                RaiseStatusChanged();
            }
        }

        // 2026-04-21 Nakazawa: _transport.DisconnectAsync() → POST /api/transport/disconnect に変更
        public async Task DisconnectAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync("api/transport/disconnect", null);
                var result = await ReadApiResponseAsync(response);
                Log.Information("[Api] Disconnect: {Message}", result.Message);
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Error(ex, "[Api] Disconnect HTTP error");
            }
            finally
            {
                await RefreshStatusAsync();
                RaiseStatusChanged();
            }
        }

        // 2026-04-21 Nakazawa: _lightingService.InitializeTransmitterAsync() → POST /api/transmitter/init に変更
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

                Log.Information("[Api] InitializeTransmitter: {Message}", result.Message);
            }
            catch (HttpRequestException ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Error(ex, "[Api] InitializeTransmitter HTTP error");
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

        #region 制御 (ILightingFacade)

        public async Task SetColorAsync(Target target, Rgb color, CancellationToken ct = default)
        {
            try
            {
                // 2026-04-21 Nakazawa: InternalSetColorAsync 内部も HTTP 呼び出しに変更済み
                await InternalSetColorAsync(target, color, ct);
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

        public async Task FlashAsync(Target target, int speedMs, Rgb color, CancellationToken ct = default)
        {
            // クライアント側実装（API未対応）：1サイクル on/off — ロジック変更なし
            var halfMs = Math.Max(50, speedMs / 2);
            var black = new Rgb(0, 0, 0);

            try
            {
                await InternalSetColorAsync(target, color, ct);
                await Task.Delay(halfMs, ct);
                await InternalSetColorAsync(target, black, ct);
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        public async Task FadeInAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
        {
            // クライアント側実装：10ステップで段階補間 — ロジック変更なし
            const int steps = 10;
            var stepMs = Math.Max(50, timeMs / steps);

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
                RaiseStatusChanged();
            }
        }

        public async Task FadeOutAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default)
        {
            // クライアント側実装：10ステップで段階補間（減衰） — ロジック変更なし
            const int steps = 10;
            var stepMs = Math.Max(50, timeMs / steps);

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
                RaiseStatusChanged();
            }
        }

        public async Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default)
        {
            // 2026-04-21 Nakazawa: API側に POST /api/light/sequence が存在するためHTTP呼び出しに変更
            // ただし target → frameNo のマッピングは暫定で sequenceId をそのまま使用
            try
            {
                var request = new { frameNo = (uint)sequenceId };
                var response = await _httpClient.PostAsJsonAsync("api/light/sequence", request, _jsonOptions, ct);
                var result = await ReadApiResponseAsync(response, ct);
                Log.Information("[Api] ExecuteSequence: {Message}", result.Message);
            }
            catch (Exception ex)
            {
                _lastError = $"HTTP通信エラー: {ex.Message}";
                Log.Error(ex, "[Api] ExecuteSequence HTTP error");
            }
            finally
            {
                RaiseStatusChanged();
            }
        }

        #endregion 制御

        #region 内部メソッド

        /// <summary>
        /// UI側 Target を HTTP API 呼び出しに振り分ける。
        /// 2026-04-21 Nakazawa: LightingService直接呼び出し → HTTP POST に変更
        /// ALL → POST /api/light/global (A2)
        /// Group01〜08 → POST /api/light/rows/each (A3)
        /// </summary>
        private async Task InternalSetColorAsync(Target target, Rgb color, CancellationToken ct)
        {
            var colorObj = new { r = (int)color.R, g = (int)color.G, b = (int)color.B };

            if (target == Target.All)
            {
                // 2026-04-21 Nakazawa: _lightingService.SetGlobalColorAsync() → POST /api/light/global
                var request = new { color = colorObj };
                var response = await _httpClient.PostAsJsonAsync("api/light/global", request, _jsonOptions, ct);
                await EnsureApiSuccessAsync(response, "SetGlobalColor", ct);
            }
            else
            {
                // 2026-04-21 Nakazawa: _lightingService.SetRowColorAsync() → POST /api/light/rows/each
                var (startRow, len) = GroupToRowRange(target);
                var request = new
                {
                    field = 0,
                    startRow = (int)startRow,
                    len = (int)len,
                    color = colorObj
                };
                var response = await _httpClient.PostAsJsonAsync("api/light/rows/each", request, _jsonOptions, ct);
                await EnsureApiSuccessAsync(response, "SetRowColor", ct);
            }
        }

        /// <summary>
        /// API レスポンスを読み取り、ApiResponseDto に変換する。
        /// 2026-04-21 Nakazawa: 新規追加 — HTTP レスポンスのパース用ヘルパー
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
        /// 2026-04-21 Nakazawa: 新規追加
        /// </summary>
        private async Task EnsureApiSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
        {
            var result = await ReadApiResponseAsync(response, ct);
            if (!result.Success)
            {
                var errorMsg = result.Error ?? $"{operation} に失敗しました";
                Log.Error("[Api] {Operation} failed: {Error}", operation, errorMsg);
                throw new InvalidOperationException(errorMsg);
            }
            Log.Information("[Api] {Operation}: {Message}", operation, result.Message);
        }

        /// <summary>
        /// GET /api/transport/status を呼び出してステータスキャッシュを更新する。
        /// 2026-04-21 Nakazawa: 新規追加 — プロパティ用のキャッシュ更新メソッド
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

                    if (data.TryGetProperty("connectedPorts", out var cp))
                        _connectedPorts = new List<string>(
                            cp.ValueKind == JsonValueKind.Number
                                ? (cp.GetInt32() > 0 ? Enumerable.Repeat("(connected)", cp.GetInt32()) : Array.Empty<string>())
                                : Array.Empty<string>());

                    // Ports 配列からポート名を取得
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

                    _isConnected = _connectedPorts.Count > 0;

                    if (data.TryGetProperty("lastError", out var le) && le.ValueKind != JsonValueKind.Null)
                        _lastError = le.GetString();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Api] RefreshStatus failed — ステータスキャッシュ更新をスキップ");
            }
        }

        /// <summary>
        /// UI Group → (startRow, len) マッピング（25台×8グループ＝200台）— 変更なし
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
        /// API レスポンス用 DTO
        /// 2026-04-21 Nakazawa: 新規追加 — SynchrolightAPI.Api の ApiResponse に対応
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

        // 2026-04-21 Nakazawa: Transport/Worker の Dispose → HttpClient の Dispose に変更
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
