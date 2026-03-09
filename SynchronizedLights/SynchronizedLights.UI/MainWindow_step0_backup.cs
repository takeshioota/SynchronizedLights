using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using SynchronizedLights.UI.ViewModels;

namespace SynchronizedLights.UI
{
    /// <summary>
    /// メイン画面
    /// 概要：シンクロライト制御ソフトのメインウィンドウ。
    /// 各カテゴリ画面（Preset / Mode / Animation / Sequence / Setting）を切り替えて表示する。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// MainWindowを生成する
        /// 概要：メイン画面の表示部品を初期化し、
        /// MainWindowViewModelをDataContextとして設定する。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();
        }
    }
}