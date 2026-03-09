using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Transport.Interfaces;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Setting画面用ViewModel
    /// 概要：通信状態・ポート状態の表示を管理する。
    /// STEP⑪では Port状態表示の接続を行う。
    /// </summary>
    public partial class SettingViewModel : ObservableObject
    {
        #region フィールド
        /// <summary>
        /// 送信処理インターフェース
        /// 概要：通信状態やキュー件数の取得に使用する。
        /// </summary>
        private readonly ITransport _transport;
        #endregion フィールド

        #region プロパティ
        /// <summary>
        /// ポート状態表示文字列
        /// 概要：Connected / Disconnected を表示する。
        /// </summary>
        [ObservableProperty]
        private string portStatusText = "Port : Unknown";

        /// <summary>
        /// 接続中ポート数表示文字列
        /// 概要：現在接続されているポート件数を表示する。
        /// </summary>
        [ObservableProperty]
        private string portCountText = "Connected Ports : 0";

        /// <summary>
        /// キュー状態表示文字列
        /// 概要：現在の送信キュー件数を表示する。
        /// </summary>
        [ObservableProperty]
        private string queueStatusText = "Queue : 0";

        /// <summary>
        /// 最終エラー表示文字列
        /// 概要：直近のエラー内容を表示する。
        /// </summary>
        [ObservableProperty]
        private string errorStatusText = "Last Error : -";
        #endregion プロパティ

        #region コンストラクタ
        /// <summary>
        /// Setting画面用ViewModelを生成する。
        /// </summary>
        public SettingViewModel(ITransport transport)
        {
            _transport = transport;
            LoadStatus();
        }
        #endregion コンストラクタ

        #region コマンド
        /// <summary>
        /// 状態再読込コマンド
        /// 概要：Transportから最新状態を再取得して画面表示を更新する。
        /// </summary>
        [RelayCommand]
        private void RefreshStatus()
        {
            LoadStatus();
        }
        #endregion コマンド

        #region メソッド
        /// <summary>
        /// 通信状態を読込む
        /// 概要：Transportから状態を取得し、画面表示用プロパティへ反映する。
        /// </summary>
        private void LoadStatus()
        {
            var status = _transport.GetStatus();

            PortStatusText = status.IsConnected
                ? "Port : Connected"
                : "Port : Disconnected";

            PortCountText = $"Connected Ports : {status.ConnectedPortCount}";
            QueueStatusText = $"Queue : {status.QueueLength}";
            ErrorStatusText = $"Last Error : {status.LastError ?? "-"}";
        }
        #endregion メソッド
    }
}