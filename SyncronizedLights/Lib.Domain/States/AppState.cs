using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;

namespace Lib.Domain.States
{
    /// <summary>
    /// アプリケーション状態管理
    /// 概要：シンクロライト制御ソフト全体で共有する状態を保持するクラス。
    /// 現在の画面カテゴリ、選択対象、色、速度、接続ポート、送信キュー長、エラー情報などを管理する。
    /// </summary>
    public class AppState
    {
        #region プロパティ
        /// <summary>
        /// 現在選択中の画面カテゴリ
        /// </summary>
        public UiCategory CurrentCategory { get; set; } = UiCategory.Preset;

        /// <summary>
        /// 現在選択中の制御対象
        /// </summary>
        public Target SelectedTarget { get; set; } = Target.All;

        /// <summary>
        /// 現在選択中の色
        /// </summary>
        public Rgb SelectedColor { get; set; } = new(255, 255, 255);

        /// <summary>
        /// 現在選択中の速度値（ミリ秒）
        /// 概要：Fade IN / FadeOut / Breath 等の総時間として使用される。
        /// </summary>
        public int SpeedValueMs { get; set; } = 1000;

        /// <summary>
        /// 補間ステップ間隔（ミリ秒）
        /// 概要：Fade等で「コマンドとコマンドの間隔」をms単位で指定する。
        ///       「最速 20ms」を最小値として、20〜100 の範囲で運用する。
        /// </summary>
        public int InterpolationIntervalMs { get; set; } = 50;

        /// <summary>
        /// 現在接続中のポート一覧
        /// </summary>
        public List<string> PortConnections { get; set; } = new();

        /// <summary>
        /// 現在の送信キュー数
        /// </summary>
        public int SendQueueLength { get; set; } = 0;

        /// <summary>
        /// 最後に発生したエラー情報
        /// </summary>
        public string? LastError { get; set; }
        #endregion プロパティ
    }
}
