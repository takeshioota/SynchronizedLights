using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Application.Interfaces
{
    /// <summary>
    /// 設定管理機能
    /// 概要：通信ポート、送信設定、システム動作に関する設定を管理する機能。
    /// COMポート選択や送信対象設定などの運用設定を保持・更新する。
    /// </summary>
    public interface ISettingUseCase
    {
        /// <summary>
        /// 設定読み込み
        /// 概要：保存されているシステム設定を読み込む。
        /// </summary>
        Task LoadSettingsAsync();

        /// <summary>
        /// 設定保存
        /// 概要：現在のシステム設定を保存する。
        /// </summary>
        Task SaveSettingsAsync();

        /// <summary>
        /// ポート接続
        /// 概要：指定されたポートへ接続する。
        /// </summary>
        Task ConnectPortsAsync(IEnumerable<string> portNames);

        /// <summary>
        /// ポート切断
        /// 概要：接続中のポートをすべて切断する。
        /// </summary>
        Task DisconnectPortsAsync();
    }
}
