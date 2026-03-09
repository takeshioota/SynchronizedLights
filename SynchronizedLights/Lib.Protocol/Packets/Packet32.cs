using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Protocol.Packets
{
    /// <summary>
    /// 32バイト通信パケット
    /// 概要：シンクロライト制御に使用する固定長32バイトの通信データを表すクラス。
    /// Protocol層で生成され、Transport層を通じてそのまま送信される。
    /// </summary>
    public class Packet32
    {
        #region プロパティ
        /// <summary>
        /// パケット長
        /// 概要：シンクロライト通信で使用する固定パケットサイズ（32バイト）。
        /// </summary>
        public const int Length = 32;

        /// <summary>
        /// 通信データ本体
        /// 概要：送信する32バイトの制御データを保持するバイト配列。
        /// Transport層ではこの配列をそのまま送信する。
        /// </summary>
        public byte[] Bytes { get; }
        #endregion プロパティ

        #region コンストラクタ
        /// <summary>
        /// Packet32インスタンスを生成する
        /// 概要：指定された32バイト配列を通信パケットとして保持する。
        /// 配列長が32バイトでない場合は例外を発生させる。
        /// </summary>
        public Packet32(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != Length)
            {
                throw new ArgumentException($"Packet length must be {Length} bytes.", nameof(bytes));
            }

            Bytes = bytes;
        }
        #endregion コンストラクタ

        #region メソッド
        /// <summary>
        /// 空パケットを生成する
        /// 概要：すべてのバイトが0で初期化された32バイト通信パケットを生成する。
        /// 主に初期化処理やテスト用途で使用する。
        /// </summary>
        public static Packet32 Empty()
        {
            return new Packet32(new byte[Length]);
        }
        #endregion メソッド
    }
}