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
        public Rgb SelectedColor { get; set; } = Rgb.White;

        /// <summary>
        /// 現在選択中の速度値（ミリ秒）
        /// </summary>
        public int SpeedValueMs { get; set; } = 1000;

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
