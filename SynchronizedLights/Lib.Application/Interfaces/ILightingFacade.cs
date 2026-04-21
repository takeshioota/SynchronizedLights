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

    // --- 演出（ApiLightingFacade側の実装にて対応） ---

    /// <summary>点滅を実行する</summary>
    Task FlashAsync(Target target, int speedMs, Rgb color, CancellationToken ct = default);

    /// <summary>フェードインを実行する</summary>
    Task FadeInAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default);

    /// <summary>フェードアウトを実行する</summary>
    Task FadeOutAsync(Target target, int timeMs, Rgb color, CancellationToken ct = default);

    // --- シーケンス ---

    /// <summary>登録済みシーケンスを実行する</summary>
    Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default);
}