using System.Collections.Generic;
using System.Text.Json.Serialization;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;

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

        /// <summary>
        /// 行間遷移時間（ミリ秒）
        /// 概要：前のステップからこのステップへのスムーズフェード時間。0=即時切替。
        /// </summary>
        [JsonPropertyName("transitionMs")]
        public int? TransitionMs { get; set; }

        /// <summary>2色目 R（Color2 時のみ）</summary>
        [JsonPropertyName("r2")]
        public byte? R2 { get; set; }

        /// <summary>2色目 G（Color2 時のみ）</summary>
        [JsonPropertyName("g2")]
        public byte? G2 { get; set; }

        /// <summary>2色目 B（Color2 時のみ）</summary>
        [JsonPropertyName("b2")]
        public byte? B2 { get; set; }

        /// <summary>BPM（Color2 時のみ）</summary>
        [JsonPropertyName("bpm")]
        public int? Bpm { get; set; }

        /// <summary>内蔵プログラムのフレーム番号（CommandType="InternalProgram" 時のみ）</summary>
        [JsonPropertyName("frameNo")]
        public uint? FrameNo { get; set; }

        // ─── Rainbow（V4.5: 3.15-3.22）─────────────────────────────────────

        /// <summary>レインボーモード（CommandType="Rainbow" 時のみ）</summary>
        [JsonPropertyName("rainbowMode")]
        public int? RainbowMode { get; set; }

        /// <summary>レインボーカラーパレット（CommandType="Rainbow" 時のみ、2〜7色）</summary>
        [JsonPropertyName("rainbowColors")]
        public List<RgbDto>? RainbowColors { get; set; }

        /// <summary>色切り替え速度（ms）</summary>
        [JsonPropertyName("rainbowCycleDurationMs")]
        public int? RainbowCycleDurationMs { get; set; }

        /// <summary>点滅周期（ms）— Blink モード時のみ</summary>
        [JsonPropertyName("rainbowBlinkPeriodMs")]
        public int? RainbowBlinkPeriodMs { get; set; }

        /// <summary>点灯比率（1〜9）— Blink モード時のみ</summary>
        [JsonPropertyName("rainbowDutyRatio")]
        public byte? RainbowDutyRatio { get; set; }

        /// <summary>フェードイン時間（ms）</summary>
        [JsonPropertyName("rainbowFadeInMs")]
        public int? RainbowFadeInMs { get; set; }

        /// <summary>フェードアウト時間（ms）</summary>
        [JsonPropertyName("rainbowFadeOutMs")]
        public int? RainbowFadeOutMs { get; set; }
    }

    /// <summary>RGB 色情報の JSON 転送用 DTO</summary>
    public class RgbDto
    {
        [JsonPropertyName("r")]
        public byte R { get; set; }

        [JsonPropertyName("g")]
        public byte G { get; set; }

        [JsonPropertyName("b")]
        public byte B { get; set; }

        public RgbDto() { }
        public RgbDto(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public RgbDto(Rgb rgb) { R = rgb.R; G = rgb.G; B = rgb.B; }

        public Rgb ToRgb() => new(R, G, B);
    }
}
