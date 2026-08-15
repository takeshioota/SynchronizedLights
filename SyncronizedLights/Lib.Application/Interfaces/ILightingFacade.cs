using System.Collections.Generic;
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

    /// <summary>
    /// 受信端末の受信チャンネルを設定する（A6: 前後分区・行ベース / POST /api/light/rx-channel）。
    /// 全端末へブロードキャスト（場次=0x00 / 開始行=0xFFFF / 長さ=0x01・可変は ch のみ / 2026-07-24訂正）。ch=2..4（CH1は端末未実装）。
    /// ※このコマンドは「端末側」の周波数を変える。送信機側（FA）は InitializeTransmitterAsync で別途合わせる必要がある。
    /// </summary>
    Task SetReceiverChannelAsync(byte channel, CancellationToken ct = default);

    /// <summary>
    /// 受信端末の受信チャンネルを設定する（AD: 左右分区・列ベース / POST /api/light/rx-channel-col）。
    /// 全端末へブロードキャスト。端末の区画方式（前後/左右）に依らず適用できるよう A6 と併用する。
    /// </summary>
    Task SetReceiverChannelColAsync(byte channel, CancellationToken ct = default);

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

    /// <summary>
    /// from→to へ durationMs かけてスムーズにフェードする（色→色の遷移）。
    /// 補間はサーバ(API)側で ≈50fps・各フレーム保持中も再送される堅牢な方式で行い、
    /// HTTP 送信は1回のみ。旧来の UI 側 30ms・1フレームずつ HTTP 送信のカクつき/ジッタを解消する。
    /// </summary>
    Task FadeColorAsync(Rgb from, Rgb to, int durationMs, int fadeSteps = 20, CancellationToken ct = default);

    // --- 演出 StartEffectAsync / StopEffectAsync ---
    //
    // 旧 FlashAsync / FadeInAsync / FadeOutAsync / BreathAsync は
    // API 担当者からの指摘（UI 側ループ＝方式 A）に基づき削除。
    // 演出は StartEffectAsync / StopEffectAsync（方式 B：API 側で 0.2 秒間隔の継続送信）に統一する。

    // --- シーケンス ---

    /// <summary>登録済みシーケンスを実行する</summary>
    Task ExecuteSequenceAsync(Target target, int sequenceId, CancellationToken ct = default);

    /// <summary>
    /// SNO端末の内蔵プログラムを再生する（A1 コマンド: POST /api/light/internal-program）
    /// </summary>
    /// <param name="frameNo">内蔵プログラムのフレーム番号</param>
    Task PlayInternalProgramAsync(uint frameNo, CancellationToken ct = default);

    /// <summary>
    /// 内蔵プログラムを停止する（POST /api/light/internal-program/stop → A2黒で上書き）
    /// </summary>
    Task StopInternalProgramAsync(CancellationToken ct = default);

    // --- レインボー（V4.5: 3.15-3.22） ---

    /// <summary>
    /// レインボーエフェクトを開始する（0xA9: 3.15 カラー設定 + 3.16-3.21 モード開始）
    /// 概要：API Server 側で 3.15（カラーパレット送信）後に、
    ///       指定モードの 0xA9 0x03 コマンドを継続送信するループを開始する。
    /// </summary>
    /// <param name="mode">レインボーモード（Solid/Blink/FadeInOut/FadeIn/FadeOut/Random）</param>
    /// <param name="colors">カラーパレット（2〜7色）</param>
    /// <param name="cycleDurationMs">色切り替え速度（ミリ秒）— 500〜2500</param>
    /// <param name="blinkPeriodMs">点滅周期（ms）— Blink モード時のみ: 100〜3600</param>
    /// <param name="dutyRatio">点灯比率（1〜9 = 10%〜90%）— Blink モード時のみ</param>
    /// <param name="fadeInMs">フェードイン時間（ms）— FadeInOut/FadeIn モード時: 256〜3000</param>
    /// <param name="fadeOutMs">フェードアウト時間（ms）— FadeInOut/FadeOut モード時: 256〜3000</param>
    Task StartRainbowAsync(
        RainbowMode mode,
        IReadOnlyList<Rgb> colors,
        int cycleDurationMs = 1000,
        int? blinkPeriodMs = null,
        byte? dutyRatio = null,
        int? fadeInMs = null,
        int? fadeOutMs = null,
        CancellationToken ct = default);

    /// <summary>
    /// レインボーエフェクトを停止する（継続送信ループ停止）
    /// </summary>
    Task StopRainbowAsync(CancellationToken ct = default);

    /// <summary>
    /// 7色ランダム一時停止（0xA9 0x04: 前回の色を保持して継続送信）
    /// </summary>
    Task PauseRainbowAsync(CancellationToken ct = default);

    /// <summary>
    /// 色テーブルのみ送信する（0xA9 0x02: 3.15 色テーブル定義）
    /// モード開始（0xA9 0x03）は送信しない。
    /// </summary>
    /// <param name="colors">カラーパレット（2〜7色）</param>
    /// <param name="cycleDurationMs">色切り替え速度（ミリ秒）— 500〜2500</param>
    Task SendColorTableAsync(
        IReadOnlyList<Rgb> colors,
        int cycleDurationMs = 1000,
        CancellationToken ct = default);

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
    /// 信号送出を完全停止する（セルフモード復帰用）
    /// 概要：KeepAlive・エフェクト・シーケンスをすべて停止し、
    ///       SNO端末が 1〜3 秒後にセルフモードへ自動復帰するようにする。
    /// </summary>
    Task StopSignalAsync(CancellationToken ct = default);

    /// <summary>
    /// Emergency Black 抑止フラグを設定する（BUG-20260729-08）。
    /// true の間、KeepAlive・再接続時の色再送を黒(0,0,0)に固定する。
    /// これにより、USB 抜線中に Emergency Black を押して黒送信が接続ガードでスキップされた場合でも、
    /// 再接続後に KeepAlive が旧点灯色を再送して点灯復活する不具合を防ぐ。
    /// </summary>
    void SetEmergencyHold(bool active);

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
    /// 再生中シーケンスを一時停止し、停止位置を API 側に保存する（POST /api/sequence/play/pause）。
    /// 続けて ResumeSequenceAsync を呼ぶことで、保存された位置から再開できる。
    /// </summary>
    /// <returns>停止対象シーケンスがあり一時停止できた場合 true</returns>
    Task<bool> PauseSequenceAsync(CancellationToken ct = default);

    /// <summary>
    /// PauseSequenceAsync で保存された位置からシーケンスを再開する（POST /api/sequence/play/resume）。
    /// </summary>
    /// <returns>再開対象があり再開できた場合 true、保留中シーケンスがなければ false</returns>
    Task<bool> ResumeSequenceAsync(CancellationToken ct = default);

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

    // --- ファイル転送 ---

    /// <summary>
    /// 2.4GHz経由でRGBファイルデータを端末に書き込む（POST /api/light/file-write/24g）
    /// </summary>
    /// <param name="rgbData">RGBデータ（バイト配列、3の倍数）</param>
    /// <param name="frameNo">書き込み先フレーム番号</param>
    Task WriteFileVia24GAsync(byte[] rgbData, uint frameNo, CancellationToken ct = default);

    /// <summary>
    /// BLEデバイスをスキャンする（GET /api/ble/scan）
    /// </summary>
    Task<IReadOnlyList<(string Address, string Name, int Rssi)>> ScanBleDevicesAsync(
        int timeoutSeconds = 5, CancellationToken ct = default);

    /// <summary>
    /// BLEデバイスに接続する（POST /api/ble/connect）
    /// </summary>
    Task ConnectBleAsync(string deviceAddress, CancellationToken ct = default);

    /// <summary>
    /// BLEデバイスを切断する（POST /api/ble/disconnect）
    /// </summary>
    Task DisconnectBleAsync(CancellationToken ct = default);

    /// <summary>
    /// BLE経由でRGBファイルデータを転送する（POST /api/ble/file-write）
    /// </summary>
    Task TransferFileBleAsync(byte[] rgbData, CancellationToken ct = default);
}
