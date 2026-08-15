using CommunityToolkit.Mvvm.ComponentModel;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// COMポート項目ViewModel
    /// 概要：Setting画面のポート一覧1行分のViewModel。
    /// ポート名、選択状態、接続状態を保持する。
    /// </summary>
    public partial class PortItemViewModel : ObservableObject
    {
        /// <summary>ポート名（例：COM3）</summary>
        public string PortName { get; }

        /// <summary>
        /// 接続対象として選択中かどうか
        /// 概要：チェックボックスで選択された状態を表す。
        /// </summary>
        [ObservableProperty]
        private bool isSelected;

        /// <summary>
        /// 実際に接続中かどうか
        /// 概要：ConnectAsync成功後にtrueになる。
        /// </summary>
        [ObservableProperty]
        private bool isConnected;

        /// <summary>接続状態の画面表示用文字列</summary>
        public string ConnectionStatusLabel => IsConnected ? "● Connected" : "○ Disconnected";

        /// <summary>PortItemViewModelを生成する</summary>
        public PortItemViewModel(string portName)
        {
            PortName = portName;
        }

        /// <summary>
        /// IsConnected変更時にラベル表示を更新する
        /// </summary>
        partial void OnIsConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(ConnectionStatusLabel));
        }
    }
}