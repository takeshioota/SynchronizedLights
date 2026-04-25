using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.Protocol.Packets;
using Lib.Transport.Interfaces;
using Lib.Transport.Models;

namespace Lib.Transport.Transports
{
    /// <summary>
    /// ダミー送信処理クラス
    /// 概要：ITransportのテスト実装。
    /// 実際のシリアル通信は行わず、接続状態や送信キュー動作を確認するための仮実装として使用する。
    /// </summary>
    public class DummyTransport : ITransport
    {
        /// <summary>
        /// ダミー送信処理クラス
        /// 概要：ITransportのテスト実装。
        /// 実際のシリアル通信は行わず、接続状態や送信キュー動作を確認するための仮実装として使用する。
        /// </summary>
        private readonly List<PortInfo> _ports =
        [
            new PortInfo { PortName = "COM1", DisplayName = "Dummy Port 1", IsConnected = false },
        new PortInfo { PortName = "COM2", DisplayName = "Dummy Port 2", IsConnected = false }
        ];

        #region フィールド
        /// <summary>
        /// ダミーポート一覧
        /// 概要：テスト用に定義した仮想ポート情報を保持する。
        /// </summary>
        private readonly Queue<Packet32> _queue = new();

        /// <summary>
        /// 最終エラー情報
        /// 概要：ダミー送信処理内で発生した最新エラーを保持する。
        /// </summary>
        private string? _lastError = null;

        /// <summary>
        /// 最終エラー情報
        /// 概要：ダミー送信処理内で発生した最新エラーを保持する。
        /// </summary>
        public IReadOnlyList<PortInfo> ListPorts()
        {
            return _ports;
        }
        #endregion フィールド

        #region メソッド
        /// <summary>
        /// 指定ポートへ接続する
        /// 概要：指定されたポート名に一致するダミーポートを接続状態に更新する。
        /// </summary>
        public Task ConnectAsync(IEnumerable<string> portNames)
        {
            foreach (var port in _ports)
            {
                port.IsConnected = portNames.Contains(port.PortName);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 接続中ポートを切断する
        /// 概要：すべてのダミーポートを未接続状態に戻す。
        /// </summary>
        public Task DisconnectAsync()
        {
            foreach (var port in _ports)
            {
                port.IsConnected = false;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 接続中ポートを切断する
        /// 概要：すべてのダミーポートを未接続状態に戻す。
        /// </summary>
        public Task EnqueueAsync(Packet32 packet, SendOptions options)
        {
            _queue.Enqueue(packet);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 現在の送信状態を取得する
        /// 概要：接続状態、接続ポート数、送信キュー数、最終エラー情報をまとめて返す。
        /// </summary>
        public TransportStatus GetStatus()
        {
            return new TransportStatus
            {
                IsConnected = _ports.Any(x => x.IsConnected),
                ConnectedPortCount = _ports.Count(x => x.IsConnected),
                QueueLength = _queue.Count,
                LastError = _lastError
            };
        }
        #endregion メソッド
    }
}
