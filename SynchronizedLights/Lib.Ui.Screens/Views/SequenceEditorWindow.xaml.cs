using System;
using System.Collections.Specialized;
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
