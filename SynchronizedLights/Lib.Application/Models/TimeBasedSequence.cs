using System;
using System.Collections.Generic;
using System.Linq;

namespace Lib.Application.Models
{
    /// <summary>
    /// 時間ベースシーケンス（複数ステップを時刻順に並べた演出パターン）
    /// PC 側に保存され、本番では「スタート押すだけ」でワンクリック再生される。
    ///   Name = "オープニング"
    ///   Steps = [
    ///     { TimeMs:    0, Command:"SetColor", Color:Red    },
    ///     { TimeMs: 1000, Command:"FadeIn",   Color:Red, DurationMs:2000 },
    ///     { TimeMs: 3000, Command:"SetColor", Color:Blue   },
    ///     { TimeMs: 4000, Command:"Flash",    Color:White  },
    ///   ]
    /// </summary>
    public class TimeBasedSequence
    {
        /// <summary>
        /// 一意識別子（GUID 文字列）
        /// 概要：JSON ファイル名と紐付け、シーケンス選択 / 切替再生の識別に使用。
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// シーケンス名（オペレーター表示用）
        /// 例：「オープニング」「アンコール 1」「ラスト」
        /// </summary>
        public string Name { get; set; } = "新規シーケンス";

        /// <summary>
        /// 説明・メモ（任意）
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// ステップ一覧（時刻順）
        /// 概要：TimeMs 昇順で並べることが望ましいが、保存時に強制ソートはしない
        ///       （ユーザーが手動で順序を入れ替えるケースもあるため）。
        ///       再生時は SortedSteps を使う。
        /// </summary>
        public List<SequenceStep> Steps { get; set; } = new();

        /// <summary>
        /// 作成日時（ISO 8601 文字列）
        /// </summary>
        public string CreatedAt { get; set; } = DateTime.Now.ToString("o");

        /// <summary>
        /// 最終更新日時（ISO 8601 文字列）
        /// </summary>
        public string UpdatedAt { get; set; } = DateTime.Now.ToString("o");

        /// <summary>
        /// 時刻順にソート済みのステップを返す（再生用）
        /// </summary>
        public IEnumerable<SequenceStep> SortedSteps
            => Steps.OrderBy(s => s.TimeMs);

        /// <summary>
        /// シーケンス全体の所要時間（ミリ秒）
        /// 概要：最後のステップの開始時刻 + EffectCycleDurationMs を返す。
        ///       Effect 系は終了まで時間がかかるためそれを加算する。
        ///       v2.2：新モデル準拠（DurationMs → EffectCycleDurationMs）。
        /// </summary>
        public int TotalDurationMs
        {
            get
            {
                if (Steps.Count == 0) return 0;
                var last = Steps.OrderByDescending(s => s.TimeMs + (s.EffectCycleDurationMs ?? 0)).First();
                return last.TimeMs + (last.EffectCycleDurationMs ?? 0);
            }
        }

        /// <summary>
        /// 概要表示用テキスト（一覧表示などで使用）
        /// </summary>
        public string GetSummary()
        {
            return $"{Name} （{Steps.Count} ステップ / {TotalDurationMs / 1000.0:F1} 秒）";
        }
    }
}
