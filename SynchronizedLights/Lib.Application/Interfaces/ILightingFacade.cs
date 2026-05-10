using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;

namespace Lib.Application.Interfaces;

/// <summary>
/// ライティング制御ファセード
/// 概要：UIと通信層の境界。Dummy / Real API を差し替えるための統一インターフェース。
/// </summary>
public interface ILightingFacade
{
    // --- 接続管理 ---

    /// <summary>利用可能なCOMポート一覧を取得する</summary>
    Task<IReadOnlyList<string>> GetAvailablePortsAsync();

    /// <summary>指定ポート群を開く</summary>
    Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default);

    /// <summary>全ポートを閉じる</summary>
    Task DisconnectAsync();

    /// <summary>送信機の初期設定（FA/FB）を送信する</summary>
    Task InitializeTransmitterAsync(byte channel, byte power, CancellationToken ct = default);

    // --- 状態取得 ---

    /// <summary>いずれかのポートが接続中かどうか</summary>
    bool IsConnected { get; }

    /// <summary>現在接続中のポート名一覧</summary>
    IReadOnlyList<string> ConnectedPorts { get; }

    /// <summary>送信キュー長</summary>
    int QueueLength { get; }

    /// <summary>直近のエラー文字列（なければ null）</summary>
    string? LastError { get; }

    /// <summary>最終エラーをクリアする（トースト消去時に呼び出す）</summary>
    void ClearError();

    /// <summary>接続状態やキュー長が変化した際に発火するイベント</summary>
    event EventHandler? StatusChanged;

    // --- 即時制御 ---

    /// <summary>対象に色を設定する</summary>
    Task SetColorAsync(Target target, Rgb color, CancellationToken ct = default);

    // --- 演出 StartEffectAsync / StopEffectAsync ---

    /// <summary>点滅を実行する</summary>
    Task FlashAsync(Target target, int speedMs, Rgb color, CancellationToken ct = default);

    /// <summary>フェードインを実行する</summary>
    Task FadeInAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default);

    /// <summary>フェードアウトを実行する</summary>
    Task FadeOutAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default);

    /// <summary>
    /// ブレス（呼吸）演出を実行する
    /// </summary>
    Task BreathAsync(Target target, int cycleMs, Rgb color, int cycles = 3, CancellationToken ct = default);

    // --- シーケンス ---

    /// <summary>登録済みシーケンスを実行する</summary>
    Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default);

    /// <summary>
    /// エフェクトを開始する（Effect API: POST /api/effect/start）
    /// 概要：単発(continuous=false)・連続(continuous=true)を切替可能。
    ///       連続中に新しい StartEffect を呼ぶと前のエフェクトは API 側で自動停止される。
    /// </summary>
    /// <param name="effectType">"Flash" / "FadeIn" / "FadeOut" / "Breathing" / "SevenColor"</param>
    /// <param name="color">基本色</param>
    /// <param name="cycleDurationMs">1サイクルの時間（ms）</param>
    /// <param name="flashIntervalMs">Flash時のON/OFF間隔（ms）。Flash以外はnull可</param>
    /// <param name="fadeSteps">Fade補間ステップ数（既定20）</param>
    /// <param name="continuous">true=連続再生、false=1回のみ</param>
    Task StartEffectAsync(
        string effectType,
        Rgb color,
        int cycleDurationMs = 1000,
        int? flashIntervalMs = null,
        int fadeSteps = 20,
        bool continuous = true,
        CancellationToken ct = default);

    /// <summary>
    /// 実行中のエフェクトを停止する（Effect API: POST /api/effect/stop）
    /// </summary>
    Task StopEffectAsync(CancellationToken ct = default);

    /// <summary>
    /// シーケンスを登録または上書き保存する（POST /api/sequence）
    /// 概要：UI で編集した時間ベースシーケンスを API サーバー側に登録する。
    ///       同名のシーケンスが既に存在すれば上書きされる。
    /// </summary>
    /// <param name="name">シーケンス名（API 側では一意キー）</param>
    /// <param name="steps">API 形式に変換済みのステップ列</param>
    Task UpsertSequenceAsync(
        string name,
        IReadOnlyList<Lib.Application.Models.SequenceApiStep> steps,
        CancellationToken ct = default);

    /// <summary>
    /// 登録済みシーケンスの再生を開始する（POST /api/sequence/play）
    /// </summary>
    Task PlaySequenceAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// 再生中シーケンスを停止する（POST /api/sequence/stop）
    /// </summary>
    Task StopSequenceAsync(CancellationToken ct = default);

    /// <summary>
    /// 再生状態を取得する（GET /api/sequence/play/status）
    /// </summary>
    /// <returns>(isPlaying, currentSequenceName)</returns>
    Task<(bool IsPlaying, string? Name)> GetSequencePlayStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// 再生状態を詳細取得する（GET /api/sequence/play/status）
    /// 概要：再生中フラグ + シーケンス名 + 現在ステップ位置 + 総ステップ数を返す。
    ///       連続再生中の DataGrid ハイライトに使用する。
    /// </summary>
    /// <returns>(isPlaying, sequenceName, currentStepIndex, totalStepCount)。未再生時は (false, null, -1, 0)。</returns>
    Task<(bool IsPlaying, string? Name, int CurrentStepIndex, int TotalStepCount)> GetSequencePlayStatusDetailedAsync(CancellationToken ct = default);

    /// <summary>
    /// API サーバー上のシーケンス名一覧を取得する（GET /api/sequence）
    /// </summary>
    Task<IReadOnlyList<string>> ListSequenceNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// API サーバー上のシーケンスを削除する（DELETE /api/sequence/{name}）
    /// </summary>
    Task DeleteSequenceFromApiAsync(string name, CancellationToken ct = default);
}
