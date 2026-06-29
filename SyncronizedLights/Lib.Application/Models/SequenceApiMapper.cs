using System;
using System.Collections.Generic;
using System.Linq;
using Lib.Domain.Enums;

namespace Lib.Application.Models
{
    /// <summary>
    /// UI ローカルモデル（<see cref="TimeBasedSequence"/> / <see cref="SequenceStep"/>）を
    /// Sequence API 形式（<see cref="SequenceApiStep"/>）に変換するヘルパー
    /// </summary>
    /// <remarks>
    ///
    /// CommandType の値はすべて API と同一：
    ///   "Color" / "Off" / "Effect" / "EffectStop" / "InternalProgram" / "Rainbow" / "RainbowStop" / "RainbowPause"
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
                // Preset は UI 側ローカルで実行するため API には送信しない
                if (step.CommandType == "Preset") continue;
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
                TransitionMs = step.TransitionMs > 0 ? step.TransitionMs : null,
            };

            switch (api.CommandType)
            {
                case "Color":
                    // 色のみ。エフェクト関連はセットしない。
                    break;

                // NO.35: Color2（2色交互点灯）廃止。旧データ互換のためフィールドは残すが
                //        新規にこの分岐へ来ることはない（Cmdドロップダウンから除外済み）。

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

                    // 繰り返し（true）／ 1 回実行後 最終色保持（false）／ 未指定（null）
                    api.Continuous = step.Continuous;

                    // Flash 時の ON/OFF 間隔を別途指定
                    if (step.EffectType == "Flash")
                    {
                        api.FlashIntervalMs = Math.Max(20, step.GetEffectCycleDurationOrDefault() / 2);
                    }
                    break;

                case "EffectStop":
                    // エフェクト停止：色・エフェクト情報は不要
                    api.R = 0;
                    api.G = 0;
                    api.B = 0;
                    break;

                case "InternalProgram":
                    // 端末内蔵プログラム再生（A1 コマンド）
                    api.FrameNo = step.FrameNo ?? 0;
                    break;

                case "Rainbow":
                    // レインボーエフェクト開始（V4.5: 0xA9 コマンド）
                    api.RainbowMode = (int)(step.RainbowMode ?? Domain.Enums.RainbowMode.Solid);
                    api.RainbowColors = step.RainbowColors?
                        .Select(c => new RgbDto(c)).ToList();
                    api.RainbowCycleDurationMs = step.RainbowCycleDurationMs ?? 1000;
                    api.RainbowBlinkPeriodMs = step.RainbowBlinkPeriodMs;
                    api.RainbowDutyRatio = step.RainbowDutyRatio;
                    api.RainbowFadeInMs = step.RainbowFadeInMs;
                    api.RainbowFadeOutMs = step.RainbowFadeOutMs;
                    break;

                case "RainbowStop":
                    // レインボー停止（色情報は不要）
                    api.R = 0; api.G = 0; api.B = 0;
                    break;

                case "RainbowPause":
                    // レインボー一時停止（0xA9 0x04: 前回色を保持。色情報・パラメータは不要）
                    api.R = 0; api.G = 0; api.B = 0;
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
