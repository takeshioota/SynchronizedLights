using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;

namespace Lib.Application.Models
{
    /// <summary>
    /// 時間ベースシーケンスの 1 ステップを表す
    /// 構造：[シーケンス] ├ 時刻 ├ CommandType + EffectType ├ RGB ├ サイクル時間 / Fade ステップ ├ 再送回数 ├ メモ
    /// </summary>
    /// <remarks>
    /// API 仕様書 v20260510 で SequenceCommandType が 4 値化されたことを受け、
    /// 内部モデルも CommandType + EffectType の 2 軸構造に変更。
    /// </remarks>
    public class SequenceStep
    {

        /// <summary>
        /// 開始時刻（ミリ秒）— シーケンス開始からの相対時刻
        /// 例：0 = 開始直後、1000 = 1秒後、3000 = 3秒後
        /// </summary>
        public int TimeMs { get; set; } = 0;

        /// <summary>
        /// コマンドタイプ（API v20260510）
        /// 値： "Color" / "Off" / "Effect" / "EffectStop"
        /// </summary>
        public string CommandType { get; set; } = "Color";

        /// <summary>
        /// エフェクト種別（CommandType="Effect" 時のみ有効、それ以外は null）
        /// 値： "Flash" / "FadeIn" / "FadeOut" / "Breathing" / "SevenColor" / null
        /// </summary>
        /// <remarks>
        /// 入力値が "FadeInHold" / "FadeOutHold" のときは "FadeIn" / "FadeOut" + Continuous=false に
        /// 正規化して格納する（「1 回実行後 最終色保持」動作と等価）。
        /// </remarks>
        public string? EffectType
        {
            get => _effectType;
            set
            {
                switch (value)
                {
                    case "FadeInHold":
                        _effectType = "FadeIn";
                        Continuous = false;
                        break;
                    case "FadeOutHold":
                        _effectType = "FadeOut";
                        Continuous = false;
                        break;
                    default:
                        _effectType = value;
                        break;
                }
            }
        }
        private string? _effectType = null;

        /// <summary>
        /// エフェクト連続実行フラグ（CommandType="Effect" 時のみ意味を持つ）
        /// 値： null = 未指定（繰り返し相当として扱われる）、
        ///        true = 繰り返し実行（停止コマンドまで継続）、
        ///        false = 1 回実行後 最終色を保持
        /// </summary>
        /// <remarks>
        /// 用例：
        ///   - 「Fade In/In」（黒→緑→緑保持）= EffectType:"FadeIn" + Continuous:false
        ///   - 「Fade Out/Out」（緑→黒→黒保持）= EffectType:"FadeOut" + Continuous:false
        /// </remarks>
        public bool? Continuous { get; set; } = null;

        /// <summary>色 R（0〜255）</summary>
        public byte ColorR { get; set; } = 255;

        /// <summary>色 G（0〜255）</summary>
        public byte ColorG { get; set; } = 255;

        /// <summary>色 B（0〜255）</summary>
        public byte ColorB { get; set; } = 255;

        /// <summary>
        /// エフェクト サイクル時間（ms）— Effect コマンド時のみ意味を持つ
        /// 例：Breathing で 3000 = 3 秒で 1 周期
        /// </summary>
        public int? EffectCycleDurationMs { get; set; } = null;

        /// <summary>
        /// フェードステップ数 — Effect コマンド時のみ意味を持つ
        /// 例：FadeIn / FadeOut / Breathing で 20 = 20 段階の補間
        /// </summary>
        public int? FadeSteps { get; set; } = null;

        /// <summary>
        /// 再送回数（仕様書: 3〜5回）
        /// 概要：このステップ送信時に同一コマンドを何回繰り返すか。1なら再送なし。
        /// </summary>
        public int RetransmitCount { get; set; } = 3;

        /// <summary>
        /// コメント（任意、15 文字程度を想定）
        /// 概要：曲名・場面名など、現場運用で識別しやすい短い名前を入れる欄。
        /// </summary>
        public string Comment { get; set; } = "";

        /// <summary>
        /// メモ（任意、自由記述）
        /// 概要：ステップ作成者が自由に書ける備考。コメント欄より長文を想定。
        /// </summary>
        public string Note { get; set; } = "";

        /// <summary>
        /// 行間遷移時間（ミリ秒）
        /// 概要：前の行からこの行へ切り替わる際のスムーズフェード時間。
        ///       0 = 即時切替（従来動作）、N > 0 = N ミリ秒かけて色を滑らかに遷移。
        /// </summary>
        public int TransitionMs { get; set; } = 0;

