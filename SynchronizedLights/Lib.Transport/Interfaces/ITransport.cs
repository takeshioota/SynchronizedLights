using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.Protocol.Packets;
using Lib.Transport.Models;

namespace Lib.Transport.Interfaces
{

    /// <summary>
    /// 送信制御インターフェース
    /// 概要：シンクロライト制御用パケットを送信するためのTransport層インターフェース。
    /// 使用可能ポートの取得、接続・切断、送信キュー登録、状態取得などの機能を定義する。
    /// </summary>

    public interface ITransport
    {
        #region メソッド
        /// <summary>
        /// 使用可能なポート一覧を取得する
        /// 概要：送信先として利用できるポート情報を一覧で返す。
        /// </summary>
        IReadOnlyList<PortInfo> ListPorts();

        /// <summary>
        /// 使用可能なポート一覧を取得する
        /// 概要：送信先として利用できるポート情報を一覧で返す。
        /// </summary>
        Task ConnectAsync(IEnumerable<string> portNames);

        /// <summary>
        /// 接続中ポートを切断する
        /// 概要：現在接続されているすべてのポートを切断する。
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// 送信キューへパケットを登録する
        /// 概要：生成済みのPacket32を送信キューに登録し、後続の送信処理に引き渡す。
        /// </summary>
        Task EnqueueAsync(Packet32 packet, SendOptions options);

        /// <summary>
        /// 現在の送信状態を取得する
        /// 概要：接続状態、接続ポート数、送信キュー数、エラー情報などを返す。
        /// </summary>
        TransportStatus GetStatus();
        #endregion メソッド

    }
}
