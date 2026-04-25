using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Transport.Models
{
    /// <summary>
    /// 送信状態情報
    /// 概要：Transport層の現在状態を表すモデル。
    /// 接続有無、接続ポート数、送信キュー数、最終エラー情報などを保持する。
    /// </summary>
    public class TransportStatus
    {
        #region プロパティ
        /// <summary>
        /// 送信状態情報
        /// 概要：Transport層の現在状態を表すモデル。
        /// 接続有無、接続ポート数、送信キュー数、最終エラー情報などを保持する。
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// 接続中ポート数
        /// 概要：現在接続されているポートの件数を表す。
        /// </summary>
        public int ConnectedPortCount { get; set; }

        /// <summary>
        /// 送信キュー数
        /// 概要：送信待ちとしてキューに登録されているパケット件数を表す。
        /// </summary>
        public int QueueLength { get; set; }

        /// <summary>
        /// 最終エラー情報
        /// 概要：直近で発生した送信系エラーの内容を保持する。
        /// エラーがない場合は null。
        /// </summary>
        public string? LastError { get; set; }
        #endregion プロパティ
    }
}