        /// <summary>2色目 R（CommandType="Color2" 時のみ有効）</summary>
        public byte? Color2R { get; set; } = null;

        /// <summary>2色目 G（CommandType="Color2" 時のみ有効）</summary>
        public byte? Color2G { get; set; } = null;

        /// <summary>2色目 B（CommandType="Color2" 時のみ有効）</summary>
        public byte? Color2B { get; set; } = null;

        /// <summary>
        /// BPM（CommandType="Color2" 時のみ有効）
        /// 概要：2色交互点灯の周期。周期(ms) = 60000 / BPM。
        /// </summary>
        public int? Bpm { get; set; } = null;

        /// <summary>
        /// F1: ステップ番号（小数入力対応、例: 1.0, 1.1, 2.5）
        /// 概要：オペレータが自由に番号を振り、ソート時にこの値の昇順で並び替える。
        ///       null の場合は自動採番（行位置ベース）。
        /// </summary>
        public double? StepNumber { get; set; } = null;

        /// <summary>
        /// ロック状態（true = 選択時に実行しない）
        /// 概要：本番中に先の演出を安全に修正するため、ロックした行は
        ///       選択時の自動実行をスキップする。
        /// </summary>
        public bool IsLocked { get; set; } = false;

        /// <summary>
        /// Chase/OL グループ指定（"Chase" / "OL" / 空文字）
        /// 概要：このステップが Chase/OL グループの一員であることを永続化する。
        ///       開き直し時に Trig 列の表示（指定）を復元するために使用。
        ///       実行状態そのものではなく「指定」を保存する（自動再生はしない）。
        /// </summary>
        public string LoopTrig { get; set; } = "";

        /// <summary>
        /// サブシーケンスコメント（任意）
        /// 概要：シーケンス内の区切り・グループ名を記入する欄。
        /// </summary>
        public string SubSequenceComment { get; set; } = "";

        /// <summary>
        /// プリセットシーケンス名（CommandType="Preset" 時に参照するシーケンスの名前）
        /// 概要：このステップ実行時に、指定名のシーケンスをバックグラウンドでループ再生する。
        /// </summary>
        public string PresetSequenceName { get; set; } = "";

        /// <summary>
        /// 内蔵プログラムのフレーム番号（CommandType="InternalProgram" 時のみ有効）
        /// 概要：SNO端末に事前書き込みされたアニメーションプログラムを A1 コマンドで再生する。
        /// </summary>
        public uint? FrameNo { get; set; } = null;

        // ─── Rainbow（V4.5: 3.15-3.22）─────────────────────────────────────

        /// <summary>
        /// レインボーモード（CommandType="Rainbow" 時のみ有効）
        /// 値：Solid(0) / Blink(1) / FadeInOut(2) / FadeIn(3) / FadeOut(4) / Random(5)
        /// </summary>
        public RainbowMode? RainbowMode { get; set; } = null;

        /// <summary>
        /// レインボーカラーパレット（CommandType="Rainbow" 時のみ有効、2〜7色）
        /// 概要：0xA9 0x02 で端末に送信する色リスト。未設定時はデフォルト7色。
        /// </summary>
        public List<Rgb>? RainbowColors { get; set; } = null;

        /// <summary>色切り替え速度（ms）— 500〜2500。Solid/Random モードで使用。</summary>
        public int? RainbowCycleDurationMs { get; set; } = null;

        /// <summary>点滅周期（ms）— 100〜3600。Blink モード時のみ。</summary>
        public int? RainbowBlinkPeriodMs { get; set; } = null;

        /// <summary>点灯比率（1〜9 = 10%〜90%）— Blink モード時のみ。</summary>
        public byte? RainbowDutyRatio { get; set; } = null;

        /// <summary>フェードイン時間（ms）— 256〜3000。FadeInOut/FadeIn モード時。</summary>
        public int? RainbowFadeInMs { get; set; } = null;

        /// <summary>フェードアウト時間（ms）— 256〜3000。FadeInOut/FadeOut モード時。</summary>
        public int? RainbowFadeOutMs { get; set; } = null;

