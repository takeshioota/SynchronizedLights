using System;
using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// シーケンス編集ウィンドウ
    /// 概要：
    ///   - 別ウィンドウでシーケンス編集 + ステップ実行
    ///   - キーボードショートカット対応
    ///   - 起動時はプライマリ画面中央へ配置
    ///
    /// キーボードショートカット：
    ///   Space / Enter   : 次ステップへ移動（移動先は ViewModel 側の自動実行で発火）
    ///   →     / ↓      : 次ステップへ移動（同上）
    ///   ←     / ↑      : 前ステップへ移動（同上）
    ///   Esc            : 実行中エフェクトを停止
    ///   Ctrl+S         : 保存
    ///   Ctrl+N         : 新規シーケンス
    /// </summary>
    public partial class SequenceEditorWindow : Window
    {
        public SequenceEditorWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// ウィンドウ Loaded 時：プライマリ画面で最大化（XAML で WindowState=Maximized 指定済み）
        /// 概要：オペレータが編集・本番ともシーケンス編集ウィンドウしか見ない運用前提のため、
        ///       常時全画面表示とする。ユーザーが手動でサブモニタへ移したい場合は
        ///       Restore → ドラッグ → Maximize の標準操作で対応可能。
        ///       本ハンドラは Restore 状態（中央配置）のサイズ・位置をログに残すために使用。
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var primaryW = SystemParameters.PrimaryScreenWidth;
                var primaryH = SystemParameters.PrimaryScreenHeight;
                Serilog.Log.Information(
                    "SequenceEditor Loaded: WindowState={State}, PrimaryScreen={PW}x{PH}, Restored=({W}x{H})",
                    WindowState, primaryW, primaryH, Width, Height);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "SequenceEditor: Loaded ログ出力失敗（無視）");
            }
        }

        /// <summary>
        /// PreviewKeyDown：DataGrid より先にキー入力をキャッチして処理。
        /// e.Handled = true をコマンド実行前に設定するのが重要（DataGrid の二重処理防止）。
        /// </summary>
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not SequenceEditorViewModel vm) return;

            // テキスト入力中（DataGrid セル編集中含む）はショートカットを抑制
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            {
                // ただし Esc は受け付ける（停止）
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    if (vm.StopExecutionCommand.CanExecute(null))
                        vm.StopExecutionCommand.Execute(null);
                }
                return;
            }

            // Ctrl 系
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.S:
                        e.Handled = true;
                        if (vm.SaveSequenceCommand.CanExecute(null))
                            vm.SaveSequenceCommand.Execute(null);
                        return;
                    case Key.N:
                        e.Handled = true;
                        if (vm.NewSequenceCommand.CanExecute(null))
                            vm.NewSequenceCommand.Execute(null);
                        return;
                }
                return;
            }

            // 単独キー
            switch (e.Key)
            {
                case Key.Space:
                case Key.Enter:
                    e.Handled = true;     // 先に Handled を立てる（DataGrid の二重処理防止）
                    if (vm.ExecuteCurrentStepCommand.CanExecute(null))
                        vm.ExecuteCurrentStepCommand.Execute(null);
                    break;

                case Key.Right:
                case Key.Down:
                    e.Handled = true;
                    if (vm.NextStepCommand.CanExecute(null))
                        vm.NextStepCommand.Execute(null);
                    break;

                case Key.Left:
                case Key.Up:
                    e.Handled = true;
                    if (vm.PreviousStepCommand.CanExecute(null))
                        vm.PreviousStepCommand.Execute(null);
                    break;

                case Key.Escape:
                    e.Handled = true;
                    if (vm.StopExecutionCommand.CanExecute(null))
                        vm.StopExecutionCommand.Execute(null);
                    break;
            }
        }

        /// <summary>
        /// 送信ログ ListBox の Loaded ハンドラ：新エントリ追加時に最下部へ自動スクロール
        /// </summary>
        private void LogListBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ListBox listBox) return;
            if (DataContext is not SequenceEditorViewModel vm) return;

            vm.LogEntries.CollectionChanged += (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add) return;
                listBox.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (listBox.Items.Count == 0) return;
                    var lastItem = listBox.Items[listBox.Items.Count - 1];
                    if (lastItem != null) listBox.ScrollIntoView(lastItem);
                }));
            };
        }

        /// <summary>
        /// DataGrid の外側でクリックされた瞬間に、編集中セルの値を強制コミットする
        /// </summary>
        /// <remarks>
        /// 編集中セルの値が、続けて押下されたプリセットボタンの処理時点では
        /// まだソースプロパティに反映されていない場合があるため、
        /// クリックが処理される前にコミットして整合を取る。
        /// </remarks>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (StepDataGrid == null) return;

            // クリック対象が DataGrid 内なら DataGrid 自身の編集処理に任せる
            if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, StepDataGrid))
            {
                return;
            }

            // セル → 行の順でコミットする（Cell 単位で確定してから Row 単位を確定）
            try
            {
                StepDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                StepDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "StepDataGrid.CommitEdit failed");
            }
        }

        /// <summary>
        /// 指定した DependencyObject が parent の子孫かどうかを判定する
        /// </summary>
        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                          ?? LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        // 割り込みボタンが押されているかどうか（離したときに二重に Release を呼ばないため）
        private bool _interruptIsPressed;

        /// <summary>
        /// 割り込みボタン押下開始：Custom 1 の色で全 LED 点灯
        /// </summary>
        private async void InterruptButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not SequenceEditorViewModel vm) return;
            if (_interruptIsPressed) return;
            _interruptIsPressed = true;

            // ボタンへ確実にマウスキャプチャを取らせて、外にドラッグしても Release を検知できるようにする
            if (sender is System.Windows.UIElement el)
            {
                el.CaptureMouse();
            }

            await vm.InterruptPressAsync();
        }

        /// <summary>
        /// 割り込みボタン押下終了（ボタン上で離した場合）：シーケンスの続きを再生
        /// </summary>
        private async void InterruptButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            await ReleaseInterruptAsync(sender);
        }

        /// <summary>
        /// マウスがボタン外へドラッグして離された場合の保険
        /// </summary>
        private async void InterruptButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_interruptIsPressed) return;
            if (e.LeftButton == MouseButtonState.Released)
            {
                await ReleaseInterruptAsync(sender);
            }
        }

        /// <summary>
        /// マウスキャプチャを失った場合の保険（システム的に Release が来ないケースの最終手段）
        /// </summary>
        private async void InterruptButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_interruptIsPressed) return;
            await ReleaseInterruptAsync(sender);
        }

        /// <summary>
        /// 割り込み解除処理の共通入口
        /// </summary>
        private async Task ReleaseInterruptAsync(object sender)
        {
            if (!_interruptIsPressed) return;
            _interruptIsPressed = false;

            if (sender is System.Windows.UIElement el && el.IsMouseCaptured)
            {
                el.ReleaseMouseCapture();
            }

            if (DataContext is SequenceEditorViewModel vm)
            {
                await vm.InterruptReleaseAsync();
            }
        }

        /// <summary>
        /// 送信ログ ListBox の Ctrl+C：選択行をクリップボードへコピー
        /// </summary>
        private void LogListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key != Key.C) return;

            e.Handled = true;

            if (LogListBox.SelectedItems.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var item in LogListBox.SelectedItems)
            {
                if (item is LogEntry entry)
                {
                    sb.AppendLine(entry.Display);
                }
            }

            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Clipboard SetText failed");
            }
        }

        /// <summary>
        /// カスタム色プリセットの右クリック「色を編集...」メニューハンドラ
        /// 概要：ContextMenu は Visual Tree 外のため通常の Binding が効かない。
        ///       PlacementTarget（右クリックされた Button）の DataContext から
        ///       対象のカスタム色アイテムを取得し、ViewModel のコマンドを実行する。
        /// </summary>
        private void CustomColorEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not CustomColorPresetItem item) return;
            if (DataContext is not SequenceEditorViewModel vm) return;

            if (vm.EditCustomColorCommand.CanExecute(item))
            {
                vm.EditCustomColorCommand.Execute(item);
            }
        }
    }
}
