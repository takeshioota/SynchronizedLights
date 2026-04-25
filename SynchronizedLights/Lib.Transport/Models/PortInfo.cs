using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Transport.Models
{
    /// <summary>
    /// ポート情報
    /// 概要：送信に使用するポートの識別情報と接続状態を保持するモデル。
    /// COMポート一覧表示や接続状態管理に使用する。
    /// </summary>
    public class PortInfo
    {
        #region プロパティ
        /// <summary>
        /// ポート名
        /// 概要：システム上で識別されるポート名を表す。
        /// </summary>
        public string PortName { get; set; } = string.Empty;

        /// <summary>
        /// 表示名
        /// 概要：画面表示用のポート名称を表す。
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 接続状態
        /// 概要：該当ポートが現在接続中かどうかを表す。
        /// </summary>
        public bool IsConnected { get; set; }
        #endregion プロパティ
    }
}