        /// <summary>
        /// 読み込み時は自動的に新モデルへマイグレートする。
        /// </summary>
        [JsonPropertyName("Command")]
        public string? Command
        {
            get => null;   // 出力時は出さない（新形式優先）
            set
            {
                if (value == null) return;
                switch (value)
                {
                    case "SetColor": CommandType = "Color"; EffectType = null; break;
                    case "Off": CommandType = "Off"; EffectType = null; break;
                    case "Flash": CommandType = "Effect"; EffectType = "Flash"; break;
                    case "FadeIn": CommandType = "Effect"; EffectType = "FadeIn"; break;
                    case "FadeOut": CommandType = "Effect"; EffectType = "FadeOut"; break;
                    case "Breath": CommandType = "Effect"; EffectType = "Breathing"; break;
                    case "SevenColor": CommandType = "Effect"; EffectType = "SevenColor"; break;
                    default: CommandType = "Color"; EffectType = null; break;
                }
            }
        }

        /// <summary>
        /// 旧 DurationMs プロパティ。
        /// <see cref="EffectCycleDurationMs"/> を使用。
        /// </summary>
        [JsonPropertyName("DurationMs")]
        public int? DurationMs
        {
            get => null;
            set
            {
                if (value.HasValue && value.Value > 0)
                {
                    // CommandType が Effect の場合のみ意味を持つが、
                    // JSON 読込順序によっては Command より先に DurationMs が来る可能性があるため、
                    // 値は素直に EffectCycleDurationMs に格納する。
                    EffectCycleDurationMs = value.Value;
                }
            }
        }

        /// <summary>
        /// 旧 InterpolationIntervalMs プロパティ。
        /// <see cref="FadeSteps"/> を使用（FadeSteps = DurationMs / InterpolationIntervalMs）。
        /// </summary>
        [JsonPropertyName("InterpolationIntervalMs")]
        public int? InterpolationIntervalMs
        {
            get => null;
            set
            {
                // 旧データに InterpolationIntervalMs があれば、FadeSteps を計算して反映
                if (value.HasValue && value.Value > 0 && EffectCycleDurationMs.HasValue)
                {
                    FadeSteps = Math.Max(1, EffectCycleDurationMs.Value / value.Value);
                }
            }
        }

        // ─── ヘルパーメソッド ───────────────────────────────────────────────

        /// <summary>このステップが Effect コマンドかどうか</summary>
        [JsonIgnore]
        public bool IsEffect => CommandType == "Effect";

        /// <summary>
        /// API 送信用：Fade補間ステップ数を返す。
        /// NO.39 修正: FadeIn/In 等は Fade(ms)=0 を既定値とする（NO.12）が、0 をそのまま API に渡すと
        /// 補間計算（stepCount&lt;1）が破綻し「動作しない／チラつく」原因になっていた。
        /// 明示設定（1以上）があればそれを使い、未設定(null)・0以下のときは周期(EffectCycleDurationMs)から
        /// 約20ms/ステップを目安に自動算出（10〜150 にクランプ）して滑らかに補間する。
        /// </summary>
        public int GetFadeStepsOrDefault()
        {
            if (FadeSteps.HasValue && FadeSteps.Value > 0) return FadeSteps.Value;
            var cycleMs = GetEffectCycleDurationOrDefault();
            return Math.Clamp(cycleMs / 20, 10, 150);
        }

        /// <summary>
        /// Effect 種別ごとの周期(ms)下限。低すぎる周期でランプが発光停止するのを防ぐ（BUG-20260729-04/05）。
        /// SevenColor=80 / Breathing=30 / Flash・FadeIn・FadeOut=20 / それ以外=0。
        /// </summary>
        public static int GetMinEffectCycleMs(string? effectType) => effectType switch
        {
            "SevenColor" => 80,
            "Breathing" => 30,
            "Flash" or "FadeIn" or "FadeOut" => 20,
            _ => 0,
        };

        /// <summary>
        /// API 送信用：EffectCycleDurationMs が未設定なら既定値（1000）を返す。
        /// BUG-20260729-04/05: Effect 時は種別別の下限（<see cref="GetMinEffectCycleMs"/>）でクランプし、
        /// UI setter / ToModel をすり抜けた低すぎる周期による発光停止を防ぐ（最終防御）。
        /// </summary>
        public int GetEffectCycleDurationOrDefault()
        {
            var v = EffectCycleDurationMs ?? 1000;
            return IsEffect ? Math.Max(GetMinEffectCycleMs(EffectType), v) : v;
        }

        /// <summary>
        /// [互換] 補間ステップ数の計算ヘルパー（旧 API 互換）。
        /// 新モデルでは <see cref="GetFadeStepsOrDefault"/> を推奨。
        /// </summary>
        public int CalculateInterpolationSteps() => GetFadeStepsOrDefault();
    }
}
