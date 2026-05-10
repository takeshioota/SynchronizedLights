using System;
using System.Text.Json.Serialization;

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
        public string? EffectType { get; set; } = null;

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
        /// メモ・コメント（任意）
        /// 概要：ステップ作成者が自由に書ける備考。
        /// </summary>
        public string Note { get; set; } = "";

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

        /// <summary>API 送信用：FadeSteps が未設定なら既定値（20）を返す</summary>
        public int GetFadeStepsOrDefault() => FadeSteps ?? 20;

        /// <summary>API 送信用：EffectCycleDurationMs が未設定なら既定値（1000）を返す</summary>
        public int GetEffectCycleDurationOrDefault() => EffectCycleDurationMs ?? 1000;

        /// <summary>
        /// [互換] 補間ステップ数の計算ヘルパー（旧 API 互換）。
        /// 新モデルでは <see cref="GetFadeStepsOrDefault"/> を推奨。
        /// </summary>
        public int CalculateInterpolationSteps() => GetFadeStepsOrDefault();
    }
}
