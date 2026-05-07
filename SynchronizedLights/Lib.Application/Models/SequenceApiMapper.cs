using System;
using System.Collections.Generic;
using System.Linq;

namespace Lib.Application.Models
{
    /// <summary>
    /// UI ローカルモデル（<see cref="TimeBasedSequence"/> / <see cref="SequenceStep"/>）を
    /// Sequence API 形式（<see cref="SequenceApiStep"/>）に変換するヘルパー。
    /// </summary>
    /// <remarks>
    /// UI コマンド名 ⇔ API CommandType / EffectType マッピング：
    ///   "SetColor"   → CommandType="Color"
    ///   "Off"        → CommandType="Off"
    ///   "FadeIn"     → CommandType="Effect", EffectType="FadeIn"
    ///   "FadeOut"    → CommandType="Effect", EffectType="FadeOut"
    ///   "Flash"      → CommandType="Effect", EffectType="Flash"
    ///   "Breath"     → CommandType="Effect", EffectType="Breathing"  ← 名前差異に注意
    ///   "SevenColor" → CommandType="Effect", EffectType="SevenColor"
    /// </remarks>
    public static class SequenceApiMapper
    {
        /// <summary>
        /// シーケンス全体を API 形式の連続ステップ列に変換する。
        /// 概要：UI 上の各 SequenceStep について、
        ///       Effect 系は Effect 開始ステップを 1 件出力する。
        ///       （Effect 停止は次のステップで自動上書きされるため、明示的な EffectStop は省略）
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
        /// 1 ステップを API 形式へマップ
        /// </summary>
        public static SequenceApiStep MapOne(SequenceStep step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));

            switch (step.Command)
            {
                case "SetColor":
                    return new SequenceApiStep
                    {
                        TimeOffsetMs = step.TimeMs,
                        CommandType = "Color",
                        R = step.ColorR,
                        G = step.ColorG,
                        B = step.ColorB,
                        Field = 0,
                        RetransmitCount = step.RetransmitCount,
                    };

                case "Off":
                    return new SequenceApiStep
                    {
                        TimeOffsetMs = step.TimeMs,
                        CommandType = "Off",
                        Field = 0,
                        RetransmitCount = step.RetransmitCount,
                    };

                case "FadeIn":
                case "FadeOut":
                case "Flash":
                case "SevenColor":
                case "Breath":
                    return new SequenceApiStep
                    {
                        TimeOffsetMs = step.TimeMs,
                        CommandType = "Effect",
                        EffectType = MapEffectType(step.Command),
                        R = step.ColorR,
                        G = step.ColorG,
                        B = step.ColorB,
                        Field = 0,
                        EffectCycleDurationMs = step.DurationMs,
                        FlashIntervalMs = step.Command == "Flash"
                            ? Math.Max(50, step.DurationMs / 2)
                            : (int?)null,
                        FadeSteps = step.CalculateInterpolationSteps(),
                        RetransmitCount = step.RetransmitCount,
                    };

                default:
                    // 未知のコマンドは Color にフォールバック（白）
                    return new SequenceApiStep
                    {
                        TimeOffsetMs = step.TimeMs,
                        CommandType = "Color",
                        R = 255,
                        G = 255,
                        B = 255,
                        Field = 0,
                    };
            }
        }

        /// <summary>UI 表記 → API EffectType への変換（"Breath" → "Breathing"）</summary>
        public static string MapEffectType(string uiCommand) => uiCommand switch
        {
            "Breath" => "Breathing",
            _ => uiCommand,
        };
    }
}
