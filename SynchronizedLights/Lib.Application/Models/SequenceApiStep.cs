using System.Text.Json.Serialization;

namespace Lib.Application.Models
{
    /// <summary>
    /// Sequence API（POST /api/sequence）に渡すステップ DTO
    /// 概要：UI 側ローカルモデル <see cref="SequenceStep"/> を API 形式に変換するための転送モデル。
    /// </summary>
    public class SequenceApiStep
    {
        /// <summary>シーケンス開始からの相対時刻（ミリ秒）</summary>
        [JsonPropertyName("timeOffsetMs")]
        public int TimeOffsetMs { get; set; }

        /// <summary>
        /// コマンド種別
        /// 値： "Color" / "Off" / "Effect" / "EffectStop"
        /// </summary>
        [JsonPropertyName("commandType")]
        public string CommandType { get; set; } = "Color";

        /// <summary>R 値（Color / Effect 時のみ）</summary>
        [JsonPropertyName("r")]
        public byte? R { get; set; }

        /// <summary>G 値</summary>
        [JsonPropertyName("g")]
        public byte? G { get; set; }

        /// <summary>B 値</summary>
        [JsonPropertyName("b")]
        public byte? B { get; set; }

        /// <summary>フィールド／パネル ID（既定 0）</summary>
        [JsonPropertyName("field")]
        public byte? Field { get; set; } = 0;

        /// <summary>再送回数（任意）</summary>
        [JsonPropertyName("retransmitCount")]
        public int? RetransmitCount { get; set; }

        /// <summary>
        /// Effect 種別（CommandType="Effect" 時のみ）
        /// 値： "Flash" / "FadeIn" / "FadeOut" / "Breathing" / "SevenColor"
        /// </summary>
        [JsonPropertyName("effectType")]
        public string? EffectType { get; set; }

        /// <summary>Effect の 1 サイクル時間（ミリ秒）</summary>
        [JsonPropertyName("effectCycleDurationMs")]
        public int? EffectCycleDurationMs { get; set; }

        /// <summary>Flash の ON/OFF 間隔（ミリ秒、Flash 時のみ）</summary>
        [JsonPropertyName("flashIntervalMs")]
        public int? FlashIntervalMs { get; set; }

        /// <summary>Fade 補間ステップ数</summary>
        [JsonPropertyName("fadeSteps")]
        public int? FadeSteps { get; set; }

        /// <summary>
        /// エフェクト連続実行フラグ
        /// 値： true = 繰り返し、false = 1 回実行後 最終色保持、null = 未指定（繰り返し相当）
        /// 「Fade In/In」「Fade Out/Out」のような最終色保持動作のときに false を指定する。
        /// </summary>
        [JsonPropertyName("continuous")]
        public bool? Continuous { get; set; }
    }
}
