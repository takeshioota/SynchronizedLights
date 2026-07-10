using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;
using Lib.Application.Models;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Lib.Ui.Screens.Views;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing.Imaging;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// シーケンス編集ウィンドウ用 ViewModel
    /// 概要：時刻ベースシーケンスの編集・保存・再生・ステップ実行を提供する。
    ///
    ///   ワークフロー：
    ///     ① 下半分でプリセット（色 + 動作）を選択 → 「Save（追加）」で
    ///        DataGrid の選択行の直後に新規行を挿入
    ///     ② DataGrid で各セルを直接編集（時刻・色・所要等）
    ///     ③ 実行モード：
    ///        Enter → 現在行を実行 + 次行へ移動
    ///        ←     → 前行へ移動 + 実行
    ///        各ステップは continuous=true で実行（次の Enter まで継続、Stop 不要）
    /// </summary>
    public partial class SequenceEditorViewModel : ObservableObject
    {
        #region フィールド

        private readonly TimeBasedSequenceStore _store;
        private readonly CustomColorStore _customColorStore;
        private readonly AggressiveColorStore _aggressiveColorStore;
        private readonly InternalProgramStore _internalProgramStore;
        private readonly ILightingFacade? _lighting;
        private readonly DispatcherTimer? _statusTimer;
        private TimeBasedSequence? _editingSequence;

        /// <summary>
        /// 選択行変更時の自動実行を一時的に抑制するフラグ
        /// 概要：シーケンス切替・空行追加・行削除・一覧再読込などプログラムから
        ///       選択を変更する場合は本フラグを立てて、不要な自動実行を防ぐ。
        /// </summary>
        private bool _suppressAutoExecute;

        /// <summary>
        /// 連続再生中、最後に送信ログに記録したステップ番号
        /// 概要：ポーリング検知で currentStepIndex の変化を見て、進んだステップごとに [TX] ログを残すために使用。
        ///       再生開始時に -1 にリセット、ポーリングで重複ログを防ぐ。
        /// </summary>
        private int _lastLoggedStepIndex = -1;

        /// <summary>
        /// 連続再生の終了を検知するための「前回の再生状態」キャッシュ
        /// </summary>
        private bool _wasPlaying;

        /// <summary>
        /// スムーズ遷移のキャンセル用トークンソース
        /// 概要：次のステップ実行で前の遷移を即座にキャンセルする。
        /// </summary>
        private CancellationTokenSource? _transitionCts;

        /// <summary>
        /// 前回実行したステップの色（スムーズ遷移の開始色として使用）
        /// </summary>
        private (byte R, byte G, byte B)? _lastExecutedColor;

        /// <summary>
        /// B7 修正: ステップ実行の排他制御用セマフォ
        /// 概要：複数のステップ実行が同時に走ると API 呼び出しが競合し、
        ///       まれに応答が得られない問題を防止する。
        /// </summary>
        private readonly SemaphoreSlim _executionSemaphore = new(1, 1);

        /// <summary>
        /// 複数行ループ実行のキャンセル用トークンソース
        /// </summary>
        private CancellationTokenSource? _loopCts;

        /// <summary>
        /// サブシーケンス（Preset）実行のキャンセル用トークンソース
        /// </summary>
        private CancellationTokenSource? _subSequenceCts;

        /// <summary>
        /// Undo 履歴スタック（各要素は編集操作直前のステップ一覧のスナップショット）
        /// </summary>
        private readonly List<List<SequenceStep>> _undoHistory = new();

        /// <summary>
        /// Undo 可能な最大回数（拡張可能：後から変更しても既存履歴に影響しない）
        /// </summary>
        private int _maxUndoDepth = 50;

        /// <summary>
        /// v3.9: DataGrid で現在選択中のステップ群（Chase/OLボタンで使用）
        /// </summary>
        private IList<SequenceStepWrapper> _currentSelectedSteps = new List<SequenceStepWrapper>();

        /// <summary>
        /// v3.9: DataGrid の選択行を保持する（IntegratedWindow から呼ばれる）
        /// </summary>
        public void UpdateSelectedSteps(IList<SequenceStepWrapper> selected)
        {
            _currentSelectedSteps = selected ?? new List<SequenceStepWrapper>();
        }

        /// <summary>
        /// Emergency モードが有効かどうか
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmergencyWhiteActive))]
        [NotifyPropertyChangedFor(nameof(IsEmergencyBlackActive))]
        private bool isEmergencyActive;

        /// <summary>
        /// Emergency モード種別（"White" / "Black" / null）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmergencyWhiteActive))]
        [NotifyPropertyChangedFor(nameof(IsEmergencyBlackActive))]
        private string? emergencyMode;

        /// <summary>Emergency（白）がアクティブか（ボタンハイライト用）</summary>
        public bool IsEmergencyWhiteActive => IsEmergencyActive && EmergencyMode == "White";

        /// <summary>Emergency（黒）がアクティブか（ボタンハイライト用）</summary>
        public bool IsEmergencyBlackActive => IsEmergencyActive && EmergencyMode == "Black";

        /// <summary>
        /// v3.9: 進行ロック — ON中は選択行の実行が継続し、別行選択/編集しても点灯は変わらない。
        /// 本番中に先のキューを安全に修正するための機能。
        /// </summary>
        [ObservableProperty]
        private bool isProgressLocked;

        /// <summary>進行ロック表示テキスト</summary>
        public string ProgressLockButtonText => IsProgressLocked ? "ロック解除" : "進行ロック";

        partial void OnIsProgressLockedChanged(bool value)
        {
            OnPropertyChanged(nameof(ProgressLockButtonText));
            AppendLog("INFO", value ? "進行ロック ON" : "進行ロック OFF");

            // ロック解除時: 現在選択中のステップを即時実行して復帰
            if (!value && SelectedStep != null && _lighting != null && _lighting.IsConnected)
            {
                _ = ExecuteStepWithoutAdvanceAsync();
            }
        }

        /// <summary>進行ロック トグル</summary>
        [RelayCommand]
        private void ToggleProgressLock()
        {
            IsProgressLocked = !IsProgressLocked;
        }

        /// <summary>
        /// DataGrid の先頭行へスクロールをリクエストするイベント（B6/F3 対応）
        /// </summary>
        public event Action? RequestScrollToFirstRow;

        /// <summary>
        /// NO.40: 選択行（SelectedStep）が見える位置までスクロールをリクエストするイベント。
        /// クリック・矢印・Enter送り・Chase/OL/再生での行送りすべてで発火する。
        /// </summary>
        public event Action? RequestScrollToSelectedItem;

        #endregion

        /// <summary>
        /// App.DataDirectory を dynamic で取得する。
        /// Lib.Ui.Screens は SynchronizedLights.UI を参照しないため、Application.Current 経由。
        /// </summary>
        private static string? GetDataDirectory()
        {
            try
            {
                // App.DataDirectory は static プロパティのためリフレクションで取得
                var appType = System.Windows.Application.Current?.GetType();
                var prop = appType?.GetProperty("DataDirectory", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return prop?.GetValue(null) as string;
            }
            catch { return null; }
        }

        #region コンストラクタ

        public SequenceEditorViewModel(ILightingFacade lighting)
        {
            var dataDir = GetDataDirectory();
            _store = new TimeBasedSequenceStore(dataDir);
            _customColorStore = new CustomColorStore(dataDir);
            _aggressiveColorStore = new AggressiveColorStore(dataDir);
            _internalProgramStore = new InternalProgramStore(dataDir);
            _lighting = lighting;

            Sequences = new ObservableCollection<TimeBasedSequence>();
            EditingSteps = new ObservableCollection<SequenceStepWrapper>();
            ColorPresets = CreateColorPresets();
            ActionPresets = CreateActionPresets();
            CustomColorPresets = LoadCustomColorPresets();
            AggressiveColorPresets = LoadAggressiveColorPresets();
            InternalProgramPresets = LoadInternalProgramPresets();
            SelectedColorPreset = ColorPresets.First();
            SelectedActionPreset = ActionPresets.First();

            ReloadSequences();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _statusTimer.Tick += async (_, _) => await PollStatusAsync();
            _statusTimer.Start();
        }

        /// <summary>
        /// 永続化されているカスタム色を ObservableCollection 化して読み込む
        /// </summary>
        private ObservableCollection<CustomColorPresetItem> LoadCustomColorPresets()
        {
            var entries = _customColorStore.LoadAll();
            var collection = new ObservableCollection<CustomColorPresetItem>();
            foreach (var e in entries)
            {
                collection.Add(new CustomColorPresetItem(e.Name, e.R, e.G, e.B));
            }
            return collection;
        }

        /// <summary>
        /// 現在のカスタム色一覧を JSON へ保存する
        /// </summary>
        private void SaveCustomColors()
        {
            var entries = CustomColorPresets.Select(c => new CustomColorEntry
            {
                Name = c.Name,
                R = c.R,
                G = c.G,
                B = c.B,
            }).ToList();
            _customColorStore.SaveAll(entries);
        }

        /// <summary>
        /// 永続化されている煽り色を ObservableCollection 化して読み込む
        /// </summary>
        private ObservableCollection<AggressiveColorPresetItem> LoadAggressiveColorPresets()
        {
            var entries = _aggressiveColorStore.LoadAll();
            var collection = new ObservableCollection<AggressiveColorPresetItem>();
            foreach (var e in entries)
            {
                collection.Add(new AggressiveColorPresetItem(e.Name, e.R, e.G, e.B));
            }
            return collection;
        }

        /// <summary>
        /// 現在の煽り色一覧を JSON へ保存する
        /// </summary>
        private void SaveAggressiveColors()
        {
            var entries = AggressiveColorPresets.Select(c => new AggressiveColorEntry
            {
                Name = c.Name,
                R = c.R,
                G = c.G,
                B = c.B,
            }).ToList();
            _aggressiveColorStore.SaveAll(entries);
        }

        /// <summary>
        /// 永続化されている内蔵プログラムプリセットを ObservableCollection 化して読み込む
        /// </summary>
        private ObservableCollection<InternalProgramPresetItem> LoadInternalProgramPresets()
        {
            var entries = _internalProgramStore.LoadAll();
            var collection = new ObservableCollection<InternalProgramPresetItem>();
            foreach (var e in entries)
            {
                collection.Add(new InternalProgramPresetItem(e.Name, e.FrameNo, e.Enabled));
            }
            return collection;
        }

        /// <summary>
        /// 現在の内蔵プログラムプリセット一覧を JSON へ保存する
        /// </summary>
        private void SaveInternalPrograms()
        {
            var entries = InternalProgramPresets.Select(p => new InternalProgramEntry
            {
                Name = p.Name,
                FrameNo = p.FrameNo,
                Enabled = p.Enabled,
            }).ToList();
            _internalProgramStore.SaveAll(entries);
        }

        /// <summary>
        /// A1 内蔵プログラムプリセットの名前・番号を編集する（右クリックメニューから呼び出し）
        /// </summary>
        [RelayCommand]
        private void EditInternalProgram(InternalProgramPresetItem? item)
        {
            if (item == null) return;

            var dlg = new DlgInternalProgramEdit
            {
                Owner = System.Windows.Application.Current?.MainWindow,
                ProgramName = item.Name,
                FrameNo = item.FrameNo,
                IsEnabled = item.Enabled,
            };

            if (dlg.ShowDialog() == true)
            {
                item.Name = dlg.ProgramName;
                item.FrameNo = dlg.FrameNo;
                item.Enabled = dlg.IsEnabled;
                SaveInternalPrograms();
                StatusMessage = $"A1 プリセット「{item.Name}」を更新（frame={item.FrameNo}）";
                AppendLog("INFO", $"A1 プリセット {item.Name} を更新 (frame={item.FrameNo}, enabled={item.Enabled})");
            }
        }

        #endregion

        #region プロパティ：シーケンス管理

        public ObservableCollection<TimeBasedSequence> Sequences { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedSequence))]
        private TimeBasedSequence? selectedSequence;

        public bool HasSelectedSequence => SelectedSequence != null;

        [ObservableProperty]
        private string editingName = "";

        partial void OnEditingNameChanged(string value)
        {
            UpdateNameValidation();
        }

        [ObservableProperty]
        private string editingDescription = "";

        /// <summary>
        /// 名前が既存シーケンスと重複しているかどうか（赤枠表示用）
        /// </summary>
        [ObservableProperty]
        private bool isNameDuplicate;

        /// <summary>
        /// 保存ボタン横に表示する名前のエラーメッセージ。重複なし時は空文字
        /// </summary>
        [ObservableProperty]
        private string nameErrorMessage = "";

        /// <summary>
        /// 現在の EditingName と編集対象 ID から重複チェックを行い、状態プロパティを更新する
        /// </summary>
        private void UpdateNameValidation()
        {
            if (_editingSequence == null || string.IsNullOrWhiteSpace(EditingName))
            {
                IsNameDuplicate = false;
                NameErrorMessage = "";
                return;
            }

            if (_store.ExistsByName(EditingName, _editingSequence.Id))
            {
                IsNameDuplicate = true;
                NameErrorMessage = "同名のシーケンスが既に存在します";
            }
            else
            {
                IsNameDuplicate = false;
                NameErrorMessage = "";
            }
        }

        public ObservableCollection<SequenceStepWrapper> EditingSteps { get; }

        /// <summary>Undo 可能かどうか（UIバインディング用）</summary>
        public bool CanUndo => _undoHistory.Count > 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
        private SequenceStepWrapper? selectedStep;

        [ObservableProperty]
        private string statusMessage = "シーケンスを選択するか、「新規」で作成してください。";

        #endregion

        #region プロパティ：ステップ実行モード

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
        private int currentStepIndex = -1;

        public string CurrentStepLabel
            => EditingSteps.Count == 0
                ? "ステップなし"
                : $"ステップ: {(CurrentStepIndex < 0 ? 0 : CurrentStepIndex + 1)} / {EditingSteps.Count}";

        #endregion

        #region プロパティ：プリセット選択

        public ObservableCollection<ColorPresetItem> ColorPresets { get; }
        public ObservableCollection<ActionPresetItem> ActionPresets { get; }

        /// <summary>
        /// カスタム色プリセット（Custom 1〜4）
        /// 概要：オペレータが現場で中間色を調整するため、ローカルに 4 件まで保存可能。
        /// </summary>
        public ObservableCollection<CustomColorPresetItem> CustomColorPresets { get; }

        /// <summary>
        /// 煽りボタン用色プリセット（煽り 1, 2）
        /// 概要：ライブ中にオペレータが押下して即興で点灯させる色プリセット。
        ///       押下中は LED を一時的に上書きし、離すと前の状態へ戻る。
        /// </summary>
        public ObservableCollection<AggressiveColorPresetItem> AggressiveColorPresets { get; }

        /// <summary>
        /// A1 内蔵プログラムプリセット（CommandPanelView の A1 ボタン群）
        /// </summary>
        public ObservableCollection<InternalProgramPresetItem> InternalProgramPresets { get; }

        [ObservableProperty]
        private ColorPresetItem? selectedColorPreset;

        [ObservableProperty]
        private ActionPresetItem? selectedActionPreset;

        /// <summary>
        /// 簡単登録の時刻間隔（ms）
        /// 概要：「簡単登録: 色 / OFF」ボタンで連続追加するときに直前ステップから何 ms ずらすか。
        /// </summary>
        [ObservableProperty]
        private int easyIntervalMs = 500;

        #endregion

        #region プロパティ：API 連携

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayButtonText))]
        private bool isPlaying;

        [ObservableProperty]
        private string? playingSequenceName;

        public string PlayButtonText => IsPlaying ? "■ 停止" : "▶ 一括再生";

        /// <summary>
        /// 連続再生中の現ステップ詳細（例：「Step 3/10: Color (255,0,0)」）
        /// 概要：ステータスバー右側に表示し、いま再生中のコマンド内容を視認できるようにする。
        /// </summary>
        [ObservableProperty]
        private string? playingStepLabel;

        /// <summary>
        /// 連続再生中の現ステップの色（HEX 文字列、ステータスバーの色プレビュー丸にバインド）
        /// </summary>
        [ObservableProperty]
        private string playingStepColorHex = "#000000";

        #endregion

        #region プロパティ：送信ログ

        /// <summary>
        /// 送信ログのエントリ列（時系列で末尾追加、上限超過分は先頭から削除）
        /// </summary>
        public ObservableCollection<LogEntry> LogEntries { get; } = new();

        /// <summary>送信ログ上限（メモリ節約のため超過分は古いものから削除）</summary>
        private const int LogEntryLimit = 500;

        /// <summary>
        /// 送信ログにエントリを追加する
        /// </summary>
        /// <param name="type">"TX" / "RX" / "INFO" / "ERR"</param>
        /// <remarks>
        /// public: 送信機初期化など他 VM（SettingViewModel 経由）のコマンドログを
        /// IntegratedWindow が本ログへ橋渡しするために公開している。UI スレッドから呼ぶこと。
        /// </remarks>
        public void AppendLog(string type, string message)
        {
            LogEntries.Add(new LogEntry { Type = type, Message = message });
            while (LogEntries.Count > LogEntryLimit)
            {
                LogEntries.RemoveAt(0);
            }
        }

        /// <summary>
        /// 送信ログをクリアする
        /// </summary>
        [RelayCommand]
        private void ClearLog()
        {
            LogEntries.Clear();
        }

        #endregion

        #region SelectedSequence 変更時

        partial void OnSelectedSequenceChanged(TimeBasedSequence? oldValue, TimeBasedSequence? newValue)
        {
            // 別シーケンス選択も Chase/OL の離脱手段。切替前にループ中だったかを記録し、
            // 離脱時に読み込んだ先頭行が off なら停止ボタン相当にする（下の実行分岐で使用）。
            bool wasLooping = IsLoopRunning;

            // FAB#1 修正: シーケンス切替前に走行中の Chase/OL ループを必ず停止する。
            // EditingSteps を作り直すと旧 wrapper が孤児化し、停止しないとループが固着→
            // 選択変更が無視され Chase/OL が再設定不能（PC再起動が必要）になる。
            StopLoopExecution();
            // 旧シーケンスの選択キャッシュ（孤児 wrapper）を破棄。再選択時に新 wrapper で更新される。
            _currentSelectedSteps = new List<SequenceStepWrapper>();

            // v3.9: 切替前のシーケンスに未保存の変更があれば自動保存
            if (oldValue != null && _editingSequence != null && HasUnsavedEdits())
            {
                SaveSequence();
                AppendLog("INFO", $"自動保存: {_editingSequence.Name}");
            }

            // v3.9: シーケンス切替時は進行ロックを自動解除
            if (IsProgressLocked)
            {
                IsProgressLocked = false;
            }

            // シーケンス切替時は Undo 履歴をクリア（別シーケンスの履歴は意味がない）
            _undoHistory.Clear();
            OnPropertyChanged(nameof(CanUndo));

            // サブシーケンス実行中なら停止
            StopSubSequence();

            // シーケンス切替時は全行を入れ替えるため、自動実行を抑制
            _suppressAutoExecute = true;
            try
            {
                if (newValue == null)
                {
                    EditingName = "";
                    EditingDescription = "";
                    EditingSteps.Clear();
                    SelectedStep = null;
                    _editingSequence = null;
                    CurrentStepIndex = -1;
                    StatusMessage = "シーケンスを選択するか、「新規」で作成してください。";
                    return;
                }

                _editingSequence = newValue;
                EditingName = newValue.Name;
                EditingDescription = newValue.Description;
                EditingSteps.Clear();
                foreach (var step in newValue.Steps)
                {
                    EditingSteps.Add(new SequenceStepWrapper(step));
                }
                RenumberEditingSteps();
                CurrentStepIndex = EditingSteps.Count > 0 ? 0 : -1;
                SelectedStep = CurrentStepIndex >= 0 ? EditingSteps[CurrentStepIndex] : null;
                OnPropertyChanged(nameof(CurrentStepLabel));
                StatusMessage = $"編集中: {newValue.Name}（{EditingSteps.Count} ステップ）";
                AppendLog("INFO", $"編集中: {newValue.Name} ({EditingSteps.Count} ステップ)");
            }
            finally
            {
                _suppressAutoExecute = false;
            }

            // B6/F3 修正: シーケンス切替時にDataGridを先頭行へスクロールする
            RequestScrollToFirstRow?.Invoke();

            // 先頭行を即時実行（実機に反映）
            // 接続チェックは ExecuteStepWithoutAdvanceAsync 内部で行うため、ここでは常に呼ぶ
            if (SelectedStep != null)
            {
                // Chase/OL からの離脱で切り替えた先頭が off（消灯 / 信号Off）なら停止ボタン相当。
                if (wasLooping && IsOffStep(SelectedStep))
                {
                    _ = StopExecutionAsync();
                }
                else
                {
                    _ = ExecuteStepWithoutAdvanceAsync();
                }
            }
        }

        /// <summary>
        /// 選択行変更時の自動実行（クリック・矢印キー・Enter での移動で発火）
        /// 概要：ユーザー操作で SelectedStep が変わった瞬間に、その行の動作を即時実行する。
        ///       プログラムから選択を変更する場合は <see cref="_suppressAutoExecute"/> で抑制される。
        /// </summary>
        partial void OnSelectedStepChanged(SequenceStepWrapper? value)
        {
            // NO.40: 選択行が変わったら常に可視化（手動ナビ＋Chase/OL/再生の行送り含む。実行抑制とは独立）
            if (value != null) RequestScrollToSelectedItem?.Invoke();

            if (_suppressAutoExecute) return;
            if (value == null) return;

            // マウスクリックによる Chase/OL 離脱要求の確定処理。
            // このフラグは実ユーザー入力（行クリック）でのみ RequestLoopExit() で立つ。
            // ループ自身のプログラム的な選択変更（＝抑制ウィンドウ外に漏れるエコー）では立たないため、
            // エコーで誤ってループ停止・off判定することはない。
            if (_loopExitRequested)
            {
                _loopExitRequested = false;
                // 離脱先が off（消灯 / 信号Off）なら、通常実行せず停止ボタン相当の全停止にする。
                if (IsOffStep(value))
                {
                    _ = StopExecutionAsync();
                    return;
                }
                // off でなければ下の通常実行へフォールスルー（ループは RequestLoopExit で停止済み）。
            }

            // サブシーケンス実行中なら停止（最終色は維持）
            StopSubSequence();

            // F8/F9: CurrentStepIndex を SelectedStep と同期（マウスクリックでの任意行ジャンプ対応）
            var idx = EditingSteps.IndexOf(value);
            if (idx >= 0 && idx != CurrentStepIndex)
            {
                CurrentStepIndex = idx;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));

            // v3.9: 進行ロック中は選択移動は許可するが、実行はブロック
            if (IsProgressLocked) return;

            if (_lighting == null || !_lighting.IsConnected) return;

            // F8/F9: 選択即実行（fire-and-forget、内部で例外は捕捉される）
            // 後だし優先: ExecuteStepWithoutAdvanceAsync 内で _transitionCts をキャンセルし、
            // 最新の選択だけが実行される
            _ = ExecuteStepWithoutAdvanceAsync();
        }

        #endregion

        #region コマンド：シーケンス管理

        [RelayCommand]
        private void NewSequence()
        {
            var newSeq = new TimeBasedSequence
            {
                Name = $"新規シーケンス_{DateTime.Now:HHmmss}",
                Description = "",
                Steps = new List<SequenceStep>()
            };

            _store.Save(newSeq);
            ReloadSequences();
            SelectedSequence = Sequences.FirstOrDefault(s => s.Id == newSeq.Id);
            StatusMessage = $"新規作成: {newSeq.Name}";
            AppendLog("INFO", $"新規作成: {newSeq.Name}");
        }

        [RelayCommand]
        private void SaveSequence()
        {
            if (_editingSequence == null) { StatusMessage = "保存対象が選択されていません。"; return; }
            if (string.IsNullOrWhiteSpace(EditingName))
            {
                NameErrorMessage = "シーケンス名を入力してください";
                IsNameDuplicate = true;
                StatusMessage = "シーケンス名を入力してください。";
                return;
            }
            if (_store.ExistsByName(EditingName, _editingSequence.Id))
            {
                IsNameDuplicate = true;
                NameErrorMessage = "同名のシーケンスが既に存在します";
                StatusMessage = $"「{EditingName}」という名前のシーケンスが既に存在します。";
                return;
            }

            // 重複なし → エラー表示をクリア
            IsNameDuplicate = false;
            NameErrorMessage = "";

            // FAB#1 修正: 保存（ReloadSequences→EditingSteps 再構築）の前に走行中ループを停止する。
            // 停止しないと旧 wrapper が孤児化してループが固着し、Chase/OL が再設定不能になる。
            StopLoopExecution();

            _editingSequence.Name = EditingName.Trim();
            _editingSequence.Description = EditingDescription ?? "";
            _editingSequence.Steps = EditingSteps.Select(w => w.ToModel()).ToList();

            var savedId = _editingSequence.Id;
            var savedName = _editingSequence.Name;
            var savedStepCount = _editingSequence.Steps.Count;

            if (_store.Save(_editingSequence))
            {
                ReloadSequences();
                SelectedSequence = Sequences.FirstOrDefault(s => s.Id == savedId);
                StatusMessage = $"保存完了: {savedName}（{savedStepCount} ステップ）";
                AppendLog("INFO", $"保存: {savedName} ({savedStepCount} ステップ)");
            }
            else
            {
                StatusMessage = "保存に失敗しました。ログを確認してください。";
                AppendLog("ERR", $"保存失敗: {savedName}");
            }
        }

        [RelayCommand]
        private void DeleteSequence()
        {
            if (SelectedSequence == null) { StatusMessage = "削除対象が選択されていません。"; return; }

            var name = SelectedSequence.Name;
            var result = MessageBox.Show(
                $"「{name}」を削除しますか？\nこの操作は取り消せません。",
                "シーケンス削除確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            if (_store.Delete(SelectedSequence.Id))
            {
                StatusMessage = $"削除完了: {name}";
                AppendLog("INFO", $"削除: {name}");
                ReloadSequences();
                SelectedSequence = null;
            }
            else
            {
                StatusMessage = $"削除に失敗しました: {name}";
                AppendLog("ERR", $"削除失敗: {name}");
            }
        }

        /// <summary>
        /// 選択中シーケンスを複製する。
        /// 概要：ストアから完全なコピー（ステップ含む）を読み込み、新しい Id を採番し、
        ///       名前は Windows のファイルコピー命名規則（「<元名> - コピー」「… - コピー (2)」…）で採番して
        ///       別ファイルとして保存する。保存後は複製をエディタで開く。
        /// </summary>
        [RelayCommand]
        private void DuplicateSequence()
        {
            if (SelectedSequence == null) { StatusMessage = "複製対象が選択されていません。"; return; }

            // ストアから読み込み直すことでステップまで含む独立したディープコピーを得る
            var copy = _store.Load(SelectedSequence.Id);
            if (copy == null)
            {
                StatusMessage = "複製元の読み込みに失敗しました。";
                AppendLog("ERR", $"複製失敗(読込): {SelectedSequence.Name}");
                return;
            }

            var sourceName = copy.Name;
            // 新規シーケンスとして採番（別ファイル化）し、Windows コピー命名で新しい名前を付ける
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = GenerateCopyName(sourceName);
            copy.CreatedAt = DateTime.Now.ToString("o");

            if (_store.Save(copy))
            {
                ReloadSequences();
                SelectedSequence = Sequences.FirstOrDefault(s => s.Id == copy.Id);
                StatusMessage = $"複製完了: {copy.Name}";
                AppendLog("INFO", $"複製: {sourceName} → {copy.Name}");
            }
            else
            {
                StatusMessage = "複製に失敗しました。ログを確認してください。";
                AppendLog("ERR", $"複製失敗: {sourceName}");
            }
        }

        /// <summary>
        /// Windows のファイルコピー命名規則に合わせた複製名を生成する。
        /// 例：「オープニング」→「オープニング - コピー」→「オープニング - コピー (2)」…
        ///     既存名と衝突しなくなるまで連番を上げる。
        /// </summary>
        private string GenerateCopyName(string baseName)
        {
            baseName = (baseName ?? "").Trim();
            var first = $"{baseName} - コピー";
            if (!_store.ExistsByName(first)) return first;
            for (int n = 2; n < 10000; n++)
            {
                var candidate = $"{baseName} - コピー ({n})";
                if (!_store.ExistsByName(candidate)) return candidate;
            }
            // フォールバック（通常到達しない）
            return $"{baseName} - コピー ({DateTime.Now:HHmmss})";
        }

        [RelayCommand]
        private void DiscardChanges()
        {
            if (_editingSequence == null) return;
            var id = _editingSequence.Id;
            ReloadSequences();
            SelectedSequence = Sequences.FirstOrDefault(s => s.Id == id);
            StatusMessage = "編集を破棄して最後の保存状態に戻しました。";
        }

        #endregion

        #region コマンド：ステップ編集

        /// <summary>選択中ステップを削除</summary>
        [RelayCommand]
        private void RemoveStep()
        {
            if (SelectedStep == null) { StatusMessage = "削除する行を選択してください。"; return; }
            SaveUndoState();
            var idx = EditingSteps.IndexOf(SelectedStep);

            // 削除に伴う選択遷移は誤操作防止のため自動実行を抑制
            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Remove(SelectedStep);
                RenumberEditingSteps();
                if (CurrentStepIndex >= EditingSteps.Count) CurrentStepIndex = EditingSteps.Count - 1;
                if (idx < EditingSteps.Count) SelectedStep = EditingSteps[idx];
                else if (EditingSteps.Count > 0) SelectedStep = EditingSteps.Last();
                else SelectedStep = null;
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = "ステップを削除しました。";
        }

        /// <summary>
        /// 選択中の行を直後に複製する
        /// 概要：内容（コマンド・色・エフェクト・メモ等）はそのままコピーし、時刻のみ +1000ms ずらす。
        ///       複製先の行が新たに選択状態となる。
        /// </summary>
        [RelayCommand]
        private void DuplicateStep()
        {
            if (SelectedStep == null)
            {
                StatusMessage = "複製する行を選択してください。";
                return;
            }
            SaveUndoState();

            var src = SelectedStep.ToModel();
            var srcIndex = EditingSteps.IndexOf(SelectedStep);

            // B1 修正: 次行が存在する場合は中間値を使い、保存時のソートで行が移動しないようにする
            int newTimeMs;
            if (srcIndex + 1 < EditingSteps.Count)
            {
                var nextTimeMs = EditingSteps[srcIndex + 1].TimeMs;
                if (nextTimeMs > src.TimeMs)
                {
                    // 次行との中間値
                    newTimeMs = src.TimeMs + (nextTimeMs - src.TimeMs) / 2;
                    // 中間値が元と同じなら +1 して少なくとも区別できるようにする
                    if (newTimeMs == src.TimeMs) newTimeMs = src.TimeMs + 1;
                }
                else
                {
                    // 次行が同じか小さい場合は +1 で挿入位置を維持
                    newTimeMs = src.TimeMs + 1;
                }
            }
            else
            {
                // 最終行の場合は +1000ms
                newTimeMs = src.TimeMs + 1000;
            }

            var newStep = new SequenceStep
            {
                TimeMs = newTimeMs,
                CommandType = src.CommandType,
                EffectType = src.EffectType,
                ColorR = src.ColorR,
                ColorG = src.ColorG,
                ColorB = src.ColorB,
                EffectCycleDurationMs = src.EffectCycleDurationMs,
                FadeSteps = src.FadeSteps,
                RetransmitCount = src.RetransmitCount,
                Note = src.Note
            };
            var wrapper = new SequenceStepWrapper(newStep);

            int insertIndex = EditingSteps.IndexOf(SelectedStep) + 1;

            // Insert によって DataGrid の CollectionChanged が発火し、
            // TwoWay バインディング経由で SelectedStep/CurrentStepIndex が
            // 一時的に変わる可能性がある。他の全操作と同様、
            // コレクション変更の前に自動実行を抑制する。
            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Insert(insertIndex, wrapper);
                RenumberEditingSteps();
                SelectedStep = wrapper;
                CurrentStepIndex = insertIndex;
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"行を複製しました（位置 {insertIndex + 1}）。";
        }

        /// <summary>
        /// 選択中の行を 1 つ上に移動
        /// </summary>
        [RelayCommand]
        private void MoveStepUp()
        {
            if (SelectedStep == null)
            {
                StatusMessage = "移動する行を選択してください。";
                return;
            }
            var idx = EditingSteps.IndexOf(SelectedStep);
            if (idx <= 0)
            {
                StatusMessage = "これより上には移動できません。";
                return;
            }
            SaveUndoState();

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Move(idx, idx - 1);
                RenumberEditingSteps();
                CurrentStepIndex = idx - 1;
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"行を上に移動:{idx + 1} → {idx}";
        }

        /// <summary>
        /// 選択中の行を 1 つ下に移動
        /// </summary>
        [RelayCommand]
        private void MoveStepDown()
        {
            if (SelectedStep == null)
            {
                StatusMessage = "移動する行を選択してください。";
                return;
            }
            var idx = EditingSteps.IndexOf(SelectedStep);
            if (idx >= EditingSteps.Count - 1)
            {
                StatusMessage = "これより下には移動できません。";
                return;
            }
            SaveUndoState();

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Move(idx, idx + 1);
                RenumberEditingSteps();
                CurrentStepIndex = idx + 1;
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"行を下に移動：{idx + 1} → {idx + 2}";
        }

        /// <summary>
        /// 全ステップを TimeMs 昇順でソート
        /// </summary>
        [RelayCommand]
        private void SortByTime()
        {
            if (EditingSteps.Count == 0)
            {
                StatusMessage = "ソート対象のステップがありません。";
                return;
            }
            SaveUndoState();

            _suppressAutoExecute = true;
            try
            {
                var sorted = EditingSteps.OrderBy(w => w.TimeMs).ToList();
                EditingSteps.Clear();
                foreach (var w in sorted) EditingSteps.Add(w);
                RenumberEditingSteps();

                if (EditingSteps.Count > 0)
                {
                    CurrentStepIndex = 0;
                    SelectedStep = EditingSteps[0];
                }
                else
                {
                    CurrentStepIndex = -1;
                    SelectedStep = null;
                }
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"時刻順にソートしました（{EditingSteps.Count} 件）。";
        }

        /// <summary>
        /// F2: 全ステップをステップ番号（No列）昇順でソート
        /// 概要：ユーザーが手入力した番号（1.1, 2.5 等）の昇順に並び替える。
        /// </summary>
        [RelayCommand]
        private void SortByStepNumber()
        {
            if (EditingSteps.Count == 0)
            {
                StatusMessage = "ソート対象のステップがありません。";
                return;
            }
            SaveUndoState();

            _suppressAutoExecute = true;
            try
            {
                var sorted = EditingSteps.OrderBy(w => w.RowNumber).ToList();
                EditingSteps.Clear();
                foreach (var w in sorted) EditingSteps.Add(w);
                RenumberEditingSteps();

                if (EditingSteps.Count > 0)
                {
                    CurrentStepIndex = 0;
                    SelectedStep = EditingSteps[0];
                }
                else
                {
                    CurrentStepIndex = -1;
                    SelectedStep = null;
                }
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"ステップ番号順にソートしました（{EditingSteps.Count} 件）。";
        }

        /// <summary>
        /// 全ステップをクリア（確認ダイアログあり）
        /// 概要：UI 上のみクリアし、JSON への書き込みは「保存」が押されるまで行わない。
        ///       「編集破棄」で元の状態に戻すことができる。
        /// </summary>
        [RelayCommand]
        private void ClearAllSteps()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択してください。";
                return;
            }
            if (EditingSteps.Count == 0)
            {
                StatusMessage = "ステップは既に空です。";
                return;
            }

            var ans = MessageBox.Show(
                $"全てのステップ（{EditingSteps.Count} 件）を削除しますか？\n\n「保存」を押すまでは未確定です。元に戻したい場合は「編集破棄」でリセットできます。",
                "全クリア確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (ans != MessageBoxResult.Yes) return;
            SaveUndoState();

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Clear();
                RenumberEditingSteps();
                SelectedStep = null;
                CurrentStepIndex = -1;
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = "全ステップをクリアしました。「保存」を押すと確定、「編集破棄」で元に戻せます。";
        }

        /// <summary>
        /// 選択中の行の直後にテンプレート由来のステップ群を挿入する共通ヘルパー
        /// 概要：選択行があればその直後、なければ末尾に挿入する。
        ///       1 件目の開始時刻 = 挿入基準行 TimeMs + intervalMs（intervalMs=0 の場合は +1000ms）。
        ///       以降は intervalMs ずつ加算していく。
        ///       挿入された 1 件目を選択状態にする（自動実行は抑制）。
        /// </summary>
        private void InsertStepsAfterSelected(IReadOnlyList<SequenceStep> steps, int intervalMs)
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }
            if (steps == null || steps.Count == 0) return;
            SaveUndoState();

            int interval = Math.Max(0, intervalMs);
            int insertIndex;
            int baseTimeMs;
            if (SelectedStep != null && EditingSteps.Contains(SelectedStep))
            {
                var selIdx = EditingSteps.IndexOf(SelectedStep);
                insertIndex = selIdx + 1;
                int desiredBase = SelectedStep.TimeMs + (interval > 0 ? interval : 1000);

                // B5 修正: 後続行と時刻が重複しないよう調整する
                // 挿入先の直後の行（既存行）のTimeMsを確認し、重複しそうなら中間値を使う
                int totalInsertSpan = steps.Count * Math.Max(interval, 1);
                if (insertIndex < EditingSteps.Count)
                {
                    int nextTimeMs = EditingSteps[insertIndex].TimeMs;
                    // 挿入する全ステップの末尾時刻が後続行を超える場合、スペースを等分する
                    if (desiredBase + totalInsertSpan - Math.Max(interval, 1) >= nextTimeMs
                        && nextTimeMs > SelectedStep.TimeMs)
                    {
                        int availableSpace = nextTimeMs - SelectedStep.TimeMs;
                        int stepInterval = Math.Max(1, availableSpace / (steps.Count + 1));
                        desiredBase = SelectedStep.TimeMs + stepInterval;
                        interval = stepInterval;
                    }
                }
                baseTimeMs = desiredBase;
            }
            else if (EditingSteps.Count > 0)
            {
                insertIndex = EditingSteps.Count;
                baseTimeMs = EditingSteps.Last().TimeMs + (interval > 0 ? interval : 1000);
            }
            else
            {
                insertIndex = 0;
                baseTimeMs = 0;
            }

            int firstIndex = insertIndex;
            _suppressAutoExecute = true;
            try
            {
                int currentIndex = insertIndex;
                foreach (var step in steps)
                {
                    step.TimeMs = baseTimeMs;
                    if (step.RetransmitCount <= 0) step.RetransmitCount = 3;
                    EditingSteps.Insert(currentIndex, new SequenceStepWrapper(step));
                    currentIndex++;
                    baseTimeMs += interval;
                }
                RenumberEditingSteps();

                if (firstIndex < EditingSteps.Count)
                {
                    CurrentStepIndex = firstIndex;
                    SelectedStep = EditingSteps[firstIndex];
                }
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
        }

        /// <summary>
        /// 簡単登録：選択中の色プリセットで Color ステップを末尾に追加（時刻は EasyIntervalMs ずらす）
        /// </summary>
        [RelayCommand]
        private void EasyAddColor()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }
            if (SelectedColorPreset == null)
            {
                StatusMessage = "上の色プリセットを選択してから「簡単登録: 色」を押してください。";
                return;
            }

            var step = new SequenceStep
            {
                CommandType = "Color",
                EffectType = null,
                ColorR = SelectedColorPreset.R,
                ColorG = SelectedColorPreset.G,
                ColorB = SelectedColorPreset.B,
                Note = $"簡単登録: {SelectedColorPreset.Name}"
            };
            InsertStepsAfterSelected(new[] { step }, EasyIntervalMs);
            StatusMessage = $"簡単登録：{SelectedColorPreset.Name} を追加（間隔 {EasyIntervalMs}ms）。";
        }

        /// <summary>
        /// 簡単登録：Off ステップを末尾に追加
        /// </summary>
        [RelayCommand]
        private void EasyAddOff()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }

            var step = new SequenceStep
            {
                CommandType = "Off",
                EffectType = null,
                ColorR = 0, ColorG = 0, ColorB = 0,
                Note = "簡単登録: OFF"
            };
            InsertStepsAfterSelected(new[] { step }, EasyIntervalMs);
            StatusMessage = $"簡単登録：OFF を追加（間隔 {EasyIntervalMs}ms）。";
        }

        /// <summary>
        /// テンプレート：色変更（7色を 1000ms 間隔で切り替える Color ステップ 7 件を末尾に追加）
        /// </summary>
        [RelayCommand]
        private void ApplyTemplateColorChange()
        {
            var palette = new (string Name, byte R, byte G, byte B)[]
            {
                ("Red",     255,   0,   0),
                ("Orange",  255, 128,   0),
                ("Yellow",  255, 255,   0),
                ("Green",     0, 255,   0),
                ("Cyan",      0, 255, 255),
                ("Blue",      0,   0, 255),
                ("Magenta", 255,   0, 255),
            };
            var steps = palette.Select(c => new SequenceStep
            {
                CommandType = "Color",
                EffectType = null,
                ColorR = c.R, ColorG = c.G, ColorB = c.B,
                Note = $"色変更: {c.Name}"
            }).ToList();

            InsertStepsAfterSelected(steps, intervalMs: 1000);
            StatusMessage = "テンプレ：色変更（7 色 × 1000ms）を追加しました。";
        }

        /// <summary>
        /// テンプレート：呼吸（Breathing 1 件）
        /// </summary>
        [RelayCommand]
        private void ApplyTemplateBreathing()
        {
            var step = new SequenceStep
            {
                CommandType = "Effect",
                EffectType = "Breathing",
                ColorR = 255, ColorG = 255, ColorB = 255,
                EffectCycleDurationMs = 3000,
                FadeSteps = 30,
                Note = "テンプレ: 呼吸"
            };
            InsertStepsAfterSelected(new[] { step }, intervalMs: 0);
            StatusMessage = "テンプレ：呼吸（Breathing / 周期 3000ms / Fade 30）を追加しました。";
        }

        /// <summary>
        /// テンプレート：Flash（Flash 1 件）
        /// </summary>
        [RelayCommand]
        private void ApplyTemplateFlash()
        {
            var step = new SequenceStep
            {
                CommandType = "Effect",
                EffectType = "Flash",
                ColorR = 255, ColorG = 255, ColorB = 255,
                EffectCycleDurationMs = 500,
                FadeSteps = 0,
                Note = "テンプレ: Flash"
            };
            InsertStepsAfterSelected(new[] { step }, intervalMs: 0);
            StatusMessage = "テンプレ：Flash（周期 500ms）を追加しました。";
        }

        /// <summary>
        /// テンプレート：7 色（SevenColor 1 件）
        /// </summary>
        [RelayCommand]
        private void ApplyTemplateSevenColor()
        {
            var step = new SequenceStep
            {
                CommandType = "Effect",
                EffectType = "SevenColor",
                ColorR = 255, ColorG = 0, ColorB = 0,
                EffectCycleDurationMs = 7000,
                FadeSteps = 20,
                Note = "テンプレ: 7 色"
            };
            InsertStepsAfterSelected(new[] { step }, intervalMs: 0);
            StatusMessage = "テンプレ：7 色（SevenColor / 周期 7000ms / Fade 20）を追加しました。";
        }

        /// <summary>
        /// テンプレート：カウントダウン（5 → 1 を色変化で表現 + Off）
        /// </summary>
        [RelayCommand]
        private void ApplyTemplateCountdown()
        {
            var steps = new[]
            {
                new SequenceStep { CommandType = "Color", ColorR = 255, ColorG =   0, ColorB =   0, Note = "カウントダウン: 5" },
                new SequenceStep { CommandType = "Color", ColorR = 255, ColorG = 128, ColorB =   0, Note = "カウントダウン: 4" },
                new SequenceStep { CommandType = "Color", ColorR = 255, ColorG = 255, ColorB =   0, Note = "カウントダウン: 3" },
                new SequenceStep { CommandType = "Color", ColorR =   0, ColorG = 255, ColorB =   0, Note = "カウントダウン: 2" },
                new SequenceStep { CommandType = "Color", ColorR = 255, ColorG = 255, ColorB = 255, Note = "カウントダウン: 1" },
                new SequenceStep { CommandType = "Off",   ColorR =   0, ColorG =   0, ColorB =   0, Note = "カウントダウン: 0" },
            };
            InsertStepsAfterSelected(steps, intervalMs: 1000);
            StatusMessage = "テンプレ：カウントダウン（5 → 0、1 秒間隔）を追加しました。";
        }

        /// <summary>
        /// F10: テンプレート：2色連続 Fade In
        /// 概要：色1→FadeIn/In（保持）→色2→FadeIn/In（保持）の4ステップ。
        ///       それぞれの時間は EffectCycleDurationMs で変更可能。
        /// </summary>
        [RelayCommand]
        private void ApplyTemplateTwoColorFadeIn()
        {
            var steps = new[]
            {
                new SequenceStep
                {
                    CommandType = "Effect", EffectType = "FadeIn",
                    ColorR = 255, ColorG = 0, ColorB = 0,
                    EffectCycleDurationMs = 2000, FadeSteps = 20,
                    Continuous = false,
                    Comment = "色1 FadeIn",
                    Note = "テンプレ: 2色FadeIn (色1)"
                },
                new SequenceStep
                {
                    CommandType = "Effect", EffectType = "FadeIn",
                    ColorR = 0, ColorG = 0, ColorB = 255,
                    EffectCycleDurationMs = 2000, FadeSteps = 20,
                    Continuous = false,
                    Comment = "色2 FadeIn",
                    Note = "テンプレ: 2色FadeIn (色2)"
                },
            };
            InsertStepsAfterSelected(steps, intervalMs: 2000);
            StatusMessage = "テンプレ：2色連続 Fade In（各 2000ms）を追加しました。時間は列で変更可能。";
        }

        /// <summary>
        /// F10: テンプレート：Fade In &amp; Fade Out
        /// 概要：FadeIn/In（保持）→FadeOut/Out（保持）の2ステップ。
        ///       それぞれの時間は EffectCycleDurationMs で変更可能。
        /// </summary>
        [RelayCommand]
        private void ApplyTemplateFadeInOut()
        {
            var steps = new[]
            {
                new SequenceStep
                {
                    CommandType = "Effect", EffectType = "FadeIn",
                    ColorR = 255, ColorG = 255, ColorB = 255,
                    EffectCycleDurationMs = 2000, FadeSteps = 20,
                    Continuous = false,
                    Comment = "Fade In",
                    Note = "テンプレ: FadeIn&Out (In)"
                },
                new SequenceStep
                {
                    CommandType = "Effect", EffectType = "FadeOut",
                    ColorR = 255, ColorG = 255, ColorB = 255,
                    EffectCycleDurationMs = 2000, FadeSteps = 20,
                    Continuous = false,
                    Comment = "Fade Out",
                    Note = "テンプレ: FadeIn&Out (Out)"
                },
            };
            InsertStepsAfterSelected(steps, intervalMs: 2000);
            StatusMessage = "テンプレ：Fade In & Fade Out（各 2000ms）を追加しました。時間は列で変更可能。";
        }

        #endregion

        #region コマンド：A1 内蔵プログラム

        /// <summary>A1テスト送信用フレーム番号（UI バインド）</summary>
        [ObservableProperty]
        private uint a1FrameNo;

        /// <summary>
        /// A1 内蔵プログラムを即時実行する（コマンドパネルのボタンから呼び出し）
        /// </summary>
        [RelayCommand]
        private async Task PlayInternalProgram(uint frameNo)
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            if (IsEmergencyActive)
            {
                StatusMessage = "Emergency モード中: 照明操作は抑止されています。";
                return;
            }
            try
            {
                await _lighting.PlayInternalProgramAsync(frameNo);
                AppendLog("TX", $"A1 InternalProgram frame={frameNo}");
                StatusMessage = $"A1 内蔵プログラム再生 → frame={frameNo}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"A1 実行失敗: {ex.Message}";
                AppendLog("ERR", $"A1 InternalProgram failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A1テスト送信（FrameNo入力欄の値を送信）
        /// </summary>
        [RelayCommand]
        private async Task SendA1Test()
        {
            await PlayInternalProgram(A1FrameNo);
        }

        /// <summary>
        /// A1 内蔵プログラムを停止する（A2黒で上書き）
        /// </summary>
        [RelayCommand]
        private async Task StopInternalProgram()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            try
            {
                await _lighting.StopInternalProgramAsync();
                AppendLog("TX", "A1 InternalProgram Stop");
                StatusMessage = "A1 内蔵プログラム停止";
            }
            catch (Exception ex)
            {
                StatusMessage = $"A1 停止失敗: {ex.Message}";
                AppendLog("ERR", $"A1 InternalProgram Stop failed: {ex.Message}");
            }
        }

        #endregion

        #region コマンド：Rainbow レインボー（V4.5: 3.15-3.22）

        /// <summary>レインボーモード（UI ComboBox バインド）</summary>
        [ObservableProperty]
        private RainbowMode rainbowMode = RainbowMode.Solid;

        /// <summary>レインボーモードのインデックス（ComboBox SelectedIndex バインド用）</summary>
        public int RainbowModeIndex
        {
            get => (int)RainbowMode;
            set
            {
                if (value >= 0 && value <= 5)
                {
                    RainbowMode = (RainbowMode)value;
                    OnPropertyChanged();
                }
            }
        }

        partial void OnRainbowModeChanged(RainbowMode value) => OnPropertyChanged(nameof(RainbowModeIndex));

        /// <summary>レインボーカラーパレット（UI バインド、2〜7色）</summary>
        [ObservableProperty]
        private ObservableCollection<RgbColorItem> rainbowColors = new(DefaultRainbowColors());

        /// <summary>色切り替え速度（ms）</summary>
        [ObservableProperty]
        private int rainbowCycleDurationMs = 1000;

        /// <summary>点滅周期（ms）— Blink モード</summary>
        [ObservableProperty]
        private int rainbowBlinkPeriodMs = 500;

        /// <summary>点灯比率（1〜9）— Blink モード</summary>
        private byte _rainbowDutyRatio = 5;
        public byte RainbowDutyRatio
        {
            get => _rainbowDutyRatio;
            set
            {
                if (_rainbowDutyRatio != value)
                {
                    _rainbowDutyRatio = Math.Clamp(value, (byte)1, (byte)9);
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>フェードイン時間（ms）</summary>
        [ObservableProperty]
        private int rainbowFadeInMs = 1000;

        /// <summary>フェードアウト時間（ms）</summary>
        [ObservableProperty]
        private int rainbowFadeOutMs = 1000;

        /// <summary>デフォルトレインボーカラー（赤→橙→黄→緑→青→藍→紫）</summary>
        private static List<RgbColorItem> DefaultRainbowColors() => new()
        {
            new(255, 0, 0),     // 赤
            new(255, 165, 0),   // 橙
            new(255, 255, 0),   // 黄
            new(0, 255, 0),     // 緑
            new(0, 0, 255),     // 青
            new(0, 0, 139),     // 藍
            new(128, 0, 128),   // 紫
        };

        /// <summary>
        /// UserState からコマンドパネルの Rainbow 設定（色/モード/速度等）を復元する（起動時に呼ぶ）。
        /// 色は 2 色以上保存されている場合のみ反映し、未保存時は既定7色を維持する。
        /// </summary>
        public void LoadRainbowSettings(UserState? state)
        {
            if (state == null) return;
            if (state.RainbowColors != null && state.RainbowColors.Count >= 2)
            {
                RainbowColors = new ObservableCollection<RgbColorItem>(
                    state.RainbowColors.Select(c => new RgbColorItem(c.R, c.G, c.B)));
            }
            RainbowMode = (RainbowMode)Math.Clamp(state.RainbowMode, 0, 5);
            if (state.RainbowCycleDurationMs > 0) RainbowCycleDurationMs = state.RainbowCycleDurationMs;
            if (state.RainbowBlinkPeriodMs > 0) RainbowBlinkPeriodMs = state.RainbowBlinkPeriodMs;
            if (state.RainbowDutyRatio >= 1 && state.RainbowDutyRatio <= 9) RainbowDutyRatio = state.RainbowDutyRatio;
            if (state.RainbowFadeInMs > 0) RainbowFadeInMs = state.RainbowFadeInMs;
            if (state.RainbowFadeOutMs > 0) RainbowFadeOutMs = state.RainbowFadeOutMs;
        }

        /// <summary>
        /// 現在のコマンドパネルの Rainbow 設定を UserState へ書き出す（終了時に呼ぶ）。
        /// </summary>
        public void SaveRainbowSettings(UserState? state)
        {
            if (state == null) return;
            state.RainbowMode = (int)RainbowMode;
            state.RainbowColors = RainbowColors.Select(c => new Rgb(c.R, c.G, c.B)).ToList();
            state.RainbowCycleDurationMs = RainbowCycleDurationMs;
            state.RainbowBlinkPeriodMs = RainbowBlinkPeriodMs;
            state.RainbowDutyRatio = RainbowDutyRatio;
            state.RainbowFadeInMs = RainbowFadeInMs;
            state.RainbowFadeOutMs = RainbowFadeOutMs;
        }

        /// <summary>
        /// 現在のコマンドパネルの Rainbow 設定（モード/色/速度/点滅/フェード）を、
        /// 選択中のシーケンス行に取り込む。行ごとに保存・再現され、実行時はその行の設定で Rainbow が動く。
        /// 複数行選択時は全行へ適用。適用先が現在の選択行なら即時に再実行して見た目を確認できる。
        /// </summary>
        [RelayCommand]
        private async Task ApplyRainbowToStepAsync()
        {
            // 対象: 複数選択があればその全行、無ければ SelectedStep 単一
            var targets = (_currentSelectedSteps != null && _currentSelectedSteps.Count > 0)
                ? _currentSelectedSteps.ToList()
                : (SelectedStep != null
                    ? new List<SequenceStepWrapper> { SelectedStep }
                    : new List<SequenceStepWrapper>());
            if (targets.Count == 0)
            {
                StatusMessage = "Rainbow を適用する行を選択してください。";
                return;
            }

            foreach (var w in targets)
            {
                w.CommandType = "Rainbow";
                w.RainbowMode = RainbowMode;
                w.RainbowColors = new ObservableCollection<RgbColorItem>(
                    RainbowColors.Select(c => new RgbColorItem(c.R, c.G, c.B)));
                w.RainbowCycleDurationMs = RainbowCycleDurationMs;
                w.RainbowBlinkPeriodMs = RainbowBlinkPeriodMs;
                w.RainbowDutyRatio = RainbowDutyRatio;
                w.RainbowFadeInMs = RainbowFadeInMs;
                w.RainbowFadeOutMs = RainbowFadeOutMs;
            }

            StatusMessage = $"Rainbow 設定を {targets.Count} 行に適用しました（未保存。保存で永続化）。";
            AppendLog("INFO", $"Rainbow 設定を {targets.Count} 行に適用（mode={RainbowMode}, {RainbowColors.Count}色, cycle={RainbowCycleDurationMs}ms）");

            // 即時反映: 適用先が現在の選択行なら、新パラメータでその行を再実行する
            if (SelectedStep != null && targets.Contains(SelectedStep)
                && _lighting != null && _lighting.IsConnected && !IsEmergencyActive)
            {
                await ExecuteStepWithoutAdvanceAsync();
            }
        }

        /// <summary>レインボーエフェクトを開始する</summary>
        [RelayCommand]
        private async Task StartRainbow()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            if (IsEmergencyActive)
            {
                StatusMessage = "Emergency モード中: 照明操作は抑止されています。";
                return;
            }
            try
            {
                var colors = RainbowColors.Select(c => new Rgb(c.R, c.G, c.B)).ToList();
                if (colors.Count < 2)
                {
                    StatusMessage = "レインボーカラーは2色以上必要です。";
                    return;
                }
                await _lighting.StartRainbowAsync(
                    RainbowMode, colors, RainbowCycleDurationMs,
                    RainbowMode == RainbowMode.Blink ? RainbowBlinkPeriodMs : null,
                    RainbowMode == RainbowMode.Blink ? RainbowDutyRatio : null,
                    RainbowMode is RainbowMode.FadeInOut or RainbowMode.FadeIn ? RainbowFadeInMs : null,
                    RainbowMode is RainbowMode.FadeInOut or RainbowMode.FadeOut ? RainbowFadeOutMs : null);
                AppendLog("TX", $"Rainbow {RainbowMode} ({colors.Count}色, cycle={RainbowCycleDurationMs}ms)");
                StatusMessage = $"Rainbow {RainbowMode} 開始（{colors.Count}色）";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Rainbow 開始失敗: {ex.Message}";
                AppendLog("ERR", $"Rainbow start failed: {ex.Message}");
            }
        }

        /// <summary>レインボーエフェクトを停止する</summary>
        [RelayCommand]
        private async Task StopRainbow()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            try
            {
                await _lighting.StopRainbowAsync();
                AppendLog("TX", "Rainbow Stop");
                StatusMessage = "Rainbow 停止";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Rainbow 停止失敗: {ex.Message}";
                AppendLog("ERR", $"Rainbow stop failed: {ex.Message}");
            }
        }

        /// <summary>7色ランダム一時停止（前回の色を保持）</summary>
        [RelayCommand]
        private async Task PauseRainbow()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            try
            {
                await _lighting.PauseRainbowAsync();
                AppendLog("TX", "Rainbow Pause");
                StatusMessage = "Rainbow 一時停止（色保持）";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Rainbow 一時停止失敗: {ex.Message}";
                AppendLog("ERR", $"Rainbow pause failed: {ex.Message}");
            }
        }

        /// <summary>レインボーカラーを追加する（最大7色）</summary>
        [RelayCommand]
        private void AddRainbowColor()
        {
            if (RainbowColors.Count >= 7)
            {
                StatusMessage = "レインボーカラーは最大7色です。";
                return;
            }
            RainbowColors.Add(new RgbColorItem(255, 255, 255));
        }

        /// <summary>レインボーカラーを削除する（最低2色）</summary>
        [RelayCommand]
        private void RemoveRainbowColor(RgbColorItem? item)
        {
            if (item == null) return;
            if (RainbowColors.Count <= 2)
            {
                StatusMessage = "レインボーカラーは最低2色必要です。";
                return;
            }
            RainbowColors.Remove(item);
        }

        /// <summary>
        /// レインボーカラーを変更する（右クリック → コンテキストメニュー「色を編集...」から呼ばれる）。
        /// 概要：DlgColorPicker を起動し、確定色を当該 RgbColorItem に反映する（スウォッチが即更新）。
        ///       レインボーパレットは色テーブル送信(SendColorTable)/Rainbow コマンドで使用される。
        ///       カスタム色と異なり実機へのライブ送信・JSON 永続化は行わない。
        /// </summary>
        [RelayCommand]
        private void EditRainbowColor(RgbColorItem? item)
        {
            if (item == null) return;

            var dlg = new DlgColorPicker { Owner = System.Windows.Application.Current?.MainWindow };
            dlg.SetInitialColor(item.R, item.G, item.B);

            // プレビュー：スウォッチをリアルタイム更新（HexColor 通知で反映）
            dlg.OnColorChanged = (r, g, b) =>
            {
                item.R = r;
                item.G = g;
                item.B = b;
            };

            if (dlg.ShowDialog() == true)
            {
                item.R = dlg.SelectedR;
                item.G = dlg.SelectedG;
                item.B = dlg.SelectedB;
                StatusMessage = $"レインボーカラーを更新（{item.R}, {item.G}, {item.B}）";
            }
        }

        /// <summary>3.15 色テーブルのみ送信する（0xA9 0x02）</summary>
        [RelayCommand]
        private async Task SendColorTable()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            if (IsEmergencyActive)
            {
                StatusMessage = "Emergency モード中: 照明操作は抑止されています。";
                return;
            }
            try
            {
                var colors = RainbowColors.Select(c => new Rgb(c.R, c.G, c.B)).ToList();
                if (colors.Count < 2)
                {
                    StatusMessage = "レインボーカラーは2色以上必要です。";
                    return;
                }
                await _lighting.SendColorTableAsync(colors, RainbowCycleDurationMs);
                AppendLog("TX", $"3.15 色テーブル ({colors.Count}色, cycle={RainbowCycleDurationMs}ms)");
                StatusMessage = $"色テーブル送信（{colors.Count}色）";
            }
            catch (Exception ex)
            {
                StatusMessage = $"色テーブル送信失敗: {ex.Message}";
                AppendLog("ERR", $"Color table send failed: {ex.Message}");
            }
        }

        /// <summary>3.16-3.21 モード別レインボー直接開始（CommandParameter でモード番号を渡す）</summary>
        [RelayCommand]
        private async Task StartRainbowDirect(string? modeStr)
        {
            if (!int.TryParse(modeStr, out var modeInt) || modeInt < 0 || modeInt > 5) return;
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            if (IsEmergencyActive)
            {
                StatusMessage = "Emergency モード中: 照明操作は抑止されています。";
                return;
            }
            var mode = (RainbowMode)modeInt;
            try
            {
                var colors = RainbowColors.Select(c => new Rgb(c.R, c.G, c.B)).ToList();
                if (colors.Count < 2)
                {
                    StatusMessage = "レインボーカラーは2色以上必要です。";
                    return;
                }
                var sectionNo = 16 + modeInt; // 3.16〜3.21
                await _lighting.StartRainbowAsync(
                    mode, colors, RainbowCycleDurationMs,
                    mode == RainbowMode.Blink ? RainbowBlinkPeriodMs : null,
                    mode == RainbowMode.Blink ? RainbowDutyRatio : null,
                    mode is RainbowMode.FadeInOut or RainbowMode.FadeIn ? RainbowFadeInMs : null,
                    mode is RainbowMode.FadeInOut or RainbowMode.FadeOut ? RainbowFadeOutMs : null);
                AppendLog("TX", $"3.{sectionNo} Rainbow {mode} ({colors.Count}色, cycle={RainbowCycleDurationMs}ms)");
                StatusMessage = $"3.{sectionNo} Rainbow {mode} 開始（{colors.Count}色）";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Rainbow {mode} 開始失敗: {ex.Message}";
                AppendLog("ERR", $"Rainbow {mode} start failed: {ex.Message}");
            }
        }

        #endregion

        #region コマンド：ファイル転送

        /// <summary>2.4GHz転送先フレーム番号</summary>
        [ObservableProperty]
        private uint fileWriteFrameNo;

        /// <summary>ファイル転送ステータス表示</summary>
        [ObservableProperty]
        private string fileWriteStatus = "";

        /// <summary>BLEデバイス一覧</summary>
        [ObservableProperty]
        private ObservableCollection<BleDeviceItem> bleDevices = new();

        /// <summary>選択中のBLEデバイス</summary>
        [ObservableProperty]
        private BleDeviceItem? selectedBleDevice;

        /// <summary>2.4GHz経由でRGBファイルを端末に書き込む</summary>
        [RelayCommand]
        private async Task WriteFileVia24G()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "RGBファイルを選択（2.4GHz転送）",
                Filter = "RGBファイル (*.bin;*.rgb;*.dat)|*.bin;*.rgb;*.dat|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var data = System.IO.File.ReadAllBytes(dlg.FileName);
                if (data.Length == 0 || data.Length % 3 != 0)
                {
                    StatusMessage = "ファイルサイズがRGB（3の倍数）ではありません。";
                    return;
                }

                FileWriteStatus = $"転送中... {data.Length} bytes";
                StatusMessage = $"2.4GHz ファイル書き込み中: frame={FileWriteFrameNo}, {data.Length} bytes";
                AppendLog("TX", $"FileWrite24G start: frame={FileWriteFrameNo}, file={dlg.FileName}, size={data.Length}");

                await _lighting.WriteFileVia24GAsync(data, FileWriteFrameNo);

                FileWriteStatus = $"完了: {data.Length} bytes";
                StatusMessage = $"2.4GHz ファイル書き込み完了: frame={FileWriteFrameNo}";
                AppendLog("TX", $"FileWrite24G done: frame={FileWriteFrameNo}");
            }
            catch (Exception ex)
            {
                FileWriteStatus = $"エラー: {ex.Message}";
                StatusMessage = $"ファイル書き込み失敗: {ex.Message}";
                AppendLog("ERR", $"FileWrite24G failed: {ex.Message}");
            }
        }

        /// <summary>BLEデバイスをスキャンする</summary>
        [RelayCommand]
        private async Task ScanBle()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            try
            {
                StatusMessage = "BLE スキャン中...";
                var devices = await _lighting.ScanBleDevicesAsync(5);
                BleDevices.Clear();
                foreach (var (addr, name, rssi) in devices)
                {
                    BleDevices.Add(new BleDeviceItem(addr, name, rssi));
                }
                StatusMessage = $"BLE スキャン完了: {devices.Count} デバイス";
            }
            catch (Exception ex)
            {
                StatusMessage = $"BLE スキャン失敗: {ex.Message}";
            }
        }

        /// <summary>選択したBLEデバイスに接続する</summary>
        [RelayCommand]
        private async Task ConnectBle()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            if (SelectedBleDevice == null)
            {
                StatusMessage = "BLEデバイスを選択してください。";
                return;
            }
            try
            {
                StatusMessage = $"BLE 接続中: {SelectedBleDevice.Address}...";
                await _lighting.ConnectBleAsync(SelectedBleDevice.Address);
                StatusMessage = $"BLE 接続完了: {SelectedBleDevice.Name}";
                AppendLog("BLE", $"Connected: {SelectedBleDevice.Address} ({SelectedBleDevice.Name})");
            }
            catch (Exception ex)
            {
                StatusMessage = $"BLE 接続失敗: {ex.Message}";
            }
        }

        /// <summary>BLEデバイスを切断する</summary>
        [RelayCommand]
        private async Task DisconnectBle()
        {
            if (_lighting == null) return;
            try
            {
                await _lighting.DisconnectBleAsync();
                StatusMessage = "BLE 切断完了";
                AppendLog("BLE", "Disconnected");
            }
            catch (Exception ex)
            {
                StatusMessage = $"BLE 切断失敗: {ex.Message}";
            }
        }

        /// <summary>BLE経由でRGBファイルを転送する</summary>
        [RelayCommand]
        private async Task TransferFileBle()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "RGBファイルを選択（BLE転送）",
                Filter = "RGBファイル (*.bin;*.rgb;*.dat)|*.bin;*.rgb;*.dat|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var data = System.IO.File.ReadAllBytes(dlg.FileName);
                if (data.Length == 0)
                {
                    StatusMessage = "ファイルが空です。";
                    return;
                }

                StatusMessage = $"BLE ファイル転送中: {data.Length} bytes";
                AppendLog("BLE", $"FileTransfer start: file={dlg.FileName}, size={data.Length}");

                await _lighting.TransferFileBleAsync(data);

                StatusMessage = $"BLE ファイル転送完了: {data.Length} bytes";
                AppendLog("BLE", $"FileTransfer done: {data.Length} bytes");
            }
            catch (Exception ex)
            {
                StatusMessage = $"BLE ファイル転送失敗: {ex.Message}";
                AppendLog("ERR", $"BLE FileTransfer failed: {ex.Message}");
            }
        }

        #endregion

        #region コマンド：プリセット選択

        /// <summary>
        /// 色プリセットボタン押下:選択中の行に色を反映する（CommandType は変更しない）
        /// </summary>
        [RelayCommand]
        private void SelectColorPreset(ColorPresetItem? item)
        {
            if (item == null) return;
            SelectedColorPreset = item;
            foreach (var p in ColorPresets) p.IsSelected = p == item;
            foreach (var c in CustomColorPresets) c.IsSelected = false;

            if (SelectedStep == null)
            {
                StatusMessage = _editingSequence == null
                    ? "シーケンスを選択するか、「新規」で作成してください。"
                    : EditingSteps.Count == 0
                        ? "「+ 追加」ボタンで行を追加してから色プリセットを押してください。"
                        : "色を反映する行を先に選択してください。";
                return;
            }

            SelectedStep.ColorR = item.R;
            SelectedStep.ColorG = item.G;
            SelectedStep.ColorB = item.B;
            // NO.37: 色変更後にその行を実行し、変更色を実機へ送信する
            ReExecuteCurrentStep();
            StatusMessage = $"色を反映：{item.Name}（{item.R}, {item.G}, {item.B}）";
        }

        /// <summary>
        /// カスタム色プリセット押下：選択中の行に色を反映（色プリセットと同じ挙動）
        /// </summary>
        [RelayCommand]
        private void SelectCustomColorPreset(CustomColorPresetItem? item)
        {
            if (item == null) return;
            foreach (var c in CustomColorPresets) c.IsSelected = c == item;
            foreach (var p in ColorPresets) p.IsSelected = false;

            if (SelectedStep == null)
            {
                StatusMessage = _editingSequence == null
                    ? "シーケンスを選択するか、「新規」で作成してください。"
                    : EditingSteps.Count == 0
                        ? "「+ 追加」ボタンで行を追加してからカスタム色プリセットを押してください。"
                        : "色を反映する行を先に選択してください。";
                return;
            }

            SelectedStep.ColorR = item.R;
            SelectedStep.ColorG = item.G;
            SelectedStep.ColorB = item.B;
            // NO.37: 色変更後にその行を実行し、変更色を実機へ送信する
            ReExecuteCurrentStep();
            StatusMessage = $"カスタム色を反映：{item.Name}（{item.R}, {item.G}, {item.B}）";
        }

        /// <summary>
        /// カスタム色を編集する（右クリック → コンテキストメニュー「色を編集...」から呼ばれる）
        /// 概要：DlgColorPicker を起動し、編集結果を当該カスタム色に反映して JSON へ保存する。
        /// </summary>
        [RelayCommand]
        private void EditCustomColor(CustomColorPresetItem? item)
        {
            if (item == null) return;

            var dlg = new DlgColorPicker { Owner = System.Windows.Application.Current?.MainWindow };
            dlg.SetInitialColor(item.R, item.G, item.B);

            // 色変更をリアルタイムで選択行 + 実機に反映
            var origR = item.R;
            var origG = item.G;
            var origB = item.B;
            dlg.OnColorChanged = (r, g, b) =>
            {
                if (SelectedStep != null)
                {
                    SelectedStep.ColorR = r;
                    SelectedStep.ColorG = g;
                    SelectedStep.ColorB = b;
                }
                if (_lighting != null && _lighting.IsConnected)
                {
                    _ = _lighting.SetColorAsync(
                        Lib.Domain.Enums.Target.All,
                        new Lib.Domain.ValueObjects.Rgb(r, g, b));
                }
            };

            if (dlg.ShowDialog() == true)
            {
                // 確定色を選択中ステップにも明示的に反映
                if (SelectedStep != null)
                {
                    SelectedStep.ColorR = dlg.SelectedR;
                    SelectedStep.ColorG = dlg.SelectedG;
                    SelectedStep.ColorB = dlg.SelectedB;
                }
                item.R = dlg.SelectedR;
                item.G = dlg.SelectedG;
                item.B = dlg.SelectedB;
                SaveCustomColors();
                StatusMessage = $"カスタム色「{item.Name}」を更新（{item.R}, {item.G}, {item.B}）";
                AppendLog("INFO", $"カスタム色 {item.Name} を更新 ({item.R},{item.G},{item.B})");
            }
            else
            {
                // Cancel 時は元の色に復元
                if (SelectedStep != null)
                {
                    SelectedStep.ColorR = origR;
                    SelectedStep.ColorG = origG;
                    SelectedStep.ColorB = origB;
                }
            }
        }

        /// <summary>
        /// 動作プリセットボタン押下：選択中の行のコマンド種別を確定し、続けて空行を自動挿入する
        /// </summary>
        [RelayCommand]
        private void SelectActionPreset(ActionPresetItem? item)
        {
            if (item == null) return;
            SelectedActionPreset = item;
            foreach (var p in ActionPresets) p.IsSelected = p == item;

            if (SelectedStep == null)
            {
                StatusMessage = _editingSequence == null
                    ? "シーケンスを選択するか、「新規」で作成してください。"
                    : EditingSteps.Count == 0
                        ? "「+ 追加」ボタンで行を追加してから動作プリセットを押してください。"
                        : "動作を反映する行を先に選択してください。";
                return;
            }

            var (cmdType, effType, continuous) = MapActionToCommand(item.Command);
            SelectedStep.CommandType = cmdType;
            SelectedStep.EffectType = effType ?? "";
            // 連続実行フラグを反映（null=未指定、true=繰り返し、false=1 回実行後 最終色保持）
            SelectedStep.Continuous = continuous;

            // 消灯時は RGB を 0/0/0 にして、DataGrid 表示と実動作を一致させる
            // （+追加 時の色踏襲でも 0/0/0 が引き継がれるので、後続行も「消灯起点」になる）
            if (cmdType == "Off")
            {
                SelectedStep.ColorR = 0;
                SelectedStep.ColorG = 0;
                SelectedStep.ColorB = 0;
            }

            // Effect の場合は周期・Fade のデフォルト値を補完
            if (cmdType == "Effect")
            {
                if (SelectedStep.EffectCycleDurationMs <= 0)
                {
                    SelectedStep.EffectCycleDurationMs = 1000;
                }
                // FadeIn / FadeOut 系はデフォルト Fade=0（API 側が適切に補間する）
                // Flash / Breathing / SevenColor はデフォルト Fade=20
                if (SelectedStep.FadeSteps <= 0
                    && effType != "FadeIn" && effType != "FadeOut")
                {
                    SelectedStep.FadeSteps = 20;
                }
            }

            // 動作確定の手応えとして、当該行を即時実行（接続中のみ）
            if (_lighting != null && _lighting.IsConnected)
            {
                _ = ExecuteStepWithoutAdvanceAsync();
            }

            StatusMessage = $"動作を確定：{item.DisplayName}（選択行を上書き）";
        }

        /// <summary>
        /// プリセットボタンの動作名を内部モデル（CommandType / EffectType / Continuous）に変換する
        /// </summary>
        /// <remarks>
        /// Continuous の意味：
        ///   null  → 未指定（繰り返し相当）
        ///   true  → 繰り返し
        ///   false → 1 回実行後 最終色保持
        /// 「Fade In/In」「Fade Out/Out」は FadeIn / FadeOut に Continuous=false を組み合わせて実現する。
        /// </remarks>
        private static (string CommandType, string? EffectType, bool? Continuous) MapActionToCommand(string? presetCommand)
        {
            return presetCommand switch
            {
                "SetColor"    => ("Color",  null,         null),
                "Off"         => ("Off",    null,         null),
                "Flash"       => ("Effect", "Flash",      null),
                "FadeIn"      => ("Effect", "FadeIn",     null),
                "FadeOut"     => ("Effect", "FadeOut",    null),
                "FadeInHold"  => ("Effect", "FadeIn",     false), // 1 回フェードイン後 目標色保持
                "FadeOutHold" => ("Effect", "FadeOut",    false), // 1 回フェードアウト後 消灯保持
                "Breath"      => ("Effect", "Breathing",  null),
                "SevenColor"  => ("Effect", "SevenColor", null),
                // NO.38: 信号Off（端末セルフモード開放）
                "SignalOff"   => ("SignalOff", null,      null),
                // NO.35: Color2 廃止（マッピング削除）
                _             => ("Color",  null,         null),
            };
        }

        /// <summary>
        /// 選択中の行の直後に空行を 1 行挿入する
        /// 概要：時刻は直前行 + 1000ms、コマンドは Color、色は常に白(255,255,255)、エフェクト関連は未指定。
        ///       挿入後はその空行が選択状態となり、ユーザーが色や動作を選んで埋めていくワークフロー。
        /// </summary>
        [RelayCommand]
        private void AddEmptyStep()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }
            SaveUndoState();

            int insertIndex;
            if (SelectedStep != null && EditingSteps.Contains(SelectedStep))
            {
                insertIndex = EditingSteps.IndexOf(SelectedStep) + 1;
            }
            else if (EditingSteps.Count > 0)
            {
                insertIndex = EditingSteps.Count;
            }
            else
            {
                insertIndex = 0;
            }

            // 追加される行は常に「白 / Color / エフェクトなし」の初期値で統一する。
            // TimeMs=0 → 空表示（無限待機）がデフォルト。
            var step = new SequenceStep
            {
                TimeMs = 0,
                CommandType = "Color",
                EffectType = null,
                ColorR = 255,
                ColorG = 255,
                ColorB = 255,
                EffectCycleDurationMs = null,
                FadeSteps = null,
                RetransmitCount = 3,
                Note = ""
            };
            var wrapper = new SequenceStepWrapper(step);

            // Insert によって DataGrid の CollectionChanged が発火し、
            // TwoWay バインディング経由で SelectedStep/CurrentStepIndex が
            // 一時的に変わる可能性がある。他の全操作と同様、
            // コレクション変更の前に自動実行を抑制する。
            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Insert(insertIndex, wrapper);
                RenumberEditingSteps();
                SelectedStep = wrapper;
                CurrentStepIndex = insertIndex;
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"空行を挿入（位置 {insertIndex + 1}）。色や動作のボタンをクリックして内容を確定してください。";
        }

        #endregion

        #region コマンド：ステップ実行

        /// <summary>
        /// 現在ステップを実行 + 次ステップへ移動（Enter / Space キー・「実行 ▶」ボタン）
        /// 概要：選択行を1つ進める。単発の実行は <see cref="OnSelectedStepChanged"/> に任せ、
        ///       マーカー行(Chase/OL)に「外から」着地した場合は矢印キーと同じく自動起動する。
        ///       挙動は <see cref="NextStep"/> と完全一致させ、クリック／矢印／Enter を同じ規則で統一する。
        /// </summary>
        [RelayCommand]
        private void ExecuteCurrentStep()
        {
            if (EditingSteps.Count == 0)
            {
                StatusMessage = "実行するステップがありません。";
                return;
            }

            // 矢印キーと同じく、Chase/OL 実行中ならまずループを停止してから移動する（Enter も離脱手段）
            bool wasLooping = IsLoopRunning;
            if (wasLooping) StopLoopExecution();

            // 未選択時は先頭行を選択し、マーカー行なら自動起動（クリック相当＝enter-from-outside 判定なし）
            if (CurrentStepIndex < 0)
            {
                CurrentStepIndex = 0;
                SelectedStep = EditingSteps[0];
                if (!wasLooping) TryAutoStartLoop(SelectedStep, prevForEnterCheck: null);
                return;
            }

            // 末尾の場合は移動しない
            if (CurrentStepIndex >= EditingSteps.Count - 1)
            {
                StatusMessage = "最後のステップです。";
                return;
            }

            // 次行へ移動。off 行への離脱なら全停止（矢印キーと同じ）
            var newIndex = CurrentStepIndex + 1;
            if (MoveSelectionForLoopExit(wasLooping, newIndex)) return;

            var prev = SelectedStep;                 // 移動前の行（enter-from-outside 判定用）
            CurrentStepIndex = newIndex;
            SelectedStep = EditingSteps[newIndex];
            // 矢印でマーカー行に「外から」入ったら自動起動（同ブロック内移動では起動しない）
            if (!wasLooping) TryAutoStartLoop(SelectedStep, prev);
            StatusMessage = $"実行: ステップ {newIndex + 1} / {EditingSteps.Count}";
        }

        /// <summary>
        /// 前ステップへ移動（← / ↑ キー）
        /// </summary>
        [RelayCommand]
        private void PreviousStep()
        {
            if (EditingSteps.Count == 0) return;

            // 矢印キーは Chase/OL の離脱手段でもある。実行中ならまずループを停止する。
            bool wasLooping = IsLoopRunning;
            if (wasLooping) StopLoopExecution();

            // 現在行が Chase/OL ブロック内なら、マーカー位置に関係なくブロックごと
            // 飛び越えて直前の一般行へ離脱する（中間行からでも確実に抜けられる）。
            if (TryJumpOutOfLoopBlock(wasLooping, forward: false)) return;

            if (CurrentStepIndex <= 0)
            {
                StatusMessage = "最初のステップです。";
                return;
            }

            var newIndex = CurrentStepIndex - 1;
            if (MoveSelectionForLoopExit(wasLooping, newIndex)) return;

            var prev = SelectedStep;                 // 移動前の行（enter-from-outside 判定用）
            CurrentStepIndex = newIndex;
            SelectedStep = EditingSteps[newIndex];
            // 矢印でマーカー行に「外から」入ったら自動起動（離脱操作中=wasLooping は起動しない）
            if (!wasLooping) TryAutoStartLoop(SelectedStep, prev);
            StatusMessage = $"前ステップ：{newIndex + 1} / {EditingSteps.Count}";
        }

        /// <summary>次ステップへ移動（→ / ↓ キー）</summary>
        [RelayCommand]
        private void NextStep()
        {
            if (EditingSteps.Count == 0) return;

            // 矢印キーは Chase/OL の離脱手段でもある。実行中ならまずループを停止する。
            bool wasLooping = IsLoopRunning;
            if (wasLooping) StopLoopExecution();

            // 現在行が Chase/OL ブロック内なら、マーカー位置に関係なくブロックごと
            // 飛び越えて直後の一般行へ離脱する（中間行からでも確実に抜けられる）。
            if (TryJumpOutOfLoopBlock(wasLooping, forward: true)) return;

            if (CurrentStepIndex >= EditingSteps.Count - 1)
            {
                StatusMessage = "最後のステップです。";
                return;
            }

            var newIndex = CurrentStepIndex + 1;
            if (MoveSelectionForLoopExit(wasLooping, newIndex)) return;

            var prev = SelectedStep;                 // 移動前の行（enter-from-outside 判定用）
            CurrentStepIndex = newIndex;
            SelectedStep = EditingSteps[newIndex];
            // 矢印でマーカー行に「外から」入ったら自動起動（離脱操作中=wasLooping は起動しない）
            if (!wasLooping) TryAutoStartLoop(SelectedStep, prev);
            StatusMessage = $"次ステップへ移動：{newIndex + 1} / {EditingSteps.Count}";
        }

        /// <summary>
        /// Chase/OL からの矢印キー離脱時、移動先が off（消灯 / 信号Off）なら停止ボタン相当にする。
        /// off停止を実行した場合は true を返す（呼び出し側は以降の通常移動をスキップする）。
        /// </summary>
        private bool MoveSelectionForLoopExit(bool wasLooping, int newIndex)
        {
            if (!wasLooping) return false;
            var target = EditingSteps[newIndex];
            if (!IsOffStep(target)) return false;

            // 選択は移すが自動実行は抑制し、停止ボタン相当の全停止を行う。
            _suppressAutoExecute = true;
            try
            {
                CurrentStepIndex = newIndex;
                SelectedStep = target;
            }
            finally { _suppressAutoExecute = false; }

            _ = StopExecutionAsync();
            return true;
        }

        /// <summary>
        /// 現在行が Chase/OL マーカーの連続ブロック(2行以上)内にあるとき、矢印キーで
        /// ブロックごと飛び越えて外側の行へ着地させる。ブロック内のどの行にいても、
        /// forward=false(↑/←)ならブロック先頭の直前行、forward=true(↓/→)なら
        /// ブロック末尾の直後行へ一気に移動する。
        /// 概要：Chase 実行中は SelectedStep/CurrentStepIndex がブロック内を往復するため、
        ///       単純な ±1 移動ではマーカーが端に来た瞬間しか離脱できない。この処理で
        ///       中間行からでも確実に一般シーケンス行へ抜けられるようにする。
        /// 戻り値：移動（または端到達メッセージ）を行ったら true。ブロック外・単独行の
        ///        場合は false（呼び出し側の通常 ±1 移動に委ねる）。
        /// </summary>
        private bool TryJumpOutOfLoopBlock(bool wasLooping, bool forward)
        {
            int idx = CurrentStepIndex;
            if (idx < 0 || idx >= EditingSteps.Count) return false;

            var mode = EditingSteps[idx].Trig;              // "Chase" / "OL" / ""
            if (mode != "Chase" && mode != "OL") return false;

            // 同種マーカーの連続ブロックを展開（TryAutoStartLoop と同じ規則）
            int start = idx, end = idx;
            while (start - 1 >= 0 && EditingSteps[start - 1].Trig == mode) start--;
            while (end + 1 < EditingSteps.Count && EditingSteps[end + 1].Trig == mode) end++;
            if (end - start + 1 < 2) return false;          // ブロック未成立（単独行）は通常移動

            int exitIndex = forward ? end + 1 : start - 1;

            // ブロックが端に接していて外側に行が無い場合は端メッセージのみ（ブロック内へは戻さない）
            if (exitIndex < 0)
            {
                StatusMessage = "最初のステップです。";
                return true;
            }
            if (exitIndex >= EditingSteps.Count)
            {
                StatusMessage = "最後のステップです。";
                return true;
            }

            // off 行への離脱なら停止ボタン相当（既存の矢印離脱ルールを踏襲）
            if (MoveSelectionForLoopExit(wasLooping, exitIndex)) return true;

            var prev = SelectedStep;                        // 移動前の行（enter-from-outside 判定用）
            CurrentStepIndex = exitIndex;
            SelectedStep = EditingSteps[exitIndex];
            // 矢印でマーカー行に「外から」入ったら自動起動（離脱操作中=wasLooping は起動しない）
            if (!wasLooping) TryAutoStartLoop(SelectedStep, prev);
            StatusMessage = forward
                ? $"次ステップへ移動：{exitIndex + 1} / {EditingSteps.Count}"
                : $"前ステップ：{exitIndex + 1} / {EditingSteps.Count}";
            return true;
        }

        /// <summary>
        /// 押下中の煽りボタンインデックス（押下中の視覚フィードバック用、未押下時は -1）
        /// </summary>
        [ObservableProperty]
        private int activeAggressiveButtonIndex = -1;

        /// <summary>
        /// 押下が始まった時刻（ピカ一発の最小視認時間を担保するために使用）
        /// </summary>
        private DateTime _aggressivePressTime;

        /// <summary>
        /// 押下時にシーケンス再生中だったかどうか。Release 時にこの値を見て resume するか消灯するか判断。
        /// </summary>
        private bool _aggressivePausedSequence;

        /// <summary>ピカ一発の最小視認時間（ms）。短押しでも必ずこの時間以上は点灯させる。</summary>
        private const int AggressiveMinFlashMs = 150;

        /// <summary>
        /// 煽りボタン押し下げ時の処理
        /// 概要：再生中シーケンスがあれば一時停止して停止位置を保存し、
        ///       ボタンの色で全 LED を点灯する。
        /// </summary>
        /// <param name="buttonIndex">押された煽りボタンの index（0 = 煽り 1、1 = 煽り 2）</param>
        public async Task AggressivePressAsync(int buttonIndex)
        {
            if (IsEmergencyActive) return;
            if (_lighting == null || !_lighting.IsConnected) return;
            if (buttonIndex < 0 || buttonIndex >= AggressiveColorPresets.Count) return;

            ActiveAggressiveButtonIndex = buttonIndex;
            _aggressivePressTime = DateTime.UtcNow;

            var item = AggressiveColorPresets[buttonIndex];
            var color = new Rgb(item.R, item.G, item.B);

            try
            {
                // シーケンス再生中なら一時停止して状態を保存する
                _aggressivePausedSequence = await _lighting.PauseSequenceAsync();
                await _lighting.SetColorAsync(Target.All, color);
                AppendLog("TX", $"{item.Name} ON ({color.R},{color.G},{color.B})");
                AppendContinuousSendStartLog();
            }
            catch (Exception ex)
            {
                AppendLog("ERR", $"{item.Name} ON 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 煽りボタン離した時の処理
        /// 概要：押下時にシーケンスを一時停止していたら続きから再開する。
        ///       一時停止対象がなかった場合は消灯する。
        ///       短押し（ピカ一発）の場合は最小視認時間を担保してから処理する。
        /// </summary>
        public async Task AggressiveReleaseAsync(int buttonIndex)
        {
            if (_lighting == null) return;
            if (ActiveAggressiveButtonIndex < 0) return;

            var item = (buttonIndex >= 0 && buttonIndex < AggressiveColorPresets.Count)
                ? AggressiveColorPresets[buttonIndex]
                : null;

            // ピカ一発を視認可能にするため、最小点灯時間を担保する
            var elapsed = (int)(DateTime.UtcNow - _aggressivePressTime).TotalMilliseconds;
            if (elapsed < AggressiveMinFlashMs)
            {
                await Task.Delay(AggressiveMinFlashMs - elapsed);
            }

            try
            {
                if (_aggressivePausedSequence)
                {
                    // 再生中だったシーケンスを続きから再開
                    await _lighting.ResumeSequenceAsync();
                    AppendLog("TX", $"{item?.Name ?? "煽り"} OFF (Resume Sequence)");
                    AppendContinuousSendStartLog();
                }
                else
                {
                    // NO.24 修正: Effect/Rainbow ステップの場合はステップを再実行してエフェクトを復帰する
                    // （NO.35: Color2 は廃止のため対象から除外）
                    var currentStep = SelectedStep;
                    if (currentStep != null &&
                        currentStep.CommandType is "Effect" or "Rainbow")
                    {
                        await ExecuteStepWithoutAdvanceAsync();
                        AppendLog("TX", $"{item?.Name ?? "煽り"} OFF → ステップ再実行 ({currentStep.CommandType})");
                    }
                    else if (_lastExecutedColor.HasValue)
                    {
                        var prev = _lastExecutedColor.Value;
                        await _lighting.SetColorAsync(Target.All, new Rgb(prev.R, prev.G, prev.B));
                        AppendLog("TX", $"{item?.Name ?? "煽り"} OFF → 復帰 ({prev.R},{prev.G},{prev.B})");
                        AppendContinuousSendStartLog();
                    }
                    else
                    {
                        await _lighting.SetColorAsync(Target.All, new Rgb(0, 0, 0));
                        AppendLog("TX", $"{item?.Name ?? "煽り"} OFF (Off)");
                        AppendContinuousSendStartLog();
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERR", $"{item?.Name ?? "煽り"} OFF 失敗: {ex.Message}");
            }
            finally
            {
                _aggressivePausedSequence = false;
                ActiveAggressiveButtonIndex = -1;
            }
        }

        /// <summary>
        /// 煽りボタンの色を編集する（右クリック「色を編集...」メニューから呼ばれる）
        /// </summary>
        [RelayCommand]
        private void EditAggressiveColor(AggressiveColorPresetItem? item)
        {
            if (item == null) return;
            var dlg = new DlgColorPicker { Owner = System.Windows.Application.Current?.MainWindow };
            dlg.SetInitialColor(item.R, item.G, item.B);

            // 色変更をリアルタイムで実機に反映
            dlg.OnColorChanged = (r, g, b) =>
            {
                if (_lighting != null && _lighting.IsConnected)
                {
                    _ = _lighting.SetColorAsync(
                        Lib.Domain.Enums.Target.All,
                        new Lib.Domain.ValueObjects.Rgb(r, g, b));
                }
            };

            if (dlg.ShowDialog() == true)
            {
                item.R = dlg.SelectedR;
                item.G = dlg.SelectedG;
                item.B = dlg.SelectedB;
                SaveAggressiveColors();
                StatusMessage = $"煽り色「{item.Name}」を更新（{item.R}, {item.G}, {item.B}）";
                AppendLog("INFO", $"煽り色 {item.Name} を更新 ({item.R},{item.G},{item.B})");
            }
        }

        /// <summary>
        /// 実行中エフェクトを停止（停止ボタン または Esc キー）
        /// 一括再生中はシーケンス自体を停止し、停止時点の色を維持する。
        /// それ以外はエフェクトのみ停止する。
        /// </summary>
        [RelayCommand]
        private async Task StopExecutionAsync()
        {
            if (_lighting == null) return;

            // NO.21 修正: 全ループ・遷移・サブシーケンスを確実にキャンセルする
            StopLoopExecution();
            _transitionCts?.Cancel();
            _subSequenceCts?.Cancel();

            // セマフォが保持されたままの場合に強制解放（Chase 中の API 呼び出しがブロックしている場合の救済）
            if (_executionSemaphore.CurrentCount == 0)
            {
                try { _executionSemaphore.Release(); }
                catch (SemaphoreFullException) { /* already released */ }
            }

            // 一括再生中ならシーケンス停止＋停止時の色を維持する
            if (IsPlaying)
            {
                await StopSequenceWithColorHoldAsync();
                return;
            }

            // 単発エフェクト + Rainbow 停止
            try
            {
                await _lighting.StopEffectAsync();
                try { await _lighting.StopRainbowAsync(); }
                catch { /* Rainbow が走っていなければ無視 */ }
                StatusMessage = "停止しました。";
                AppendLog("TX", "EffectStop + RainbowStop");
                AppendContinuousSendStopLog();
            }
            catch (Exception ex)
            {
                StatusMessage = $"停止失敗: {ex.Message}";
                AppendLog("ERR", $"Stop 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// Emergency（白）トグル（後方互換）
        /// </summary>
        [RelayCommand]
        private async Task ToggleEmergencyWhiteAsync()
        {
            await ToggleEmergencyAsync("White", new Rgb(255, 255, 255));
        }

        /// <summary>
        /// Emergency（黒）トグル（後方互換）
        /// </summary>
        [RelayCommand]
        private async Task ToggleEmergencyBlackAsync()
        {
            await ToggleEmergencyAsync("Black", new Rgb(0, 0, 0));
        }

        /// <summary>
        /// BlackOut トグル（v3.9: Emergency白/黒を統合した単一ボタン）
        /// 概要：全灯を消灯(黒)で固定。再押下で解除し直前の状態に復帰。
        /// </summary>
        [RelayCommand]
        private async Task ToggleBlackOutAsync()
        {
            await ToggleEmergencyAsync("Black", new Rgb(0, 0, 0));

            // 解除時は直前の実行行を再実行して復帰
            if (!IsEmergencyActive)
            {
                try
                {
                    await ExecuteStepWithoutAdvanceAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BlackOut] 復帰失敗: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 信号Offトグル（セルフモード用）
        /// 概要：KeepAlive/エフェクト/シーケンスを全停止し、
        ///       SNO端末が自動的にセルフモード（ユーザー自由操作）へ復帰するようにする。
        ///       MC中・休憩中・開始前に使用する。
        ///       再押下で選択行を再実行して制御モードに復帰。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSignalOffActive))]
        private bool isSignalOff;

        public bool IsSignalOffActive => IsSignalOff;

        [RelayCommand]
        private async Task ToggleSignalOffAsync()
        {
            if (_lighting == null || !_lighting.IsConnected) return;

            if (IsSignalOff)
            {
                // 復帰：制御モードに戻す
                IsSignalOff = false;
                StatusMessage = "信号On — 制御モードに復帰";
                AppendLog("INFO", "信号On: 制御モードに復帰");
                try { await ExecuteStepWithoutAdvanceAsync(); }
                catch (Exception ex) { AppendLog("ERR", $"信号On 復帰失敗: {ex.Message}"); }
            }
            else
            {
                // 全停止：セルフモードに移行
                StopLoopExecution();
                if (IsPlaying)
                {
                    await _lighting.StopSequenceAsync();
                    IsPlaying = false;
                }
                await _lighting.StopSignalAsync();
                IsSignalOff = true;
                StatusMessage = "信号Off — 端末はセルフモードに復帰します";
                AppendLog("INFO", "信号Off: KeepAlive停止、端末セルフモードへ");
            }
        }

        /// <summary>
        /// Emergency 共通トグル処理
        /// </summary>
        private async Task ToggleEmergencyAsync(string mode, Rgb color)
        {
            if (IsEmergencyActive && EmergencyMode == mode)
            {
                // 解除
                IsEmergencyActive = false;
                EmergencyMode = null;
                StatusMessage = "Emergency モードを解除しました。";
                AppendLog("INFO", $"Emergency ({mode}) 解除");

                // 解除時は選択行を再実行して復帰
                try { await ExecuteStepWithoutAdvanceAsync(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Emergency] 復帰失敗: {ex.Message}"); }
                return;
            }

            // 有効化 — UIを即座に更新してから送信（ラグ防止）
            IsEmergencyActive = true;
            EmergencyMode = mode;
            StatusMessage = $"Emergency モード ({mode}) 有効 — 照明操作は抑止されています。";
            AppendLog("INFO", $"Emergency ({mode}) 有効");

            // NO.36: 即応性確保 — 色送信より「先に」ローカルの連続送信ループを止める。
            // Color遷移ループ/Chase/OL/サブシーケンスが単一 HttpClient を占有し続けると、
            // 緊急の黒送信が後ろに詰まる・直後に旧色で上書きされるため、押下→消灯が遅れていた。
            // ループを先に止めてから黒を送ることで競合を排除し、即時に消灯させる。
            StopLoopExecution();
            _transitionCts?.Cancel();
            _subSequenceCts?.Cancel();

            if (_lighting != null && _lighting.IsConnected)
            {
                try
                {
                    // 色送信を最優先（実機を即座に切替）。
                    // NO.37: SetColorAsync → API 側 StartColorHold は内部で実行中シーケンス/エフェクトを
                    //   停止した上で「黒の連続送信ホールド（20ms間隔）」を開始する。
                    //   従来はこの直後に StopSequenceAsync / StopEffectAsync を呼んでいたが、
                    //   StopEffectAsync は scheduler.Abort 経由で“その黒の連続ホールド自体”を止めてしまい、
                    //   黒が数パケットしか送られなかった。2.4GHz でそのわずかな黒パケットが落ちると
                    //   KeepAlive 再送（最短800ms後）まで消灯が反映されず、押下→消灯が遅れていた。
                    //   よって黒送信後は API への追加停止呼び出しを行わず、連続ホールドを維持して
                    //   確実かつ即時に消灯させる（停止は StartColorHold 側で実施済み）。
                    await _lighting.SetColorAsync(Target.All, color);
                    AppendLog("TX", $"Emergency ({mode}): ({color.R},{color.G},{color.B}) — 連続ホールド維持");

                    // UI 状態のみ更新（API 側の再生停止はカラーホールド開始時に完了済み）
                    IsPlaying = false;
                }
                catch (Exception ex)
                {
                    AppendLog("ERR", $"Emergency 送信失敗: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// F6: 接続設定ダイアログを表示する
        /// 概要：メイン画面を経由せずにシーケンス編集ウィンドウから直接
        ///       接続設定・イニシャライズを行えるようにする。
        /// </summary>
        [RelayCommand]
        private void ShowSettingsDialog()
        {
            if (_lighting == null) return;

            var settingVm = new SettingViewModel(_lighting);

            var dlg = new DlgSettings
            {
                Owner = System.Windows.Application.Current?.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow
            };
            dlg.SetSettingViewModel(settingVm);
            dlg.ShowDialog();

            // 接続状態の変更をログに反映
            if (_lighting.IsConnected)
            {
                StatusMessage = $"接続中: {string.Join(", ", _lighting.ConnectedPorts)}";
                AppendLog("INFO", $"設定ダイアログから接続: {string.Join(", ", _lighting.ConnectedPorts)}");
            }
        }

        /// <summary>
        /// 一括再生中のシーケンスを停止し、停止時点のステップの色を保持する。
        /// 後続ステップは発火させない。
        /// </summary>
        private async Task StopSequenceWithColorHoldAsync()
        {
            if (_lighting == null) return;

            // 停止前に「いまどのステップを実行中か」を取得して色を退避する
            SequenceStepWrapper? lastWrapper = null;
            try
            {
                var status = await _lighting.GetSequencePlayStatusDetailedAsync();
                if (status.IsPlaying && status.CurrentStepIndex >= 0 && status.CurrentStepIndex < EditingSteps.Count)
                {
                    lastWrapper = EditingSteps[status.CurrentStepIndex];
                }
            }
            catch { /* 取得失敗時は色保持なしで停止する */ }

            try
            {
                await _lighting.StopSequenceAsync();

                // 停止時点の色を再送して LED 状態を維持する
                if (lastWrapper != null)
                {
                    var color = new Rgb(lastWrapper.ColorR, lastWrapper.ColorG, lastWrapper.ColorB);
                    if (lastWrapper.CommandType == "Off" || lastWrapper.CommandType == "EffectStop")
                    {
                        await _lighting.SetColorAsync(Target.All, new Rgb(0, 0, 0));
                        AppendLog("TX", "Sequence Stop → Off 維持");
                        AppendContinuousSendStartLog();
                    }
                    else
                    {
                        await _lighting.SetColorAsync(Target.All, color);
                        AppendLog("TX", $"Sequence Stop → Color ({color.R},{color.G},{color.B}) 維持");
                        AppendContinuousSendStartLog();
                    }
                }
                else
                {
                    AppendLog("TX", "Sequence Stop");
                    AppendContinuousSendStopLog();
                }

                StatusMessage = "シーケンス再生を停止しました（停止時の色を維持）。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"シーケンス停止失敗: {ex.Message}";
                AppendLog("ERR", $"Sequence Stop 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 現在ステップを実行する（共通ロジック、advance なし）
        /// 概要：continuous=true で実行 → 次の Enter で上書き、Stop 不要
        /// </summary>
        /// <remarks>
        /// コードビハインドからの呼び出しには <see cref="ReExecuteCurrentStepAsync"/> を使用すること。
        /// </remarks>
        private async Task ExecuteStepWithoutAdvanceAsync()
        {
            if (IsEmergencyActive)
            {
                StatusMessage = "Emergency モード中: 照明操作は抑止されています。";
                return;
            }
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            // SelectedStep を直接参照することで、CurrentStepIndex のバインド同期タイミングに左右されない
            var wrapper = SelectedStep;
            if (wrapper == null) return;

            // ロック中の行は実行をスキップ（本番中に先の演出を安全に修正可能）
            if (wrapper.IsLocked)
            {
                StatusMessage = $"ステップ {wrapper.RowNumber:0.##} はロック中のため実行をスキップしました。";
                return;
            }

            // 前の遷移をキャンセル
            // 2026-05-30 修正: Cancel 後に Dispose を追加（リソースリーク防止）
            _transitionCts?.Cancel();
            _transitionCts?.Dispose();
            _transitionCts = new CancellationTokenSource();
            var transitionToken = _transitionCts.Token;

            // B7 修正: 排他制御 — 前の API 呼び出しが完了するまで待つ（最大 5 秒でタイムアウト）
            // これにより、高速な連続選択で API 呼び出しが競合するのを防ぐ
            if (!await _executionSemaphore.WaitAsync(5000))
            {
                Log.Debug("ExecuteStepWithoutAdvance: semaphore timeout, skipping");
                return;
            }

            // B7: セマフォ待ち中にキャンセルされた場合はスキップ
            if (transitionToken.IsCancellationRequested)
            {
                _executionSemaphore.Release();
                return;
            }

            var step = wrapper.ToModel();
            var color = new Rgb(step.ColorR, step.ColorG, step.ColorB);
            var target = Target.All;

            try
            {
                // NO.15,17,18,19 修正: 全コマンド実行前に走行中エフェクトを確実に停止する。
                // Effect→Effect 遷移時に旧エフェクトが停止されず競合していた問題を解消。
                // NO.27,28 対策: 停止呼び出しがハングしても全体を長時間ブロックしないよう
                //   短いタイムアウト(2s)を付与し、遅延区間特定のため所要時間を計測する。
                var preStopSw = Stopwatch.StartNew();
                using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    // RainbowPause は「その時の色を保持」するコマンド。ここで走行中のレインボー
                    // （API 内部では Effect 扱いのループ）を止めてしまうと保持すべき色が失われ、
                    // 直後の A9 04（現在色ホールド）が白点灯になる。事前停止せず、下部ボタンと同じく
                    // PauseRainbow 自身に停止→現在色ホールドを任せる（StartPacketHold が内部で停止する）。
                    if (step.CommandType is not "RainbowPause")
                    {
                        try { await _lighting.StopEffectAsync(stopCts.Token); }
                        catch { /* 走っていない/タイムアウトは無視 */ }
                    }

                    if (step.CommandType is not "Rainbow" and not "RainbowStop" and not "RainbowPause")
                    {
                        try { await _lighting.StopRainbowAsync(stopCts.Token); }
                        catch { /* 走っていない/タイムアウトは無視 */ }
                    }
                }

                // API 側の停止処理完了を待つ（Effect→Effect 競合防止）。
                // NO.15/17/18/19: 30ms では端末側の停止完了に対して短く不作動が残るケースがあったため 50ms に引き上げ。
                await Task.Delay(50);

                if (preStopSw.ElapsedMilliseconds > 300)
                {
                    var msg = $"プリ停止に {preStopSw.ElapsedMilliseconds}ms（cmd={step.CommandType}/{step.EffectType}）";
                    AppendLog("PERF", msg);
                    Log.Warning("[PERF] {Msg}", msg); // NO.27/28: 現地ログ解析で遅延を捕捉できるよう永続化
                }

                switch (step.CommandType)
                {
                    case "Color":
                        // スムーズ遷移：TransitionMs > 0 かつ前回の色がある場合
                        if (step.TransitionMs > 0 && _lastExecutedColor.HasValue)
                        {
                            var from = _lastExecutedColor.Value;
                            var toR = step.ColorR;
                            var toG = step.ColorG;
                            var toB = step.ColorB;
                            var intervalMs = 30; // 補間間隔
                            var totalSteps = Math.Max(1, step.TransitionMs / intervalMs);

                            AppendLog("TX", $"Color 遷移 ({from.R},{from.G},{from.B})→({toR},{toG},{toB}) {step.TransitionMs}ms");
                            AppendContinuousSendStartLog();

                            for (int i = 1; i <= totalSteps; i++)
                            {
                                if (transitionToken.IsCancellationRequested) break;

                                var t = (double)i / totalSteps;
                                var r = (byte)(from.R + (toR - from.R) * t);
                                var g = (byte)(from.G + (toG - from.G) * t);
                                var b = (byte)(from.B + (toB - from.B) * t);
                                await _lighting.SetColorAsync(target, new Rgb(r, g, b));
                                if (i < totalSteps)
                                {
                                    try { await Task.Delay(intervalMs, transitionToken); }
                                    catch (OperationCanceledException) { break; }
                                }
                            }
                        }
                        else
                        {
                            await _lighting.SetColorAsync(target, color);
                            AppendLog("TX", $"Color ({step.ColorR},{step.ColorG},{step.ColorB}) retransmit={step.RetransmitCount}");
                            AppendContinuousSendStartLog();
                        }
                        _lastExecutedColor = (step.ColorR, step.ColorG, step.ColorB);
                        // U2: 送信状態をステータスバーに表示（Off含め全Color系で統一）
                        StatusMessage = $"送信完了 → RGB({step.ColorR},{step.ColorG},{step.ColorB}) 連続送信中";
                        break;

                    case "Off":
                        await _lighting.SetColorAsync(target, new Rgb(0, 0, 0));
                        AppendLog("TX", $"Off retransmit={step.RetransmitCount}");
                        AppendContinuousSendStartLog();
                        _lastExecutedColor = (0, 0, 0);
                        // U2: Off(RGB000)送信時にステータスバーにも送信状態を表示
                        StatusMessage = "Off 送信完了 → API 側で RGB(0,0,0) 連続送信中";
                        break;

                    // NO.35: Color2（2色交互点灯）廃止。実行ロジックを削除。
                    //        旧保存データに Color2 ステップが残っていても該当 case が無いため
                    //        何も送信されず無害（Cmdドロップダウンからも除外済み）。

                    case "Effect":
                        if (string.IsNullOrEmpty(step.EffectType))
                        {
                            StatusMessage = "エフェクト種別が未設定です。";
                            return;
                        }
                        // 連続実行フラグ：未指定（null）の場合は繰り返し（true）を採用
                        var effectContinuous = step.Continuous ?? true;
                        // NO.27,28 計測: シーケンス選択→Fade 開始までの遅延区間特定のため StartEffect の
                        //   HTTP 往復時間を計測する（300ms 超過時のみログ）。
                        var startFxSw = Stopwatch.StartNew();
                        await _lighting.StartEffectAsync(
                            effectType: step.EffectType,
                            color: color,
                            cycleDurationMs: step.GetEffectCycleDurationOrDefault(),
                            flashIntervalMs: step.EffectType == "Flash"
                                ? Math.Max(20, step.GetEffectCycleDurationOrDefault() / 2)
                                : (int?)null,
                            fadeSteps: step.GetFadeStepsOrDefault(),
                            continuous: effectContinuous);
                        if (startFxSw.ElapsedMilliseconds > 300)
                        {
                            var fxMsg = $"StartEffect HTTP に {startFxSw.ElapsedMilliseconds}ms（{step.EffectType}）";
                            AppendLog("PERF", fxMsg);
                            Log.Warning("[PERF] {Msg}", fxMsg); // NO.27/28: 現地ログ解析用に永続化
                        }
                        AppendLog("TX", $"Effect {FormatEffectDisplayName(step.EffectType, step.Continuous)} ({step.ColorR},{step.ColorG},{step.ColorB}) {FormatEffectParamsForLog(step.EffectType, step.GetEffectCycleDurationOrDefault(), step.GetFadeStepsOrDefault(), step.RetransmitCount, effectContinuous)}");
                        AppendContinuousSendStartLog();
                        _lastExecutedColor = (step.ColorR, step.ColorG, step.ColorB);
                        break;

                    case "EffectStop":
                        await _lighting.StopEffectAsync();
                        AppendLog("TX", "EffectStop");
                        AppendContinuousSendStopLog();
                        break;

                    case "InternalProgram":
                    {
                        var frame = step.FrameNo ?? 0;
                        await _lighting.PlayInternalProgramAsync(frame);
                        AppendLog("TX", $"InternalProgram frame={frame}");
                        StatusMessage = $"内蔵プログラム再生 → frame={frame}";
                        break;
                    }

                    case "Rainbow":
                    {
                        // 行に個別の色設定があればそれを優先（「この行に適用」で取り込んだ再現可能な設定）。
                        // 無ければ（プルダウンで Rainbow にしただけの行）コマンドパネルの現在設定を使う
                        //   → プルダウンでセットするだけで動き、パネルの速度/モード変更も反映される。
                        RainbowMode rbMode;
                        List<Rgb> colors;
                        int cycle;
                        int? blink, fadeIn, fadeOut;
                        byte? duty;
                        if (step.RainbowColors != null && step.RainbowColors.Count >= 2)
                        {
                            rbMode = step.RainbowMode ?? RainbowMode.Solid;
                            colors = step.RainbowColors;
                            cycle = step.RainbowCycleDurationMs ?? 1000;
                            blink = step.RainbowBlinkPeriodMs;
                            duty = step.RainbowDutyRatio;
                            fadeIn = step.RainbowFadeInMs;
                            fadeOut = step.RainbowFadeOutMs;
                        }
                        else
                        {
                            rbMode = RainbowMode;
                            colors = RainbowColors.Select(c => new Rgb(c.R, c.G, c.B)).ToList();
                            cycle = RainbowCycleDurationMs;
                            blink = RainbowMode == RainbowMode.Blink ? RainbowBlinkPeriodMs : (int?)null;
                            duty = RainbowMode == RainbowMode.Blink ? RainbowDutyRatio : (byte?)null;
                            fadeIn = RainbowMode is RainbowMode.FadeInOut or RainbowMode.FadeIn ? RainbowFadeInMs : (int?)null;
                            fadeOut = RainbowMode is RainbowMode.FadeInOut or RainbowMode.FadeOut ? RainbowFadeOutMs : (int?)null;
                        }
                        await _lighting.StartRainbowAsync(rbMode, colors, cycle, blink, duty, fadeIn, fadeOut);
                        AppendLog("TX", $"Rainbow {rbMode} ({colors.Count}色, cycle={cycle}ms)");
                        StatusMessage = $"Rainbow {rbMode} 開始";
                        break;
                    }

                    case "RainbowStop":
                    {
                        await _lighting.StopRainbowAsync();
                        AppendLog("TX", "RainbowStop");
                        StatusMessage = "Rainbow 停止";
                        break;
                    }

                    case "RainbowPause":
                    {
                        await _lighting.PauseRainbowAsync();
                        AppendLog("TX", "RainbowPause");
                        StatusMessage = "Rainbow 一時停止（色保持）";
                        break;
                    }

                    case "SignalOff":
                    {
                        // NO.26: シーケンス内から信号Off（端末セルフモード移行）
                        await _lighting.StopSignalAsync();
                        AppendLog("TX", "SignalOff (セルフモード移行)");
                        StatusMessage = "信号Off — 端末はセルフモードに復帰します";
                        break;
                    }

                    case "Preset":
                    {
                        // SelectedStep から直接 PresetSequenceName を取得
                        var presetName = SelectedStep?.PresetSequenceName ?? "";
                        if (string.IsNullOrWhiteSpace(presetName))
                        {
                            StatusMessage = "プリセットシーケンス名が未設定です。";
                            return;
                        }

                        // 前のサブシーケンスを停止
                        StopSubSequence();
                        _subSequenceCts = new CancellationTokenSource();
                        var subToken = _subSequenceCts.Token;

                        // シーケンスを名前で読み込み
                        var subSeq = _store.LoadByName(presetName);
                        if (subSeq == null)
                        {
                            StatusMessage = $"プリセットシーケンス「{presetName}」が見つかりません。";
                            return;
                        }

                        AppendLog("TX", $"Preset 開始: {presetName}（{subSeq.Steps.Count} ステップ）");
                        StatusMessage = $"Preset 実行中: {presetName}";

                        // fire-and-forget でサブシーケンスループを開始
                        _ = RunSubSequenceAsync(subSeq, subToken);
                        break;
                    }

                    default:
                        StatusMessage = $"未対応コマンド: {step.CommandType}";
                        return;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"実行失敗: {ex.Message}";
                AppendLog("ERR", $"実行失敗: {ex.Message}");
                Log.Warning(ex, "ExecuteStepWithoutAdvance failed");
            }
            finally
            {
                // B7: セマフォを解放
                _executionSemaphore.Release();
            }
        }

        // NO.37 修正: トレーリングデバウンス用の状態。
        // 旧実装（前回実行から 100ms 以内はスキップ）は R→G→B のように
        // 連続でセル編集すると 2 件目以降が捨てられ、最終色が送信されない不具合があった。
        // 「最後の編集から一定時間経過後に 1 回だけ実行」する方式に変更し、最終値を必ず反映する。
        private const int ReExecuteTrailingMs = 150;
        private DateTime _lastReExecuteRequest = DateTime.MinValue;
        private bool _reExecuteScheduled;
        private SequenceStepWrapper? _reExecuteTarget;

        /// <summary>
        /// セル編集確定・カラーピッカー確定時に現在ステップを再実行する（コードビハインドから呼び出し用）。
        /// </summary>
        /// <remarks>
        /// NO.37 修正: トレーリングデバウンス。連続編集（R/G/B を続けて変更等）が落ち着いた後、
        /// 対象行が選択されたままであれば最終値で 1 回だけ実行する。行を切り替えた場合は
        /// <see cref="OnSelectedStepChanged"/> 側が新しい行を実行するため、本トレーリングは何もしない。
        /// </remarks>
        public void ReExecuteCurrentStep()
        {
            if (_suppressAutoExecute) return;
            if (IsProgressLocked) return;
            if (SelectedStep == null) return;
            if (_lighting == null || !_lighting.IsConnected) return;

            _reExecuteTarget = SelectedStep;
            _lastReExecuteRequest = DateTime.UtcNow;
            if (_reExecuteScheduled) return; // 既にトレーリング実行が予約済み
            _reExecuteScheduled = true;
            _ = RunTrailingReExecuteAsync();
        }

        /// <summary>
        /// NO.37: 最後の編集要求から <see cref="ReExecuteTrailingMs"/> 待ってから、対象行を 1 回だけ実行する。
        /// </summary>
        private async Task RunTrailingReExecuteAsync()
        {
            try
            {
                // 連続編集が続く間は待ち続ける（最後の要求から一定時間経過するまで）
                while (true)
                {
                    var remaining = ReExecuteTrailingMs - (DateTime.UtcNow - _lastReExecuteRequest).TotalMilliseconds;
                    if (remaining <= 0) break;
                    await Task.Delay((int)Math.Ceiling(remaining));
                }

                if (_suppressAutoExecute || IsProgressLocked) return;
                if (_lighting == null || !_lighting.IsConnected) return;
                // 対象行が選択されたままの時のみ実行（行を切り替えていれば OnSelectedStepChanged が実行済み）
                if (SelectedStep == null || !ReferenceEquals(SelectedStep, _reExecuteTarget)) return;

                await ExecuteStepWithoutAdvanceAsync();
            }
            finally
            {
                _reExecuteScheduled = false;
            }
        }

        /// <summary>
        /// 複数行ループ実行を開始する（最大7行、連続行のみ）
        /// 概要：選択された連続行を TimeMs の差分で待機しながら順番に実行し、
        ///       末尾まで到達したら先頭に戻ってループする。
        ///       別の行が選択されるとキャンセルされる。
        /// </summary>
        public async Task StartLoopExecutionAsync(IList<SequenceStepWrapper> selectedSteps)
        {
            // 前のループをキャンセル
            StopLoopExecution();

            if (selectedSteps == null || selectedSteps.Count < 2 || selectedSteps.Count > 15) return;
            if (_lighting == null || !_lighting.IsConnected) return;
            if (IsEmergencyActive) return;

            // 連続行かチェック（EditingSteps内でインデックスが連続しているか）
            var indices = selectedSteps
                .Select(s => EditingSteps.IndexOf(s))
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToList();
            if (indices.Count != selectedSteps.Count) return;
            for (int i = 1; i < indices.Count; i++)
            {
                if (indices[i] != indices[i - 1] + 1) return; // 連続していない
            }

            var steps = indices.Select(i => EditingSteps[i]).ToList();
            _loopCts = new CancellationTokenSource();
            OnPropertyChanged(nameof(IsLoopRunning));
            var token = _loopCts.Token;

            StatusMessage = $"ループ実行中: {steps.Count} ステップ";
            AppendLog("INFO", $"ループ実行開始: {steps.Count} ステップ (No {steps.First().RowNumber:0.##}〜{steps.Last().RowNumber:0.##})");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        var step = steps[i];
                        if (step.IsLocked) continue;

                        // 選択を変更して実行（_suppressAutoExecute で自動実行を抑制し、手動で実行）
                        _suppressAutoExecute = true;
                        try
                        {
                            SelectedStep = step;
                            CurrentStepIndex = EditingSteps.IndexOf(step);
                        }
                        finally
                        {
                            _suppressAutoExecute = false;
                        }

                        await ExecuteStepWithoutAdvanceAsync();
                        if (token.IsCancellationRequested) break;

                        // 次のステップまでの待機時間を計算
                        int nextIdx = (i + 1) % steps.Count;
                        var current = step.ToModel();
                        var next = steps[nextIdx].ToModel();
                        int waitMs;
                        if (nextIdx > i)
                        {
                            waitMs = Math.Max(100, next.TimeMs - current.TimeMs);
                        }
                        else
                        {
                            // ループの末尾→先頭: 最後のステップから1秒後に先頭に戻る
                            waitMs = 1000;
                        }

                        try { await Task.Delay(waitMs, token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常キャンセル */ }
            catch (Exception ex)
            {
                AppendLog("ERR", $"ループ実行エラー: {ex.Message}");
            }

            StatusMessage = "ループ実行を停止しました。";
            AppendLog("INFO", "ループ実行停止");
        }

        /// <summary>
        /// 複数行ループ実行を停止する
        /// </summary>
        public void StopLoopExecution()
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;

            // Trig（Chase/OL 指定）は保存対象なので停止では消さない（開き直しで復元するため）。
            // 別グループで Chase/OL を開始したときに StartChase/StartOverlap 側で付け替える。
            // ここでは実行状態の変化のみ UI へ通知する。
            OnPropertyChanged(nameof(IsLoopRunning));
        }

        /// <summary>
        /// ループ実行中かどうか（SelectionChanged 抑制にも使用）
        /// </summary>
        public bool IsLoopRunning => _loopCts != null && !_loopCts.IsCancellationRequested;

        /// <summary>
        /// 指定行が「off」動作（消灯 / 信号Off）かどうか。
        /// Chase/OL からの離脱先が off のとき、停止ボタン相当の全停止に切り替える判定に使う。
        /// </summary>
        private static bool IsOffStep(SequenceStepWrapper? w)
            => w != null && (w.CommandType == "Off" || w.CommandType == "SignalOff");

        /// <summary>
        /// マウスクリックで Chase/OL を抜ける際に立てる一回限りのフラグ。
        /// 直後に確定する <see cref="SelectedStep"/> の変更（OnSelectedStepChanged）で消費し、
        /// 離脱先が off なら停止ボタン相当にするために使う。
        /// </summary>
        private bool _loopExitRequested;

        /// <summary>
        /// ユーザー入力（行クリック）による Chase/OL 離脱要求。ループを停止し、
        /// 直後の選択確定で off 判定を行うためのフラグを立てる。
        /// 実ユーザー入力のときだけ呼ばれるため、ループのプログラム的選択エコーで誤停止しない。
        /// </summary>
        public void RequestLoopExit()
        {
            if (!IsLoopRunning) return;
            _loopExitRequested = true;
            StopLoopExecution();
        }

        #endregion

        #region v3.9: Chase（往復）/ Overlap（OL）

        /// <summary>
        /// Chase/OL で Time・BPM が未設定（Time=0 かつ BPM が既定 120 または 0）のときに用いる
        /// 1 ステップあたりの既定周期（ms）。これにより未設定でも一定ペースで自動サイクルする。
        /// </summary>
        private const int DefaultLoopStepMs = 1000;

        /// <summary>
        /// v3.9: Chase（往復）実行を開始する
        /// 概要：DataGrid で選択された連続行(2-7行)で [Chase] ボタン押下 → 往復サイクル実行。
        ///       各行の点灯時間は Time 列(秒)。Time/BPM 未設定時は既定 1 秒/ステップで自動サイクル。
        ///       BPM > 0 の場合は BPM から待機時間を算出。
        ///       Trig 列に "Chase" を表示。
        ///       ↑↓カーソルや別行選択で中断、その行を実行して点灯継続。
        /// </summary>
        [RelayCommand]
        private async Task StartChaseAsync()
        {
            var group = ResolveLoopGroup("Chase");
            if (group == null)
            {
                StatusMessage = "Chase: 2〜15行の連続行を選択、または Chase マーカー行を選択してください。";
                return;
            }
            await StartChaseExecutionAsync(group);
        }

        /// <summary>
        /// v3.9: Overlap（OL）実行を開始する
        /// 概要：DataGrid で選択された連続行(2-7行)で [OL] ボタン押下 → 色クロスフェードのループ。
        ///       フェード時間は次の行の Time 列(秒)。Time/BPM 未設定時は既定 1 秒/ステップで自動サイクル。
        ///       BPM > 0 の場合は BPM から時間を算出。
        ///       Trig 列に "OL" を表示。
        ///       ↓カーソルで中断、最終行の色で点灯継続。
        /// </summary>
        [RelayCommand]
        private async Task StartOverlapAsync()
        {
            var group = ResolveLoopGroup("OL");
            if (group == null)
            {
                StatusMessage = "OL: 2〜15行の連続行を選択、または OL マーカー行を選択してください。";
                return;
            }
            await StartOverlapExecutionAsync(group);
        }

        /// <summary>
        /// Chase/OL ボタン押下時に、実行対象の連続行グループを解決する。
        /// ① 連続 2〜7 行が選択されていればそれ（新規グループ定義／再定義）を優先。
        /// ② そうでなければ（1行だけ選択など）、選択行を含む同種マーカー(mode)の連続ブロックを対象にする。
        ///    → 保存済み Chase/OL 行を1行選んで押すだけで、そのグループ全体を再実行できる。
        /// どちらも満たさなければ null。
        /// </summary>
        private IList<SequenceStepWrapper>? ResolveLoopGroup(string mode)
        {
            // ① 明示的な連続 2〜15 行選択を優先
            var sel = _currentSelectedSteps;
            if (sel != null && sel.Count >= 2 && sel.Count <= 15)
            {
                var idxs = sel.Select(s => EditingSteps.IndexOf(s))
                              .Where(i => i >= 0).OrderBy(i => i).ToList();
                if (idxs.Count == sel.Count && IsContiguousIndices(idxs))
                    return idxs.Select(i => EditingSteps[i]).ToList();
            }

            // ② 選択行（単一含む）が mode マーカーのブロック内なら、その連続ブロックを対象にする
            var anchor = (sel != null && sel.Count > 0) ? sel[0] : SelectedStep;
            if (anchor != null)
            {
                int idx = EditingSteps.IndexOf(anchor);
                if (idx >= 0 && EditingSteps[idx].Trig == mode)
                {
                    int start = idx, end = idx;
                    while (start - 1 >= 0 && EditingSteps[start - 1].Trig == mode) start--;
                    while (end + 1 < EditingSteps.Count && EditingSteps[end + 1].Trig == mode) end++;
                    int count = end - start + 1;
                    if (count >= 2 && count <= 15)
                    {
                        var group = new List<SequenceStepWrapper>();
                        for (int i = start; i <= end; i++) group.Add(EditingSteps[i]);
                        return group;
                    }
                }
            }
            return null;
        }

        /// <summary>インデックス列が連続（+1 ずつ）かどうか。</summary>
        private static bool IsContiguousIndices(List<int> idxs)
        {
            for (int i = 1; i < idxs.Count; i++)
                if (idxs[i] != idxs[i - 1] + 1) return false;
            return true;
        }

        /// <summary>
        /// クリック入口：選択中の行が Chase/OL マーカーなら、そのブロックのループを自動起動する。
        /// View の PreviewMouseLeftButtonDown（ループ非実行中）から選択確定後に呼ばれる。
        /// クリックは明示操作なので enter-from-outside 判定は行わない（同じ行の再クリック＝再実行も可）。
        /// </summary>
        public void TryAutoStartLoopForSelectedRow()
            => TryAutoStartLoop(SelectedStep, prevForEnterCheck: null);

        /// <summary>
        /// マーカー行に「カーソルが当たった」ときの Chase/OL 自動起動。
        /// 起動条件：対象行が "Chase"/"OL" マーカー、接続済み、非常停止/進行ロックでない、
        /// 同種マーカーの連続ブロック(2〜15行)が成立すること。
        /// prevForEnterCheck != null（矢印）の場合、直前行が同ブロック内なら起動しない
        /// （ブロック内を矢印で移動＝離脱側の操作。起動⇔停止の往復ジャンクを防ぐ）。
        /// ※ ユーザー操作の2入口（クリック / 矢印）からのみ呼ぶこと。プログラム的な
        ///    SelectedStep 変更（編集・読込・ループのエコー）から呼ぶと誤起動する。
        /// </summary>
        private void TryAutoStartLoop(SequenceStepWrapper? row, SequenceStepWrapper? prevForEnterCheck)
        {
            if (row == null) return;
            var mode = row.Trig;                       // "Chase" / "OL" / ""
            if (mode != "Chase" && mode != "OL") return;
            if (_lighting == null || !_lighting.IsConnected) return;
            if (IsEmergencyActive || IsProgressLocked) return;

            int idx = EditingSteps.IndexOf(row);
            if (idx < 0) return;

            // 同種マーカーの連続ブロックを展開（ResolveLoopGroup② と同じ規則）
            int start = idx, end = idx;
            while (start - 1 >= 0 && EditingSteps[start - 1].Trig == mode) start--;
            while (end + 1 < EditingSteps.Count && EditingSteps[end + 1].Trig == mode) end++;
            int count = end - start + 1;
            if (count < 2 || count > 15) return;

            // 矢印：ブロック外→内へ入った時のみ起動（同ブロック内移動では起動しない）
            if (prevForEnterCheck != null)
            {
                int p = EditingSteps.IndexOf(prevForEnterCheck);
                if (p >= start && p <= end) return;
            }

            var group = new List<SequenceStepWrapper>();
            for (int i = start; i <= end; i++) group.Add(EditingSteps[i]);

            if (mode == "Chase") _ = StartChaseExecutionAsync(group);
            else                 _ = StartOverlapExecutionAsync(group);
        }

        /// <summary>
        /// Chase/OL の指定（Trig マーカー）を解除する。選択行があればその行のみ、無ければ全行。
        /// 複数個所に設定した指定を個別に消したいときに使う。実行中ループがあれば停止する。
        /// </summary>
        [RelayCommand]
        private void ClearLoopMark()
        {
            StopLoopExecution();
            var targets = (_currentSelectedSteps != null && _currentSelectedSteps.Count > 0)
                ? _currentSelectedSteps.ToList()
                : EditingSteps.ToList();
            foreach (var w in targets) w.Trig = "";
            StatusMessage = $"Chase/OL 指定を解除しました（{targets.Count} 行）。";
        }

        /// <summary>
        /// Chase モードでのループ実行（BPM/Time 対応）
        /// </summary>
        public async Task StartChaseExecutionAsync(IList<SequenceStepWrapper> selectedSteps)
        {
            StopLoopExecution();

            if (selectedSteps == null || selectedSteps.Count < 2 || selectedSteps.Count > 15) return;
            if (_lighting == null || !_lighting.IsConnected) return;
            if (IsEmergencyActive) return;

            var indices = selectedSteps
                .Select(s => EditingSteps.IndexOf(s))
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToList();
            if (indices.Count != selectedSteps.Count)
            {
                StatusMessage = "Chase: 連続する2〜15行を選択し直してから実行してください。";
                return;
            }
            for (int i = 1; i < indices.Count; i++)
            {
                if (indices[i] != indices[i - 1] + 1)
                {
                    StatusMessage = "Chase: 連続した行を選択してください。";
                    return;
                }
            }

            var steps = indices.Select(i => EditingSteps[i]).ToList();

            // Trig 列にマーク（複数個所の Chase/OL を共存させるため、他グループの指定はクリアしない。
            // 対象行のみ上書きする。指定の全消去は「解除」ボタンで行う）
            foreach (var s in steps) s.Trig = "Chase";

            _loopCts = new CancellationTokenSource();
            OnPropertyChanged(nameof(IsLoopRunning));
            var token = _loopCts.Token;

            StatusMessage = $"Chase 実行中: {steps.Count} ステップ";
            AppendLog("INFO", $"Chase 開始: {steps.Count} ステップ");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        var step = steps[i];

                        _suppressAutoExecute = true;
                        try
                        {
                            SelectedStep = step;
                            CurrentStepIndex = EditingSteps.IndexOf(step);
                        }
                        finally { _suppressAutoExecute = false; }

                        var sw = Stopwatch.StartNew();
                        await ExecuteStepWithoutAdvanceAsync();
                        if (token.IsCancellationRequested) break;

                        // 待機時間: BPM > 0 なら BPM 優先、そうでなければ Time 列
                        // 未設定（Time=0 かつ BPM 既定120/0）のときは既定周期で自動サイクル（1色目で固着しない）
                        int waitMs;
                        if (step.Bpm > 0 && step.Bpm != 120)
                        {
                            waitMs = Math.Max(20, 60000 / step.Bpm);
                        }
                        else
                        {
                            waitMs = step.TimeMs > 0 ? step.TimeMs : DefaultLoopStepMs;
                        }

                        // API レイテンシを差し引いて正確なテンポを維持（waitMs は常に正）
                        var elapsed = (int)sw.ElapsedMilliseconds;
                        var adjustedWait = Math.Max(1, waitMs - elapsed);
                        try { await Task.Delay(adjustedWait, token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendLog("ERR", $"Chase エラー: {ex.Message}"); }

            // Trig（Chase 指定）は保存対象なのでループ終了時も残す。
            StatusMessage = "Chase 停止";
            AppendLog("INFO", "Chase 停止");
        }

        /// <summary>
        /// Overlap モードでのループ実行（色クロスフェード）
        /// </summary>
        public async Task StartOverlapExecutionAsync(IList<SequenceStepWrapper> selectedSteps)
        {
            StopLoopExecution();

            if (selectedSteps == null || selectedSteps.Count < 2 || selectedSteps.Count > 15) return;
            if (_lighting == null || !_lighting.IsConnected) return;
            if (IsEmergencyActive) return;

            var indices = selectedSteps
                .Select(s => EditingSteps.IndexOf(s))
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToList();
            if (indices.Count != selectedSteps.Count)
            {
                StatusMessage = "OL: 連続する2〜15行を選択し直してから実行してください。";
                return;
            }
            for (int i = 1; i < indices.Count; i++)
            {
                if (indices[i] != indices[i - 1] + 1)
                {
                    StatusMessage = "OL: 連続した行を選択してください。";
                    return;
                }
            }

            var steps = indices.Select(i => EditingSteps[i]).ToList();

            // Trig 列にマーク（他グループの指定はクリアせず対象行のみ上書き＝複数個所の共存を許可）
            foreach (var s in steps) s.Trig = "OL";

            _loopCts = new CancellationTokenSource();
            OnPropertyChanged(nameof(IsLoopRunning));
            var token = _loopCts.Token;

            StatusMessage = $"Overlap 実行中: {steps.Count} ステップ";
            AppendLog("INFO", $"Overlap 開始: {steps.Count} ステップ");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        var current = steps[i];
                        var next = steps[(i + 1) % steps.Count];

                        // フェード時間: 次の行の BPM/Time。
                        // 未設定（Time=0 かつ BPM 既定120/0）は既定周期で自動サイクル（0 だと無遅延ループ＝暴走するため）
                        int fadeMs;
                        if (next.Bpm > 0 && next.Bpm != 120)
                        {
                            fadeMs = Math.Max(20, 60000 / next.Bpm);
                        }
                        else
                        {
                            fadeMs = next.TimeMs > 0 ? next.TimeMs : DefaultLoopStepMs;
                        }

                        // フェードステップ数。stepInterval は常に >=1 ＝毎フレーム必ず throttle（暴走防止）
                        int fadeStepCount = Math.Max(1, fadeMs / 20);
                        int stepInterval = Math.Max(1, fadeMs / fadeStepCount);

                        // 現在色→次色へ補間（API レイテンシ補正付き）
                        for (int s = 0; s <= fadeStepCount; s++)
                        {
                            if (token.IsCancellationRequested) break;

                            float t = fadeStepCount > 0 ? (float)s / fadeStepCount : 1f;
                            byte r = (byte)(current.ColorR + (next.ColorR - current.ColorR) * t);
                            byte g = (byte)(current.ColorG + (next.ColorG - current.ColorG) * t);
                            byte b = (byte)(current.ColorB + (next.ColorB - current.ColorB) * t);

                            var sw = Stopwatch.StartNew();
                            await _lighting.SetColorAsync(Target.All, new Rgb(r, g, b));

                            if (s < fadeStepCount)
                            {
                                var elapsed = (int)sw.ElapsedMilliseconds;
                                var adjustedDelay = Math.Max(1, stepInterval - elapsed);
                                try { await Task.Delay(adjustedDelay, token); }
                                catch (OperationCanceledException) { break; }
                            }
                        }

                        // 選択をUIに反映
                        _suppressAutoExecute = true;
                        try
                        {
                            SelectedStep = next;
                            CurrentStepIndex = EditingSteps.IndexOf(next);
                        }
                        finally { _suppressAutoExecute = false; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendLog("ERR", $"Overlap エラー: {ex.Message}"); }

            // Trig（OL 指定）は保存対象なのでループ終了時も残す。
            StatusMessage = "Overlap 停止";
            AppendLog("INFO", "Overlap 停止");
        }

        #endregion Chase / Overlap

        #region サブシーケンス（Preset）実行

        /// <summary>
        /// サブシーケンスをループ再生する（fire-and-forget で呼ばれる）。
        /// キャンセルされると最終色を維持して停止する。
        /// </summary>
        private async Task RunSubSequenceAsync(TimeBasedSequence subSeq, CancellationToken ct)
        {
            try
            {
                // NO.30: サブシーケンスは保存順で再生し、各行の Time(sec)=その行の表示時間(duration)
                //        として扱う（親タイムラインの「絶対時刻差分」方式とは別の専用仕様）。
                //        NO.25 で新規行 TimeMs 既定が 0 になり、隣接差分方式だと全行0で各20ms
                //        ＝約0.1秒終了の回帰が出ていたため duration 方式に統一する。
                var playSteps = subSeq.Steps.ToList();
                if (playSteps.Count == 0) return;

                while (!ct.IsCancellationRequested)
                {
                    for (int i = 0; i < playSteps.Count; i++)
                    {
                        if (ct.IsCancellationRequested) break;

                        var step = playSteps[i];

                        // 再帰 Preset 防止：サブシーケンス内の Preset ステップは無視
                        if (step.CommandType == "Preset") continue;

                        await ExecuteSubSequenceStepAsync(step, ct);
                        if (ct.IsCancellationRequested) break;

                        // 当該ステップを表示する時間だけ待機（BPM>0 を優先、未設定/0 は既定1秒）。
                        int bpm = step.Bpm ?? 0;
                        int waitMs;
                        if (bpm > 0 && bpm != 120)
                            waitMs = Math.Max(20, 60000 / bpm);
                        else
                            waitMs = step.TimeMs > 0 ? step.TimeMs : 1000;

                        try { await Task.Delay(waitMs, ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常キャンセル */ }
            catch (Exception ex)
            {
                AppendLog("ERR", $"Preset 実行エラー: {ex.Message}");
                Log.Warning(ex, "RunSubSequenceAsync failed");
            }

            AppendLog("INFO", "Preset 停止（最終色維持）");
        }

        /// <summary>
        /// サブシーケンスの 1 ステップを実行する。
        /// _executionSemaphore は取得しない（デッドロック回避）。
        /// </summary>
        private async Task ExecuteSubSequenceStepAsync(SequenceStep step, CancellationToken ct)
        {
            if (_lighting == null || !_lighting.IsConnected) return;

            var color = new Rgb(step.ColorR, step.ColorG, step.ColorB);
            var target = Target.All;

            try
            {
                switch (step.CommandType)
                {
                    case "Color":
                        await _lighting.SetColorAsync(target, color, ct);
                        _lastExecutedColor = (step.ColorR, step.ColorG, step.ColorB);
                        break;

                    case "Off":
                        await _lighting.SetColorAsync(target, new Rgb(0, 0, 0), ct);
                        _lastExecutedColor = (0, 0, 0);
                        break;

                    // NO.35: Color2 廃止。サブシーケンスでも実行ロジックを削除（該当caseなしで無害）。

                    case "Effect":
                        if (string.IsNullOrEmpty(step.EffectType)) break;
                        var effectContinuous = step.Continuous ?? true;
                        await _lighting.StartEffectAsync(
                            effectType: step.EffectType,
                            color: color,
                            cycleDurationMs: step.GetEffectCycleDurationOrDefault(),
                            flashIntervalMs: step.EffectType == "Flash"
                                ? Math.Max(20, step.GetEffectCycleDurationOrDefault() / 2)
                                : (int?)null,
                            fadeSteps: step.GetFadeStepsOrDefault(),
                            continuous: effectContinuous,
                            ct: ct);
                        _lastExecutedColor = (step.ColorR, step.ColorG, step.ColorB);
                        break;

                    case "EffectStop":
                        await _lighting.StopEffectAsync(ct);
                        break;

                    case "InternalProgram":
                        await _lighting.PlayInternalProgramAsync(step.FrameNo ?? 0, ct);
                        break;

                    case "Rainbow":
                    {
                        var rbColors = step.RainbowColors ?? DefaultRainbowColors()
                            .Select(c => new Rgb(c.R, c.G, c.B)).ToList();
                        await _lighting.StartRainbowAsync(
                            step.RainbowMode ?? RainbowMode.Solid,
                            rbColors,
                            step.RainbowCycleDurationMs ?? 1000,
                            step.RainbowBlinkPeriodMs,
                            step.RainbowDutyRatio,
                            step.RainbowFadeInMs,
                            step.RainbowFadeOutMs,
                            ct);
                        break;
                    }

                    case "RainbowStop":
                        await _lighting.StopRainbowAsync(ct);
                        break;

                    case "RainbowPause":
                        await _lighting.PauseRainbowAsync(ct);
                        break;

                    case "SignalOff":
                        await _lighting.StopSignalAsync(ct);
                        break;

                    // "Preset" は RunSubSequenceAsync 側でスキップ済み
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug(ex, "ExecuteSubSequenceStepAsync failed: {Cmd}", step.CommandType);
            }
        }

        /// <summary>
        /// 実行中のサブシーケンスを停止する（最終色維持）。
        /// </summary>
        private void StopSubSequence()
        {
            _subSequenceCts?.Cancel();
            _subSequenceCts?.Dispose();
            _subSequenceCts = null;
        }

        #endregion サブシーケンス（Preset）実行

        #region Undo

        /// <summary>
        /// 現在の EditingSteps のスナップショットを Undo 履歴に保存する。
        /// 編集操作の直前に呼び出すこと。
        /// </summary>
        private void SaveUndoState()
        {
            var snapshot = EditingSteps.Select(w => w.ToModel()).ToList();
            _undoHistory.Add(snapshot);
            if (_undoHistory.Count > _maxUndoDepth)
                _undoHistory.RemoveAt(0);
            OnPropertyChanged(nameof(CanUndo));
        }

        /// <summary>
        /// セル編集開始時にスナップショットを保存する（View から呼ばれる公開メソッド）。
        /// </summary>
        public void OnCellEditBeginning()
        {
            SaveUndoState();
        }

        /// <summary>
        /// 直前の状態に戻す。履歴がなければ何もしない。
        /// </summary>
        [RelayCommand]
        private void Undo()
        {
            if (_undoHistory.Count == 0)
            {
                StatusMessage = "元に戻す操作がありません。";
                return;
            }

            var previous = _undoHistory[^1];
            _undoHistory.RemoveAt(_undoHistory.Count - 1);

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Clear();
                foreach (var step in previous)
                {
                    EditingSteps.Add(new SequenceStepWrapper(step));
                }
                RenumberEditingSteps();
                CurrentStepIndex = EditingSteps.Count > 0 ? 0 : -1;
                SelectedStep = CurrentStepIndex >= 0 ? EditingSteps[CurrentStepIndex] : null;
            }
            finally
            {
                _suppressAutoExecute = false;
            }

            OnPropertyChanged(nameof(CurrentStepLabel));
            OnPropertyChanged(nameof(CanUndo));
            StatusMessage = $"元に戻しました（残り {_undoHistory.Count} 回）";
            AppendLog("INFO", $"Undo 実行（残り {_undoHistory.Count} 回）");
        }

        #endregion Undo

        #region コマンド：API 一括再生

        [RelayCommand]
        private async Task PlayOrStopAsync()
        {
            if (IsEmergencyActive)
            {
                StatusMessage = "Emergency モード中: シーケンス再生は抑止されています。";
                return;
            }
            if (_lighting == null) { StatusMessage = "API 連携が無効のため再生できません。"; return; }

            if (IsPlaying)
            {
                try
                {
                    // 停止前に現在実行中ステップの色を退避
                    SequenceStepWrapper? lastWrapper = null;
                    try
                    {
                        var (_, _, currentIdx, _) = await _lighting.GetSequencePlayStatusDetailedAsync();
                        if (currentIdx >= 0 && currentIdx < EditingSteps.Count)
                        {
                            lastWrapper = EditingSteps[currentIdx];
                        }
                    }
                    catch { /* 取得失敗時はスキップ */ }

                    await _lighting.StopSequenceAsync();
                    AppendLog("TX", "Sequence Stop");

                    // 停止後、直前の Color/Effect 行の色を再送して点灯維持
                    if (lastWrapper != null)
                    {
                        var step = lastWrapper.ToModel();
                        if (step.CommandType == "Color" || step.CommandType == "Effect")
                        {
                            var color = new Rgb(step.ColorR, step.ColorG, step.ColorB);
                            await _lighting.SetColorAsync(Target.All, color);
                            AppendLog("TX", $"Stop hold color ({step.ColorR},{step.ColorG},{step.ColorB})");
                            StatusMessage = $"シーケンスを停止しました（色保持: {step.ColorR},{step.ColorG},{step.ColorB}）。";
                        }
                        else
                        {
                            StatusMessage = "シーケンスを停止しました。";
                        }
                    }
                    else
                    {
                        StatusMessage = "シーケンスを停止しました。";
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = $"停止失敗: {ex.Message}";
                    AppendLog("ERR", $"Sequence Stop 失敗: {ex.Message}");
                }
                finally { await PollStatusAsync(); }
                return;
            }

            if (SelectedSequence == null || _editingSequence == null)
            {
                StatusMessage = "再生対象のシーケンスを選択してください。";
                return;
            }

            if (HasUnsavedEdits())
            {
                var ans = MessageBox.Show(
                    "編集内容が未保存です。保存してから再生しますか？",
                    "未保存の編集",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (ans == MessageBoxResult.Cancel) return;
                if (ans == MessageBoxResult.Yes) SaveSequence();
            }

            try
            {
                var apiSteps = SequenceApiMapper.ToApiSteps(_editingSequence);
                await _lighting.UpsertSequenceAsync(_editingSequence.Name, apiSteps);
                await _lighting.PlaySequenceAsync(_editingSequence.Name);
                StatusMessage = $"再生開始: {_editingSequence.Name}（{apiSteps.Count} ステップ）";
                AppendLog("TX", $"Sequence Play: {_editingSequence.Name} ({apiSteps.Count} ステップ)");

                // 1 行目を即時にハイライト・ログ出力する
                // 状態ポーリング更新は時刻 0 のステップを取り逃がす場合があるため、ここで先に反映する
                if (EditingSteps.Count > 0)
                {
                    IsPlaying = true;
                    PlayingSequenceName = _editingSequence.Name;
                    UpdatePlayingStepHighlight(0);
                    AppendStepProgressLog(0);
                    _lastLoggedStepIndex = 0;
                }
                else
                {
                    _lastLoggedStepIndex = -1;
                }
                AdjustPollingInterval(true);

                // 1 行目のハイライトをユーザーに視認させるため、初回ポーリング前に短く待機する
                await Task.Delay(150);
            }
            catch (Exception ex)
            {
                StatusMessage = $"再生失敗: {ex.Message}";
                AppendLog("ERR", $"Sequence Play 失敗: {ex.Message}");
                Log.Warning(ex, "Sequence Play failed");
            }
            finally { await PollStatusAsync(); }
        }

        [RelayCommand]
        private async Task SyncToApiAsync()
        {
            if (_lighting == null || _editingSequence == null) { StatusMessage = "同期対象がありません。"; return; }
            try
            {
                _editingSequence.Steps = EditingSteps.Select(w => w.ToModel()).ToList();
                var apiSteps = SequenceApiMapper.ToApiSteps(_editingSequence);
                await _lighting.UpsertSequenceAsync(_editingSequence.Name, apiSteps);
                StatusMessage = $"API へ同期完了: {_editingSequence.Name}（{apiSteps.Count} ステップ）";
                AppendLog("TX", $"Sequence Sync: {_editingSequence.Name} ({apiSteps.Count} ステップ)");
            }
            catch (Exception ex)
            {
                StatusMessage = $"同期失敗: {ex.Message}";
                AppendLog("ERR", $"Sequence Sync 失敗: {ex.Message}");
                Log.Warning(ex, "SyncToApi failed");
            }
        }

        /// <summary>
        /// シーケンス一覧を再読込（左ペインの「更新」ボタンから呼ばれる）
        /// </summary>
        [RelayCommand]
        private void RefreshList()
        {
            var currentId = SelectedSequence?.Id;

            // 再読込に伴う選択遷移は自動実行を抑制
            _suppressAutoExecute = true;
            try
            {
                ReloadSequences();
                if (!string.IsNullOrEmpty(currentId))
                {
                    SelectedSequence = Sequences.FirstOrDefault(s => s.Id == currentId);
                }
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            StatusMessage = $"一覧を更新しました（{Sequences.Count} 件）。";
        }

        /// <summary>
        /// 記録中かどうか（記録開始ボタンの表記切替に使用）
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RecordButtonText))]
        private bool isRecording;

        /// <summary>
        /// 記録ボタンの表示テキスト（記録中は「■ 記録停止」）
        /// </summary>
        public string RecordButtonText => IsRecording ? "■ 記録停止" : "● 記録開始";

        /// <summary>
        /// 記録の開始/停止トグル
        /// 概要：操作した色変更や Effect コマンドを順次シーケンスとして記録する。
        ///       本格的な記録ロジックは別 Phase で実装予定（API の record/start, record/stop を利用）。
        /// </summary>
        [RelayCommand]
        private void ToggleRecording()
        {
            if (!IsRecording)
            {
                IsRecording = true;
                StatusMessage = "記録機能は別 Phase で実装予定です（ボタン状態のみ切替）。";
            }
            else
            {
                IsRecording = false;
                StatusMessage = "記録停止しました。";
            }
        }

        #endregion

        #region 内部メソッド

        private void ReloadSequences()
        {
            Sequences.Clear();
            foreach (var seq in _store.LoadAll())
            {
                Sequences.Add(seq);
            }
        }

        /// <summary>
        /// EditingSteps の RowNumber を振り直す
        /// 概要：行追加・削除・複製・移動・全クリア・テンプレ挿入後に呼ぶ。
        ///       F1 修正: RowNumber が 0（未設定）の行のみ自動採番する。
        ///       ユーザーが手入力した番号は保持される。
        /// </summary>
        private void RenumberEditingSteps()
        {
            // 新規追加行（RowNumber == 0）は空欄のまま残す。
            // オペレータが手動で番号を振る運用とする。
            // RowNumber == 0 は DataGrid 上で空欄表示される（StringFormat 0.## → "0" は表示されるが、
            // 実際には未設定状態を示す）。
        }

        /// <summary>
        /// 全行の RowNumber を現在のコレクション順で 1, 2, 3, ... に強制再採番する。
        /// ソート後に呼び出すことで、表示順とコレクション順を完全に一致させる。
        /// </summary>
        private void ForceRenumberAllSteps()
        {
            for (int i = 0; i < EditingSteps.Count; i++)
            {
                EditingSteps[i].RowNumber = i + 1;
            }
        }

        /// <summary>
        /// DataGrid カラムヘッダークリック時のソート処理。
        /// 実コレクションを並べ替え＋全行再採番し、表示順とコレクション順を一致させる。
        /// </summary>
        public void SortEditingStepsByColumn(string? sortMemberPath, bool ascending)
        {
            if (EditingSteps.Count == 0) return;
            SaveUndoState();

            var sorted = ascending
                ? EditingSteps.OrderBy(w => GetSortValue(w, sortMemberPath)).ToList()
                : EditingSteps.OrderByDescending(w => GetSortValue(w, sortMemberPath)).ToList();

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Clear();
                foreach (var w in sorted) EditingSteps.Add(w);
                RenumberEditingSteps();

                if (EditingSteps.Count > 0)
                {
                    CurrentStepIndex = 0;
                    SelectedStep = EditingSteps[0];
                }
                else
                {
                    CurrentStepIndex = -1;
                    SelectedStep = null;
                }
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"ソートしました（{EditingSteps.Count} 件）。";
        }

        /// <summary>ソート用にプロパティ値を取得する</summary>
        private static object GetSortValue(SequenceStepWrapper w, string? path) => path switch
        {
            "RowNumber" => w.RowNumber,
            "Comment" => w.Comment ?? "",
            "TimeSec" => w.TimeSec ?? 0.0,
            "TimeMs" => w.TimeMs,
            "Bpm" => w.Bpm,
            "CommandType" => w.CommandType ?? "",
            "ColorR" => w.ColorR,
            "ColorG" => w.ColorG,
            "ColorB" => w.ColorB,
            "EffectTypeDisplay" => w.EffectTypeDisplay ?? "",
            "EffectCycleDurationMs" => w.EffectCycleDurationMs,
            "FadeSteps" => w.FadeSteps,
            "PresetSequenceName" => w.PresetSequenceName ?? "",
            "FrameNo" => w.FrameNo,
            "Note" => w.Note ?? "",
            "RetransmitCount" => w.RetransmitCount,
            _ => w.RowNumber,
        };

        /// <summary>停止中のポーリング間隔（軽量）</summary>
        private static readonly TimeSpan PollIntervalIdle = TimeSpan.FromMilliseconds(1500);

        /// <summary>連続再生中のポーリング間隔（高頻度・ハイライト追従用）</summary>
        private static readonly TimeSpan PollIntervalPlaying = TimeSpan.FromMilliseconds(200);

        private async Task PollStatusAsync()
        {
            if (_lighting == null) return;
            try
            {
                var (playing, name, currentIdx, _) = await _lighting.GetSequencePlayStatusDetailedAsync();
                IsPlaying = playing;
                PlayingSequenceName = name;

                // 連続再生中の行ハイライト：currentIdx に一致する行のみ true、他は false
                UpdatePlayingStepHighlight(playing ? currentIdx : -1);

                // 現ステップ詳細（ステータスバー表示用）
                UpdatePlayingStepDetail(playing ? currentIdx : -1);

                // 連続再生中のステップ進行を検知して [TX] ログを残す
                // ポーリング間隔より短い時間でステップが進んだ場合に取りこぼさないよう、
                // 前回ログ済みの次のステップから現在ステップまでを順番にログ出力する
                if (playing && currentIdx >= 0
                    && currentIdx > _lastLoggedStepIndex
                    && currentIdx < EditingSteps.Count)
                {
                    for (int i = _lastLoggedStepIndex + 1; i <= currentIdx; i++)
                    {
                        AppendStepProgressLog(i);
                    }
                    _lastLoggedStepIndex = currentIdx;
                }

                // 再生終了を検知（前回 playing=true → 今回 false の遷移）
                if (_wasPlaying && !playing)
                {
                    int total = EditingSteps.Count;
                    AppendLog("INFO", $"Sequence 再生完了 ({total} ステップ)");
                    _lastLoggedStepIndex = -1;
                }
                _wasPlaying = playing;

                // 再生中は高頻度、停止中は通常頻度に切替
                AdjustPollingInterval(playing);
            }
            catch
            {
                IsPlaying = false;
                PlayingSequenceName = null;
                UpdatePlayingStepHighlight(-1);
                UpdatePlayingStepDetail(-1);
                AdjustPollingInterval(false);
            }
        }

        /// <summary>
        /// エフェクト種別の表示名を返す
        /// 連続実行フラグが false の FadeIn / FadeOut は「Fade In/In」「Fade Out/Out」と表示する
        /// </summary>
        private static string FormatEffectDisplayName(string? effectType, bool? continuous)
        {
            if (continuous == false)
            {
                if (effectType == "FadeIn") return "FadeIn/In";
                if (effectType == "FadeOut") return "FadeOut/Out";
            }
            return effectType ?? "";
        }

        // 連続送信の状態を示すマーカー（送信ログで「20ms 間隔の連続送信が継続中」と明示する）
        private const string ContinuousSendStartMarker = "↳ API 側で 20ms 間隔の連続送信中（次コマンドまたは停止まで継続）";
        private const string ContinuousSendStopMarker = "↳ 連続送信を停止";

        /// <summary>
        /// 連続送信開始マーカーを送信ログに追記する（Color / Off / Effect の TX 直後に使用）
        /// </summary>
        private void AppendContinuousSendStartLog()
        {
            AppendLog("INFO", ContinuousSendStartMarker);
        }

        /// <summary>
        /// 連続送信停止マーカーを送信ログに追記する（EffectStop / Sequence Stop の TX 直後に使用）
        /// </summary>
        private void AppendContinuousSendStopLog()
        {
            AppendLog("INFO", ContinuousSendStopMarker);
        }

        /// <summary>
        /// Effect コマンドのパラメータをログ用文字列に整形する
        /// Flash の場合は flashInterval（ON/OFF 片側時間 = cycle ÷ 2）も含める
        /// </summary>
        private static string FormatEffectParamsForLog(string? effectType, int cycle, int fade, int retransmit, bool continuous)
        {
            if (effectType == "Flash")
            {
                var flashInterval = Math.Max(20, cycle / 2);
                return $"cycle={cycle}ms flashInterval={flashInterval}ms fade={fade} retransmit={retransmit} cont={continuous}";
            }
            return $"cycle={cycle}ms fade={fade} retransmit={retransmit} cont={continuous}";
        }

        /// <summary>
        /// 連続再生中、進行したステップの内容を [TX] ログに記録
        /// </summary>
        private void AppendStepProgressLog(int stepIndex)
        {
            if (stepIndex < 0 || stepIndex >= EditingSteps.Count) return;

            var w = EditingSteps[stepIndex];
            int total = EditingSteps.Count;
            bool effectContinuous = w.Continuous ?? true;
            string detail = w.CommandType switch
            {
                "Color"      => $"Step {stepIndex + 1}/{total}: Color ({w.ColorR},{w.ColorG},{w.ColorB}) retransmit={w.RetransmitCount}",
                "Off"        => $"Step {stepIndex + 1}/{total}: Off retransmit={w.RetransmitCount}",
                "Effect"     => $"Step {stepIndex + 1}/{total}: Effect {FormatEffectDisplayName(w.EffectType, w.Continuous)} ({w.ColorR},{w.ColorG},{w.ColorB}) {FormatEffectParamsForLog(w.EffectType, w.EffectCycleDurationMs > 0 ? w.EffectCycleDurationMs : 1000, w.FadeSteps > 0 ? w.FadeSteps : 20, w.RetransmitCount, effectContinuous)}",
                "EffectStop" => $"Step {stepIndex + 1}/{total}: EffectStop",
                _            => $"Step {stepIndex + 1}/{total}: {w.CommandType}",
            };
            AppendLog("TX", detail);

            // 各 TX 後の状態マーカー（EffectStop は連続送信停止、その他は連続送信中）
            if (w.CommandType == "EffectStop")
            {
                AppendContinuousSendStopLog();
            }
            else
            {
                AppendContinuousSendStartLog();
            }
        }

        /// <summary>
        /// 現在実行中ステップの詳細ラベルと色をステータスバー表示用に更新
        /// </summary>
        private void UpdatePlayingStepDetail(int currentIndex)
        {
            if (currentIndex < 0 || currentIndex >= EditingSteps.Count)
            {
                PlayingStepLabel = null;
                PlayingStepColorHex = "#000000";
                return;
            }

            var w = EditingSteps[currentIndex];
            string detail = w.CommandType switch
            {
                "Color"      => $"Step {currentIndex + 1}/{EditingSteps.Count}: Color ({w.ColorR},{w.ColorG},{w.ColorB})",
                "Off"        => $"Step {currentIndex + 1}/{EditingSteps.Count}: Off",
                "Effect"     => $"Step {currentIndex + 1}/{EditingSteps.Count}: Effect {FormatEffectDisplayName(w.EffectType, w.Continuous)} ({w.ColorR},{w.ColorG},{w.ColorB})",
                "EffectStop" => $"Step {currentIndex + 1}/{EditingSteps.Count}: EffectStop",
                _            => $"Step {currentIndex + 1}/{EditingSteps.Count}: {w.CommandType}",
            };
            PlayingStepLabel = detail;

            // 色プレビュー：Off / EffectStop は黒、それ以外は当該行の RGB
            if (w.CommandType == "Off" || w.CommandType == "EffectStop")
            {
                PlayingStepColorHex = "#000000";
            }
            else
            {
                PlayingStepColorHex = $"#{w.ColorR:X2}{w.ColorG:X2}{w.ColorB:X2}";
            }
        }

        /// <summary>
        /// 連続再生中の行ハイライトを更新
        /// </summary>
        private void UpdatePlayingStepHighlight(int currentIndex)
        {
            for (int i = 0; i < EditingSteps.Count; i++)
            {
                EditingSteps[i].IsCurrentlyPlaying = (i == currentIndex);
            }
        }

        /// <summary>
        /// ポーリング間隔を再生状態に応じて切替（再生中=200ms / 停止中=1500ms）
        /// 概要：DispatcherTimer.Interval を変更しただけでは次回 Tick からしか反映されないため、
        ///       Stop → Interval 変更 → Start で即座に新間隔を適用する。
        /// </summary>
        private void AdjustPollingInterval(bool playing)
        {
            if (_statusTimer == null) return;
            var desired = playing ? PollIntervalPlaying : PollIntervalIdle;
            if (_statusTimer.Interval == desired) return;

            _statusTimer.Stop();
            _statusTimer.Interval = desired;
            _statusTimer.Start();
        }

        private bool HasUnsavedEdits()
        {
            if (_editingSequence == null) return false;
            if (_editingSequence.Name != EditingName.Trim()) return true;
            if ((_editingSequence.Description ?? "") != (EditingDescription ?? "")) return true;
            if (_editingSequence.Steps.Count != EditingSteps.Count) return true;
            for (int i = 0; i < EditingSteps.Count; i++)
            {
                var a = _editingSequence.Steps[i];
                var b = EditingSteps[i];
                if (a.TimeMs != b.TimeMs) return true;
                if (a.CommandType != b.CommandType) return true;
                if ((a.EffectType ?? "") != (b.EffectType ?? "")) return true;
                if (a.ColorR != b.ColorR || a.ColorG != b.ColorG || a.ColorB != b.ColorB) return true;
                if ((a.EffectCycleDurationMs ?? 0) != b.EffectCycleDurationMs) return true;
                if ((a.FadeSteps ?? 0) != b.FadeSteps) return true;
                if (a.RetransmitCount != b.RetransmitCount) return true;
                if ((a.Comment ?? "") != (b.Comment ?? "")) return true;
                if ((a.Note ?? "") != (b.Note ?? "")) return true;
                if ((a.LoopTrig ?? "") != (b.Trig ?? "")) return true;
                if (a.TransitionMs != b.TransitionMs) return true;
                if ((a.Color2R ?? 0) != b.Color2R || (a.Color2G ?? 0) != b.Color2G || (a.Color2B ?? 0) != b.Color2B) return true;
                if ((a.Bpm ?? 120) != b.Bpm) return true;

                // Rainbow 行のみ: パラメータのみ変更（Cmd 据え置き）でも未保存と判定する。
                // b.ToModel() で mode 別の条件付き null 化を a と同一に揃えてから比較（非Rainbow行の誤検知を防止）。
                if (b.CommandType == "Rainbow")
                {
                    var bm = b.ToModel();
                    if (a.RainbowMode != bm.RainbowMode) return true;
                    if ((a.RainbowCycleDurationMs ?? 0) != (bm.RainbowCycleDurationMs ?? 0)) return true;
                    if ((a.RainbowBlinkPeriodMs ?? 0) != (bm.RainbowBlinkPeriodMs ?? 0)) return true;
                    if ((a.RainbowDutyRatio ?? 0) != (bm.RainbowDutyRatio ?? 0)) return true;
                    if ((a.RainbowFadeInMs ?? 0) != (bm.RainbowFadeInMs ?? 0)) return true;
                    if ((a.RainbowFadeOutMs ?? 0) != (bm.RainbowFadeOutMs ?? 0)) return true;
                    var ac = a.RainbowColors; var bc = bm.RainbowColors;
                    if ((ac?.Count ?? 0) != (bc?.Count ?? 0)) return true;
                    if (ac != null && bc != null)
                        for (int k = 0; k < ac.Count; k++)
                            if (ac[k].R != bc[k].R || ac[k].G != bc[k].G || ac[k].B != bc[k].B) return true;
                }
            }
            return false;
        }

        private static ObservableCollection<ColorPresetItem> CreateColorPresets() => new()
        {
            new("Red",   255,   0,   0, "#FF0000"),
            new("Green",   0, 255,   0, "#00FF00"),
            new("Blue",    0,   0, 255, "#0000FF"),
            new("White", 255, 255, 255, "#FFFFFF"),
        };

        private static ObservableCollection<ActionPresetItem> CreateActionPresets() => new()
        {
            new("SetColor",    "色固定",       "#FFC107"),
            new("FadeIn",      "Fade In",      "#4CAF50"),
            new("FadeOut",     "Fade Out",     "#2196F3"),
            new("FadeInHold",  "Fade In/In",   "#81C784"),  // 1 回フェードイン → 目標色を保持
            new("FadeOutHold", "Fade Out/Out", "#64B5F6"),  // 1 回フェードアウト → 消灯を保持
            new("Flash",       "Flash",        "#FFEB3B"),
            new("Breath",      "Breath",       "#00BCD4"),
            new("SevenColor",  "7 Color",      "#E91E63"),
            new("Off",         "消灯",         "#9E9E9E"),
            // NO.38: 信号Off を動作プリセットに追加（選択行Cmd=SignalOffに上書き）
            new("SignalOff",   "信号Off",      "#FF9800"),
        };

        #endregion
    }


    public partial class ColorPresetItem : ObservableObject
    {
        public ColorPresetItem(string name, byte r, byte g, byte b, string hex)
        {
            Name = name; R = r; G = g; B = b; Hex = hex;
        }
        public string Name { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public string Hex { get; }

        [ObservableProperty]
        private bool isSelected;
    }

    public partial class ActionPresetItem : ObservableObject
    {
        public ActionPresetItem(string command, string displayName, string accentHex)
        {
            Command = command;
            DisplayName = displayName;
            AccentHex = accentHex;
        }
        public string Command { get; }
        public string DisplayName { get; }
        public string AccentHex { get; }

        [ObservableProperty]
        private bool isSelected;
    }

    /// <summary>
    /// カスタム色プリセット 1 件（Custom 1〜4）
    /// 概要：オペレータが現場で発色を調整できる中間色枠。
    ///       通常クリックで選択中行に色を反映、右クリック → 「色を編集...」で
    ///       DlgColorPicker を起動して RGB を編集できる。値は JSON で永続化。
    /// </summary>
    public partial class CustomColorPresetItem : ObservableObject
    {
        public CustomColorPresetItem(string name, byte r, byte g, byte b)
        {
            Name = name;
            this.r = r;
            this.g = g;
            this.b = b;
            hex = ToHex(r, g, b);
        }

        public string Name { get; }

        [ObservableProperty]
        private byte r;

        [ObservableProperty]
        private byte g;

        [ObservableProperty]
        private byte b;

        /// <summary>HEX 文字列（XAML の Fill バインド用、RGB 変更時に自動更新）</summary>
        [ObservableProperty]
        private string hex;

        [ObservableProperty]
        private bool isSelected;

        // R/G/B 変更時に HEX を自動更新（XAML Fill バインドに即時反映）
        partial void OnRChanged(byte value) => Hex = ToHex(value, G, B);
        partial void OnGChanged(byte value) => Hex = ToHex(R, value, B);
        partial void OnBChanged(byte value) => Hex = ToHex(R, G, value);

        private static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// 煽りボタン用プリセット 1 件（煽り 1, 2）
    /// 概要：押下中だけ LED を上書きする即興演出用。右クリックで色編集可能、JSON で永続化。
    /// </summary>
    public partial class AggressiveColorPresetItem : ObservableObject
    {
        public AggressiveColorPresetItem(string name, byte r, byte g, byte b)
        {
            Name = name;
            this.r = r;
            this.g = g;
            this.b = b;
            hex = ToHex(r, g, b);
        }

        public string Name { get; }

        [ObservableProperty]
        private byte r;

        [ObservableProperty]
        private byte g;

        [ObservableProperty]
        private byte b;

        [ObservableProperty]
        private string hex;

        partial void OnRChanged(byte value) => Hex = ToHex(value, G, B);
        partial void OnGChanged(byte value) => Hex = ToHex(R, value, B);
        partial void OnBChanged(byte value) => Hex = ToHex(R, G, value);

        private static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// 送信ログのエントリ
    /// 概要：時刻 + 種別タグ（TX/RX/INFO/ERR）+ メッセージで構成。
    ///       UI では Display を 1 行ずつ ListBox で表示し、Color で種別を色分けする。
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;

        /// <summary>"TX" / "RX" / "INFO" / "ERR"</summary>
        public string Type { get; init; } = "INFO";

        public string Message { get; init; } = "";

        /// <summary>UI 表示用の整形済みテキスト（HH:mm:ss [TYPE] message）</summary>
        public string Display => $"{Timestamp:HH:mm:ss} [{Type}] {Message}";

        /// <summary>種別ごとの表示色（Foreground にバインド）</summary>
        public string Color => Type switch
        {
            "TX" => "#7CFF7C",
            "RX" => "#80C0FF",
            "ERR" => "#FF5252",
            _ => "#FFC107",
        };
    }

    /// <summary>
    /// A1 内蔵プログラムプリセット 1 件分（CommandPanelView のボタンにバインド）
    /// </summary>
    public partial class InternalProgramPresetItem : ObservableObject
    {
        public InternalProgramPresetItem(string name, uint frameNo, bool enabled)
        {
            this.name = name;
            this.frameNo = frameNo;
            this.enabled = enabled;
        }

        [ObservableProperty]
        private string name;

        [ObservableProperty]
        private uint frameNo;

        [ObservableProperty]
        private bool enabled;
    }

    /// <summary>BLEデバイスアイテム（ComboBox バインド用）</summary>
    public class BleDeviceItem
    {
        public BleDeviceItem(string address, string name, int rssi)
        {
            Address = address;
            Name = name;
            Rssi = rssi;
        }

        public string Address { get; }
        public string Name { get; }
        public int Rssi { get; }
        public string Display => $"{Name} ({Address}) [{Rssi}dBm]";
    }

    /// <summary>
    /// レインボーカラーパレット用の RGB 色アイテム（MVVM バインド対応）
    /// </summary>
    public partial class RgbColorItem : ObservableObject
    {
        public RgbColorItem(byte r, byte g, byte b)
        {
            this.r = r;
            this.g = g;
            this.b = b;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HexColor))]
        private byte r;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HexColor))]
        private byte g;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HexColor))]
        private byte b;

        /// <summary>WPF バインド用: "#RRGGBB" 形式の文字列</summary>
        public string HexColor => $"#{R:X2}{G:X2}{B:X2}";
    }
}
