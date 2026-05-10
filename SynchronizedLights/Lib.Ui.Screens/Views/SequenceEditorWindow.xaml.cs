using System;
using System.Windows;
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
        /// ウィンドウ Loaded 時：プライマリ画面中央に配置
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var primaryW = SystemParameters.PrimaryScreenWidth;
                var primaryH = SystemParameters.PrimaryScreenHeight;
                Left = (primaryW - Width) / 2;
                Top = (primaryH - Height) / 2;

                Serilog.Log.Information(
                    "SequenceEditor Loaded: PrimaryScreen={PW}x{PH}, Window=({L},{T},{W}x{H})",
                    primaryW, primaryH, Left, Top, Width, Height);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "SequenceEditor: 起動位置設定失敗（無視）");
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
    }
}
