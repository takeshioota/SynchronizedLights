using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Transport.Models
{
    /// <summary>
    /// 接続状態
    /// 概要：該当ポートが現在接続中かどうかを表す。
    /// </summary>
    public class SendOptions
    {
        #region プロパティ
        /// <summary>
        /// 高優先送信フラグ
        /// 概要：通常送信より優先して処理する必要がある場合に指定する。
        /// </summary>
        public bool HighPriority { get; set; }

        /// <summary>
        /// 同報送信フラグ
        /// 概要：接続中の複数ポートへ同一内容を送信するかどうかを表す。
        /// true の場合は全接続先へ同じパケットを送信する。
        /// </summary>
        public bool Broadcast { get; set; } = true;
        #endregion プロパティ
    }
}
