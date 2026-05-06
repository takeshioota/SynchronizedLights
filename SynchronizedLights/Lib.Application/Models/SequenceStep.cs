using System;

namespace Lib.Application.Models
{
    /// <summary>
    /// 時間ベースシーケンスの 1 ステップを表す
    /// 構造：[シーケンス] ├ 時刻 ├ コマンド ├ 再送回数 ├ 補間設定
    /// </summary>
    public class SequenceStep
    {
        /// <summary>
        /// 開始時刻（ミリ秒）— シーケンス開始からの相対時刻
        /// 例：0 = 開始直後、1000 = 1秒後、3000 = 3秒後
        /// </summary>
        public int TimeMs { get; set; } = 0;

        /// <summary>
        /// コマンド種別
        /// 概要：実行する動作の種類を文字列で指定（Enumではなく文字列で永続化に強くする）。
        ///       仕様書の例に基づくサポート値：
        ///         "SetColor"  - 色変更（RGB指定）
        ///         "FadeIn"    - フェードイン開始（DurationMs指定）
        ///         "FadeOut"   - フェードアウト開始（DurationMs指定）
        ///         "Flash"     - 単発フラッシュ
        ///         "Breath"    - ブレス開始（DurationMs指定）
        ///         "Off"       - 全体消灯
        ///         "SevenColor"- 7色変化開始（DurationMs指定。1色あたり）
        /// </summary>
        public string Command { get; set; } = "SetColor";

        /// <summary>色 R（0〜255）— Command が SetColor / Fade* / Flash / Breath 等のときに使用</summary>
        public byte ColorR { get; set; } = 255;

        /// <summary>色 G（0〜255）</summary>
        public byte ColorG { get; set; } = 255;

        /// <summary>色 B（0〜255）</summary>
        public byte ColorB { get; set; } = 255;

        /// <summary>
        /// 演出所要時間（ミリ秒）— Fade / Breath 等で使用
        /// 概要：SetColor / Off / Flash では無視される。Fade* / Breath では総時間として使用。
        /// </summary>
        public int DurationMs { get; set; } = 1000;

        /// <summary>
        /// 再送回数（仕様書: 3〜5回）
        /// 概要：このステップ送信時に同一コマンドを何回繰り返すか。1なら再送なし。
        ///       SendOptions.RetransmitCount として API に渡す。
        /// </summary>
        public int RetransmitCount { get; set; } = 3;

        /// <summary>
        /// 補間ステップ間隔（ミリ秒、20〜100）
        /// 概要：Fade / Breath 等で「コマンド間の間隔」。FAB様回答の最速 20ms を最小値とする。
        ///       SetColor / Off / Flash では使用されない（無視）。
        /// </summary>
        public int InterpolationIntervalMs { get; set; } = 50;

        /// <summary>
        /// メモ・コメント（任意）
        /// 概要：ステップ作成者が自由に書ける備考。
        /// </summary>
        public string Note { get; set; } = "";

        /// <summary>
        /// 補間ステップ数の計算ヘルパー
        /// 概要：DurationMs ÷ InterpolationIntervalMs（最低 1）
        ///       例：1000ms / 50ms = 20 ステップ
        /// </summary>
        public int CalculateInterpolationSteps()
        {
            if (InterpolationIntervalMs <= 0) return 1;
            return Math.Max(1, DurationMs / InterpolationIntervalMs);
        }
    }
}
