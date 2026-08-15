using System.Windows;

namespace SynchronizedLights.UI
{
    /// <summary>
    /// コマンドウィンドウ（2モニタ時用）
    /// 概要：2台目のモニタに CommandPanelView を全画面表示する。
    ///       DataContext には MainWindowViewModel を設定する。
    /// </summary>
    public partial class CommandWindow : Window
    {
        public CommandWindow()
        {
            InitializeComponent();
        }
    }
}
