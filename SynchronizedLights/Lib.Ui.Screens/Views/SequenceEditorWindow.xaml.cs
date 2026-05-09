using System;
using System.Windows;
using System.Windows.Input;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// シーケンス編集ウィンドウ（v2.1 新規）
    /// 概要：
    ///   - 別ウィンドウでシーケンス編集 + ステップ実行
    ///   - キーボードショートカット対応
    ///   - 起動時は安全にプライマリ画面中央へ配置（マルチモニター対応は後で改良）
    ///
    /// キーボードショートカット：
    ///   Space / Enter   : 現在ステップを実行（次へ自動移動）
    ///   →     / ↓      : 次ステップへ移動（実行はしない）
    ///   ←     / ↑      : 前ステップへ移動
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
        /// ウィンドウ Loaded 時：プライマリ画面中央に配置（確実に画面内に表示）。
        /// 動作確認後、マルチモニター自動配置に置き換え可能（WINDOW_NOT_SHOW_FIX.md 参照）。
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var primaryW = SystemParameters.PrimaryScreenWidth;
                var primaryH = SystemParameters.PrimaryScreenHeight;

                // プライマリ画面中央に配置（確実に画面内に表示される）
                Left = (primaryW - Width) / 2;
                Top = (primaryH - Height) / 2;

                Serilog.Log.Information(
                    "SequenceEditor Loaded: PrimaryScreen={PW}x{PH}, Window=({L},{T},{W}x{H})",
                    primaryW, primaryH, Left, Top, Width, Height);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "SequenceEditor: 起動位置設定失敗（無視して続行）");
            }
        }

        /// <summary>キーボードショートカット処理</summary>
        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not SequenceEditorViewModel vm) return;

            // テキスト入力中（TextBox 編集中）はショートカットを抑制
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            {
                // ただし Esc は受け付ける（停止）
                if (e.Key == Key.Escape)
                {
                    if (vm.StopExecutionCommand.CanExecute(null))
                        await TryExecuteAsync(() => vm.StopExecutionCommand.ExecuteAsync(null));
                    e.Handled = true;
                }
                return;
            }

            // Ctrl 系
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.S:
                        if (vm.SaveSequenceCommand.CanExecute(null))
                            vm.SaveSequenceCommand.Execute(null);
                        e.Handled = true;
                        return;
                    case Key.N:
                        if (vm.NewSequenceCommand.CanExecute(null))
                            vm.NewSequenceCommand.Execute(null);
                        e.Handled = true;
                        return;
                }
            }

            // 単独キー
            switch (e.Key)
            {
                case Key.Space:
                case Key.Enter:
                    if (vm.ExecuteCurrentStepCommand.CanExecute(null))
                        await TryExecuteAsync(() => vm.ExecuteCurrentStepCommand.ExecuteAsync(null));
                    e.Handled = true;
                    break;

                case Key.Right:
                case Key.Down:
                    if (vm.NextStepCommand.CanExecute(null))
                        vm.NextStepCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Left:
                case Key.Up:
                    if (vm.PreviousStepCommand.CanExecute(null))
                        vm.PreviousStepCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    if (vm.StopExecutionCommand.CanExecute(null))
                        await TryExecuteAsync(() => vm.StopExecutionCommand.ExecuteAsync(null));
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>非同期コマンド実行のラッパー</summary>
        private static async System.Threading.Tasks.Task TryExecuteAsync(Func<System.Threading.Tasks.Task> action)
        {
            try { await action(); }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "SequenceEditor: コマンド実行失敗");
            }
        }
    }
}
