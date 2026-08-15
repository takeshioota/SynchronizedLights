using System.ComponentModel;
using System.Windows;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// 接続設定ポップアップダイアログ
    /// 概要：起動時及び設定ボタン押下時にモーダル表示される。
    ///       内部に SettingView をホストし、SettingViewModel をデータコンテキストとして受け取る。
    /// </summary>
    public partial class DlgSettings : Window
    {
        public DlgSettings()
        {
            InitializeComponent();
        }

        /// <summary>
        /// SettingView のデータコンテキストを設定する
        /// </summary>
        public void SetSettingViewModel(object settingViewModel)
        {
            SettingContent.DataContext = settingViewModel;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 送信機初期化(FA/FB)中はウィンドウを閉じさせない。
        /// 概要：「閉じる」ボタンは IsEnabled で無効化済だが、タイトルバーの×や
        ///       Alt+F4 でも閉じられないよう Closing をキャンセルする。
        ///       ※FA/FB 送信自体は API 側で継続するため、この抑止は送信タイミングに影響しない。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (SettingContent.DataContext is SettingViewModel vm && vm.IsInitializing)
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
    }
}
