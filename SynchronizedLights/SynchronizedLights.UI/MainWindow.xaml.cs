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

            // ViewModel 取得
            if (DataContext is not SynchronizedLights.UI.ViewModels.MainWindowViewModel vm)
            {
                vm = new SynchronizedLights.UI.ViewModels.MainWindowViewModel();
                DataContext = vm;
            }

            // App 側の静的参照登録
            App.MainVm = vm;

            // UserState 復元
            if (App.LoadedUserState != null)
            {
                vm.ApplyUserState(App.LoadedUserState);
                System.Diagnostics.Debug.WriteLine("[MainWindow] UserState applied on startup.");
            }

            // Window Closing イベントで保存
            this.Closing += OnWindowClosing;

            // 動作確認用：Ctrl+Shift+D で擬似切断（Dummy モードのみ有効）
            this.KeyDown += OnKeyDown_DebugShortcut;
        }

        /// <summary>
        /// Window が閉じられる直前に UserState を保存する
        /// </summary>
        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                if (DataContext is SynchronizedLights.UI.ViewModels.MainWindowViewModel vm)
                {
                    var state = vm.CaptureUserState();
                    App.UserStateService.Save(state);
                    System.Diagnostics.Debug.WriteLine("[MainWindow] UserState saved on closing.");
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] UserState save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// デバッグ用ショートカット：Ctrl+Shift+D で Dummy 擬似切断
        /// </summary>
        private void OnKeyDown_DebugShortcut(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.D
                && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (App.LightingFacade is Lib.Application.Facades.DummyLightingFacade dummy)
                {
                    dummy.SimulateDisconnect();
                    System.Diagnostics.Debug.WriteLine("[MainWindow] Ctrl+Shift+D: SimulateDisconnect invoked.");
                    e.Handled = true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MainWindow] Ctrl+Shift+D ignored (not Dummy mode).");
                }
            }
        }
    }
}