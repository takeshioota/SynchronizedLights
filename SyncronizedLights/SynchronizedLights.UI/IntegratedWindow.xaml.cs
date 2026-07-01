using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lib.Ui.Screens.Services;
using Lib.Ui.Screens.ViewModels;
using Lib.Ui.Screens.Views;
using SynchronizedLights.UI.ViewModels;

namespace SynchronizedLights.UI
{
    /// <summary>
    /// 統合ウィンドウ
    /// 概要：シーケンス編集 + コマンド操作を1つのウィンドウに統合。
    ///       2モニタ接続時はコマンドパネルを別ウィンドウに分離する。
    ///       MainWindow と SequenceEditorWindow の機能をすべて含む。
    /// </summary>
    public partial class IntegratedWindow : Window
    {
        private readonly MainWindowViewModel _commandVm;
        private readonly SequenceEditorViewModel _sequenceVm;
        private CommandWindow? _commandWindow;

        public IntegratedWindow()
        {
            InitializeComponent();

            // ViewModel 生成
            _commandVm = new MainWindowViewModel();
            _sequenceVm = new SequenceEditorViewModel(App.LightingFacade!);

            // DataContext をコンポジションとして設定
            DataContext = new IntegratedWindowDataContext(_commandVm, _sequenceVm);

            // App 側の静的参照登録
            App.MainVm = _commandVm;

            // NO.22: カラーピッカー確定時にシーケンス行にも色を反映
            // NO.37: 反映後にその行を再実行し、変更した色を実機へ送信する（旧実装は反映のみで未送信だった）
            _commandVm.ColorPickerConfirmed += (r, g, b) =>
            {
                if (_sequenceVm.SelectedStep != null)
                {
                    _sequenceVm.SelectedStep.ColorR = r;
                    _sequenceVm.SelectedStep.ColorG = g;
                    _sequenceVm.SelectedStep.ColorB = b;
                    _sequenceVm.ReExecuteCurrentStep();
                }
            };

            // NO.40: 選択行が変わったら DataGrid を自動スクロールして常に可視にする
            _sequenceVm.RequestScrollToSelectedItem += () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (StepDataGrid.SelectedItem != null)
                        StepDataGrid.ScrollIntoView(StepDataGrid.SelectedItem);
                }));
            };

            // UserState 復元
            if (App.LoadedUserState != null)
            {
                _commandVm.ApplyUserState(App.LoadedUserState);
                // Rainbow パネル設定（色/モード/速度）は SequenceEditorViewModel 側にあるため個別復元
                _sequenceVm.LoadRainbowSettings(App.LoadedUserState);
                System.Diagnostics.Debug.WriteLine("[IntegratedWindow] UserState applied on startup.");
            }

            // Window Closing イベント
            this.Closing += OnWindowClosing;

            // デバッグショートカット
            this.KeyDown += OnKeyDown_DebugShortcut;

            // NO.33: バージョン番号を表示（タイトルバー＋ステータスバーに常時表示）
            ApplyVersionDisplay();
        }

        /// <summary>
        /// NO.33: アセンブリのバージョンをタイトルとステータスバーに反映する。
        /// </summary>
        private void ApplyVersionDisplay()
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString()
                      ?? "1.0.0";
            this.Title = $"SynchronizedLights  v{ver}";
            VersionText.Text = $"v{ver}";
        }

        #region Window ライフサイクル

        /// <summary>
        /// Window Loaded: マルチモニター検出・コマンドパネル分離
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var monitorCount = MonitorDetector.GetMonitorCount();
                Serilog.Log.Information("IntegratedWindow Loaded: MonitorCount={Count}", monitorCount);

                if (monitorCount >= 2)
                {
                    var portraitMonitor = MonitorDetector.FindPortraitMonitorWorkArea();
                    var primaryMonitor = MonitorDetector.FindPrimaryMonitorWorkArea();

                    if (portraitMonitor is { } pm)
                    {
                        // DPI スケール取得
                        var dpiScale = GetDpiScale();

                        // IntegratedWindow を縦長モニタに配置
                        this.WindowState = WindowState.Normal;
                        this.Left = pm.Left / dpiScale;
                        this.Top = pm.Top / dpiScale;
                        this.Width = pm.Width / dpiScale;
                        this.Height = pm.Height / dpiScale;
                        this.WindowState = WindowState.Maximized;

                        Serilog.Log.Information("IntegratedWindow: Moved to portrait monitor (L={L}, T={T}, W={W}, H={H})",
                            pm.Left, pm.Top, pm.Width, pm.Height);

                        // パターン選択ダイアログ
                        var patternResult = MessageBox.Show(
                            "2台のモニターが検出されました。\n\n" +
                            "パターンA: コマンドボタンを横長画面に分離\n" +
                            "パターンB: すべて縦長画面に統合（横長画面は空）\n\n" +
                            "パターンA（分離モード）にしますか？",
                            "モニター構成の選択",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (patternResult == MessageBoxResult.Yes && primaryMonitor is { } pri)
                        {
                            DetachCommandPanel(pri, dpiScale);
                        }
                        else
                        {
                            // パターンB: 縦長画面に統合（コマンドパネルは内蔵のまま）
                            Serilog.Log.Information("IntegratedWindow: Pattern B - all on portrait screen");
                        }
                    }
                }

                // 設定ダイアログを起動時に表示
                if (_commandVm.ShowSettingsDialogCommand.CanExecute(null))
                {
                    _commandVm.ShowSettingsDialogCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "IntegratedWindow Loaded failed");
            }
        }

        /// <summary>
        /// Window Closing: 終了確認 + UserState 保存 + 子ウィンドウ閉じ
        /// </summary>
        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // アプリ終了処理中（二重起動防止の Shutdown 等）はダイアログをスキップ
            if (App.IsShuttingDownDueToMutex)
            {
                return;
            }

            var result = MessageBox.Show(
                "シンクロライト制御アプリを終了しますか？",
                "終了確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // 実行中の Chase/OL/ループを停止
            try
            {
                _sequenceVm.StopLoopExecution();
            }
            catch { /* ignore */ }

            try
            {
                if (_sequenceVm.StopExecutionCommand.CanExecute(null))
                    _sequenceVm.StopExecutionCommand.Execute(null);
            }
            catch { /* ignore */ }

            // コマンドウィンドウを閉じる
            try
            {
                _commandWindow?.Close();
                _commandWindow = null;
            }
            catch { /* ignore */ }

            // UserState 保存
            try
            {
                var state = _commandVm.CaptureUserState();
                // Rainbow パネル設定を書き出してから保存
                _sequenceVm.SaveRainbowSettings(state);
                App.UserStateService.Save(state);
                System.Diagnostics.Debug.WriteLine("[IntegratedWindow] UserState saved on closing.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IntegratedWindow] UserState save failed: {ex.Message}");
            }
        }

        #region コマンドパネル分離/統合

        /// <summary>DPI スケール取得</summary>
        private double GetDpiScale()
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
                return source.CompositionTarget.TransformToDevice.M11;
            return 1.0;
        }

        /// <summary>コマンドパネルを別ウィンドウに分離</summary>
        private void DetachCommandPanel(MonitorDetector.NativeRect targetArea, double dpiScale)
        {
            InlineCommandPanel.Visibility = Visibility.Collapsed;

            _commandWindow = new CommandWindow
            {
                DataContext = _sequenceVm
            };
            _commandWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _commandWindow.Left = targetArea.Left / dpiScale;
            _commandWindow.Top = targetArea.Top / dpiScale;
            _commandWindow.Width = targetArea.Width / dpiScale;
            _commandWindow.Height = targetArea.Height / dpiScale;

            _commandWindow.Closed += OnCommandWindowClosed;
            _commandWindow.Show();
            _commandWindow.WindowState = WindowState.Maximized;

            ToggleCommandPanelButton.Content = "統合";
            Serilog.Log.Information("CommandPanel detached to separate window");
        }

        /// <summary>コマンドパネルをインラインに統合</summary>
        private void AttachCommandPanel()
        {
            if (_commandWindow != null)
            {
                _commandWindow.Closed -= OnCommandWindowClosed;
                _commandWindow.Close();
                _commandWindow = null;
            }

            InlineCommandPanel.Visibility = Visibility.Visible;
            ToggleCommandPanelButton.Content = "分離";
            Serilog.Log.Information("CommandPanel attached back inline");
        }

        /// <summary>CommandWindow が閉じられた時（×ボタン等）→ インラインに戻す</summary>
        private void OnCommandWindowClosed(object? sender, EventArgs e)
        {
            if (_commandWindow != null)
            {
                _commandWindow.Closed -= OnCommandWindowClosed;
                _commandWindow = null;
            }

            InlineCommandPanel.Visibility = Visibility.Visible;
            ToggleCommandPanelButton.Content = "分離";
            Serilog.Log.Information("CommandWindow closed by user, panel re-attached");
        }

        /// <summary>分離/統合ボタン クリック</summary>
        private void ToggleCommandPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_commandWindow != null)
            {
                // 現在分離中 → 統合
                AttachCommandPanel();
            }
            else
            {
                // 現在統合中 → 分離
                var dpiScale = GetDpiScale();

                // 2台目モニタがあればそこに配置、なければ中央表示
                var primaryMonitor = MonitorDetector.FindPrimaryMonitorWorkArea();
                var portraitMonitor = MonitorDetector.FindPortraitMonitorWorkArea();

                // IntegratedWindow がいるモニタ以外を探す
                MonitorDetector.NativeRect? targetArea = null;
                if (MonitorDetector.GetMonitorCount() >= 2)
                {
                    // IntegratedWindow が縦モニタにいる場合 → 横モニタへ
                    // IntegratedWindow が横モニタにいる場合 → 縦モニタへ
                    if (primaryMonitor is { } pri && portraitMonitor is { } por)
                    {
                        var windowCenterX = (this.Left + this.Width / 2) * dpiScale;
                        var windowCenterY = (this.Top + this.Height / 2) * dpiScale;

                        // IntegratedWindow が primary にいるなら portrait へ、逆なら primary へ
                        if (windowCenterX >= pri.Left && windowCenterX <= pri.Right
                            && windowCenterY >= pri.Top && windowCenterY <= pri.Bottom)
                        {
                            targetArea = por;
                        }
                        else
                        {
                            targetArea = pri;
                        }
                    }
                }

                if (targetArea is { } area)
                {
                    DetachCommandPanel(area, dpiScale);
                }
                else
                {
                    // シングルモニタ: ウィンドウをフローティング表示
                    InlineCommandPanel.Visibility = Visibility.Collapsed;

                    _commandWindow = new CommandWindow
                    {
                        DataContext = _sequenceVm,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Width = 800,
                        Height = 500
                    };
                    _commandWindow.Closed += OnCommandWindowClosed;
                    _commandWindow.Show();

                    ToggleCommandPanelButton.Content = "統合";
                    Serilog.Log.Information("CommandPanel detached (single monitor, floating)");
                }
            }
        }

        #endregion コマンドパネル分離/統合

        #endregion Window ライフサイクル

        #region キーボードショートカット（SequenceEditorWindow から移植）

        /// <summary>
        /// PreviewKeyDown: DataGrid より先にキー入力をキャッチ
        /// </summary>
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // テキスト入力中はショートカットを抑制（Esc のみ例外）
            if (Keyboard.FocusedElement is TextBox)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    StopAllAndRestoreGrid();
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
                        if (_sequenceVm.SaveSequenceCommand.CanExecute(null))
                            _sequenceVm.SaveSequenceCommand.Execute(null);
                        return;
                    case Key.N:
                        e.Handled = true;
                        if (_sequenceVm.NewSequenceCommand.CanExecute(null))
                            _sequenceVm.NewSequenceCommand.Execute(null);
                        return;
                }
                return;
            }

            // 単独キー
            switch (e.Key)
            {
                case Key.Space:
                case Key.Enter:
                    e.Handled = true;
                    if (_sequenceVm.ExecuteCurrentStepCommand.CanExecute(null))
                        _sequenceVm.ExecuteCurrentStepCommand.Execute(null);
                    break;
                case Key.Right:
                case Key.Down:
                    e.Handled = true;
                    if (_sequenceVm.NextStepCommand.CanExecute(null))
                        _sequenceVm.NextStepCommand.Execute(null);
                    break;
                case Key.Left:
                case Key.Up:
                    e.Handled = true;
                    if (_sequenceVm.PreviousStepCommand.CanExecute(null))
                        _sequenceVm.PreviousStepCommand.Execute(null);
                    break;
                case Key.Escape:
                    e.Handled = true;
                    StopAllAndRestoreGrid();
                    break;
            }
        }

        /// <summary>
        /// NO.29: Esc 等での全停止と、停止後の DataGrid 編集可否の復帰をまとめて行う。
        /// OL/Chase は複数行選択(Extended)で起動するため、停止後に複数選択が残ると
        /// セル編集が始められなくなる（Time(sec)〜Memo が入力不可に見える）。
        /// 停止後に単一選択へ収束し、編集可能セルとフォーカスを復帰する。
        /// </summary>
        private void StopAllAndRestoreGrid()
        {
            _sequenceVm.StopLoopExecution();
            if (_sequenceVm.StopExecutionCommand.CanExecute(null))
                _sequenceVm.StopExecutionCommand.Execute(null);

            // ループ側の最終 SelectedStep 更新が走り終えてから収束させるため遅延実行する。
            Dispatcher.BeginInvoke(new Action(RestoreGridEditableAfterStop),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// NO.29: 複数選択を解除して現在ステップ1行のみ選択し、編集可能セル＋フォーカスを復帰する。
        /// </summary>
        private void RestoreGridEditableAfterStop()
        {
            try
            {
                var current = _sequenceVm.SelectedStep;
                StepDataGrid.SelectedItems.Clear();
                if (current != null)
                {
                    StepDataGrid.SelectedItem = current;
                    var editableColumn = StepDataGrid.Columns.FirstOrDefault(c => !c.IsReadOnly);
                    if (editableColumn != null)
                        StepDataGrid.CurrentCell = new DataGridCellInfo(current, editableColumn);
                }
                StepDataGrid.Focus();
            }
            catch
            {
                // UI 状態の復帰失敗は致命ではない（停止自体は完了している）
            }
        }

        /// <summary>
        /// デバッグ用: Ctrl+Shift+D で Dummy 擬似切断
        /// </summary>
        private void OnKeyDown_DebugShortcut(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (App.LightingFacade is Lib.Application.Facades.DummyLightingFacade dummy)
                {
                    dummy.SimulateDisconnect();
                    System.Diagnostics.Debug.WriteLine("[IntegratedWindow] Ctrl+Shift+D: SimulateDisconnect invoked.");
                    e.Handled = true;
                }
            }
        }

        #endregion キーボードショートカット

        #region DataGrid イベントハンドラ（SequenceEditorWindow から移植）

        /// <summary>
        /// DataGrid セル編集開始時：Undo 用スナップショットを保存する
        /// </summary>
        private void StepDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            _sequenceVm.OnCellEditBeginning();
        }

        /// <summary>
        /// DataGrid セル編集確定時: 変更内容を即座に実機へ反映
        /// </summary>
        private void StepDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _sequenceVm.ReExecuteCurrentStep();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// DataGrid の選択変更時：選択行をViewModelに保持（v3.9: ボタン駆動に変更）
        /// </summary>
        private void StepDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not DataGrid dg) return;

            // ループ実行中のプログラム的な選択変更は無視
            if (_sequenceVm.IsLoopRunning) return;

            var selected = dg.SelectedItems
                .OfType<SequenceStepWrapper>()
                .ToList();

            // v3.9: 選択行をViewModelに保持（Chase/OLボタンで使用）
            _sequenceVm.UpdateSelectedSteps(selected);

            if (selected.Count <= 1)
            {
                // 単一行選択 → ループ停止
                _sequenceVm.StopLoopExecution();
            }
        }

        /// <summary>
        /// Chase/OL 実行中にユーザーが行をクリックしたら、その操作でループを抜ける。
        /// PreviewMouseLeftButtonDown は実マウス入力でのみ発火し、ループのプログラム的な
        /// 選択変更（エコー）では呼ばれないため、誤停止せず確実にユーザー操作だけを拾える。
        /// ここでループを止め、続くクリックの選択変更（OnSelectedStepChanged）で
        /// 移動先の off 判定＋実行が行われる。
        /// </summary>
        private void StepDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_sequenceVm.IsLoopRunning) return;

            // クリック対象がデータ行のときだけ離脱（ヘッダ・スクロールバー等は無視）
            if (e.OriginalSource is DependencyObject src && FindAncestor<DataGridRow>(src) != null)
            {
                _sequenceVm.RequestLoopExit();
                // e.Handled は立てない。クリックはそのまま継続して行選択→実行させる。
            }
        }

        /// <summary>ビジュアルツリーを遡って指定型の祖先を探す。</summary>
        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T typed) return typed;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                          ?? LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>
        /// DataGrid カラムヘッダークリック時：実コレクションを並べ替え＋全行再採番。
        /// DataGrid のビューソート（CollectionView）だけだとコレクション順と表示順が乖離し、
        /// 行追加の挿入位置がビジュアルとずれる問題を防止する。
        /// </summary>
        private void StepDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true; // DataGrid 既定のビューソートをキャンセル

            var column = e.Column;
            var newDirection = column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending;

            _sequenceVm.SortEditingStepsByColumn(column.SortMemberPath, newDirection == System.ComponentModel.ListSortDirection.Ascending);

            // ヘッダーのソートインジケーター（▲▼）を更新
            foreach (var col in StepDataGrid.Columns)
                col.SortDirection = null;
            column.SortDirection = newDirection;
        }

        /// <summary>
        /// DataGrid の外側でクリックされた瞬間に編集中セルの値を強制コミット
        /// </summary>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (StepDataGrid == null) return;

            if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, StepDataGrid))
                return;

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

        #endregion DataGrid イベントハンドラ

        #region 送信ログ

        /// <summary>
        /// 送信ログ ListBox Loaded: 新エントリ追加時に最下部へ自動スクロール
        /// </summary>
        private void LogListBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ListBox listBox) return;

            _sequenceVm.LogEntries.CollectionChanged += (_, args) =>
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
        /// 送信ログ Ctrl+C: 選択行をクリップボードへコピー
        /// </summary>
        private void LogListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || e.Key != Key.C) return;
            e.Handled = true;

            if (LogListBox.SelectedItems.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var item in LogListBox.SelectedItems)
            {
                if (item is LogEntry entry)
                    sb.AppendLine(entry.Display);
            }

            try { Clipboard.SetText(sb.ToString()); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Clipboard SetText failed"); }
        }

        #endregion 送信ログ

        #region 煽りボタン（v3.9: エディタ内に移設）

        private int _aggressivePressedIndex = -1;

        private async void AggressiveButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not AggressiveColorPresetItem item) return;
            if (_aggressivePressedIndex >= 0) return;

            var index = _sequenceVm.AggressiveColorPresets.IndexOf(item);
            if (index < 0) return;

            _aggressivePressedIndex = index;
            btn.CaptureMouse();
            await _sequenceVm.AggressivePressAsync(index);
        }

        private async void AggressiveButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            await ReleaseAggressiveAsync(sender);
        }

        private async void AggressiveButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_aggressivePressedIndex < 0) return;
            if (e.LeftButton == MouseButtonState.Released)
                await ReleaseAggressiveAsync(sender);
        }

        private async void AggressiveButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_aggressivePressedIndex < 0) return;
            await ReleaseAggressiveAsync(sender);
        }

        private async Task ReleaseAggressiveAsync(object sender)
        {
            if (_aggressivePressedIndex < 0) return;
            var index = _aggressivePressedIndex;
            _aggressivePressedIndex = -1;

            if (sender is UIElement el && el.IsMouseCaptured)
                el.ReleaseMouseCapture();

            await _sequenceVm.AggressiveReleaseAsync(index);
        }

        #endregion 煽りボタン

        #region コンテキストメニュー

        private void AggressiveColorEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not AggressiveColorPresetItem item) return;

            if (_sequenceVm.EditAggressiveColorCommand.CanExecute(item))
                _sequenceVm.EditAggressiveColorCommand.Execute(item);
        }

        private void CustomColorEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not CustomColorPresetItem item) return;

            if (_sequenceVm.EditCustomColorCommand.CanExecute(item))
                _sequenceVm.EditCustomColorCommand.Execute(item);
        }

        #endregion コンテキストメニュー
    }

    /// <summary>
    /// IntegratedWindow の DataContext として使用するコンポジションクラス
    /// </summary>
    public class IntegratedWindowDataContext
    {
        public MainWindowViewModel CommandVm { get; }
        public SequenceEditorViewModel SequenceVm { get; }

        public IntegratedWindowDataContext(MainWindowViewModel commandVm, SequenceEditorViewModel sequenceVm)
        {
            CommandVm = commandVm;
            SequenceVm = sequenceVm;
        }
    }
}
