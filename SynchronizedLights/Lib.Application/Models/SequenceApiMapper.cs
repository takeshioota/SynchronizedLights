using System;
using System.Collections.Generic;

namespace Lib.Application.Models
{
    /// <summary>
    /// UI ローカルモデル（<see cref="TimeBasedSequence"/> / <see cref="SequenceStep"/>）を
    /// Sequence API 形式（<see cref="SequenceApiStep"/>）に変換するヘルパー
    /// </summary>
    /// <remarks>
    ///
    /// CommandType の値はすべて API と同一：
    ///   "Color" / "Off" / "Effect" / "EffectStop"
    /// EffectType の値も API と同一：
    ///   "Flash" / "FadeIn" / "FadeOut" / "Breathing" / "SevenColor"
    /// </remarks>
    public static class SequenceApiMapper
    {
        /// <summary>
        /// シーケンス全体を API 形式の連続ステップ列に変換する。
        /// </summary>
        public static IReadOnlyList<SequenceApiStep> ToApiSteps(TimeBasedSequence src)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));

            var list = new List<SequenceApiStep>();
            foreach (var step in src.SortedSteps)
            {
                list.Add(MapOne(step));
            }
            return list;
        }

        /// <summary>
        /// 1 ステップを API 形式へマップする。
        /// </summary>
        public static SequenceApiStep MapOne(SequenceStep step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));

            // ベース DTO（CommandType に関わらず共通項目をセット）
            var api = new SequenceApiStep
            {
                TimeOffsetMs = step.TimeMs,
                CommandType = string.IsNullOrEmpty(step.CommandType) ? "Color" : step.CommandType,
                R = step.ColorR,
                G = step.ColorG,
                B = step.ColorB,
                Field = 0,
                RetransmitCount = step.RetransmitCount,
            };

            switch (api.CommandType)
            {
                case "Color":
                    // 色のみ。エフェクト関連はセットしない。
                    break;

                case "Off":
                    // 消灯：色は API 仕様上無視されるが、互換性のため 0,0,0 を送る
                    api.R = 0;
                    api.G = 0;
                    api.B = 0;
                    break;

                case "Effect":
                    api.EffectType = step.EffectType;
                    api.EffectCycleDurationMs = step.GetEffectCycleDurationOrDefault();
                    api.FadeSteps = step.GetFadeStepsOrDefault();

                    // Flash 時は flashIntervalMs を別途指定（API 仕様）
                    if (step.EffectType == "Flash")
                    {
                        api.FlashIntervalMs = Math.Max(50, step.GetEffectCycleDurationOrDefault() / 2);
                    }
                    break;

                case "EffectStop":
                    // エフェクト停止：色・エフェクト情報は不要
                    api.R = 0;
                    api.G = 0;
                    api.B = 0;
                    break;

                default:
                    // 未知のコマンドは Color にフォールバック（白）
                    api.CommandType = "Color";
                    api.R = 255;
                    api.G = 255;
                    api.B = 255;
                    break;
            }

            return api;
        }
    }
}
