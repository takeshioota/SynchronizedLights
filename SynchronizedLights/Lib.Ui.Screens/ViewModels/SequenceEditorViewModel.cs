using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;
using Lib.Application.Models;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing.Imaging;
using System.Linq;
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
        private readonly ILightingFacade? _lighting;
        private readonly DispatcherTimer? _statusTimer;
        private TimeBasedSequence? _editingSequence;

        /// <summary>
        /// 選択行変更時の自動実行を一時的に抑制するフラグ
        /// 概要：シーケンス切替・空行追加・行削除・一覧再読込などプログラムから
        ///       選択を変更する場合は本フラグを立てて、不要な自動実行を防ぐ。
        /// </summary>
        private bool _suppressAutoExecute;

        #endregion

        #region コンストラクタ

        public SequenceEditorViewModel(ILightingFacade lighting)
        {
            _store = new TimeBasedSequenceStore();
            _lighting = lighting;

            Sequences = new ObservableCollection<TimeBasedSequence>();
            EditingSteps = new ObservableCollection<SequenceStepWrapper>();
            ColorPresets = CreateColorPresets();
            ActionPresets = CreateActionPresets();
            SelectedColorPreset = ColorPresets.First();
            SelectedActionPreset = ActionPresets.First();

            ReloadSequences();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _statusTimer.Tick += async (_, _) => await PollStatusAsync();
            _statusTimer.Start();
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

        [ObservableProperty]
        private string editingDescription = "";

        public ObservableCollection<SequenceStepWrapper> EditingSteps { get; }

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
        private void AppendLog(string type, string message)
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

        partial void OnSelectedSequenceChanged(TimeBasedSequence? value)
        {
            // シーケンス切替時は全行を入れ替えるため、自動実行を抑制
            _suppressAutoExecute = true;
            try
            {
                if (value == null)
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

                _editingSequence = value;
                EditingName = value.Name;
                EditingDescription = value.Description;
                EditingSteps.Clear();
                foreach (var step in value.SortedSteps)
                {
                    EditingSteps.Add(new SequenceStepWrapper(step));
                }
                CurrentStepIndex = EditingSteps.Count > 0 ? 0 : -1;
                SelectedStep = CurrentStepIndex >= 0 ? EditingSteps[CurrentStepIndex] : null;
                OnPropertyChanged(nameof(CurrentStepLabel));
                StatusMessage = $"編集中: {value.Name}（{EditingSteps.Count} ステップ）";
                AppendLog("INFO", $"編集中: {value.Name} ({EditingSteps.Count} ステップ)");
            }
            finally
            {
                _suppressAutoExecute = false;
            }
        }

        /// <summary>
        /// 選択行変更時の自動実行（クリック・矢印キー・Enter での移動で発火）
        /// 概要：ユーザー操作で SelectedStep が変わった瞬間に、その行の動作を即時実行する。
        ///       プログラムから選択を変更する場合は <see cref="_suppressAutoExecute"/> で抑制される。
        /// </summary>
        partial void OnSelectedStepChanged(SequenceStepWrapper? value)
        {
            if (_suppressAutoExecute) return;
            if (value == null) return;
            if (_lighting == null || !_lighting.IsConnected) return;

            // CurrentStepLabel を更新（インデックスがバインド経由で同期するまでに少しズレる場合があるため）
            OnPropertyChanged(nameof(CurrentStepLabel));

            // 即時実行（fire-and-forget、内部で例外は捕捉される）
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
            if (string.IsNullOrWhiteSpace(EditingName)) { StatusMessage = "シーケンス名を入力してください。"; return; }
            if (_store.ExistsByName(EditingName, _editingSequence.Id))
            {
                StatusMessage = $"「{EditingName}」という名前のシーケンスが既に存在します。";
                return;
            }

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
            var idx = EditingSteps.IndexOf(SelectedStep);

            // 削除に伴う選択遷移は誤操作防止のため自動実行を抑制
            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Remove(SelectedStep);
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

            var src = SelectedStep.ToModel();
            var newStep = new SequenceStep
            {
                TimeMs = src.TimeMs + 1000,
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
            EditingSteps.Insert(insertIndex, wrapper);

            // 複製は内容コピーであり実行はしないため自動実行を抑制
            _suppressAutoExecute = true;
            try
            {
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

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Move(idx, idx - 1);
                CurrentStepIndex = idx - 1;
            }
            finally
            {
                _suppressAutoExecute = false;
            }
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"行を上に移動：{idx + 1} → {idx}";
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

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Move(idx, idx + 1);
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

            _suppressAutoExecute = true;
            try
            {
                var sorted = EditingSteps.OrderBy(w => w.TimeMs).ToList();
                EditingSteps.Clear();
                foreach (var w in sorted) EditingSteps.Add(w);

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

            _suppressAutoExecute = true;
            try
            {
                EditingSteps.Clear();
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
        /// 既存ステップ列の末尾にテンプレート由来のステップ群を追加する共通ヘルパー
        /// 概要：直前ステップの TimeMs に intervalMs を加算した値を 1 件目の開始時刻とし、
        ///       以降は intervalMs ずつ加算していく。
        ///       追加された 1 件目を選択状態にする（自動実行は抑制）。
        /// </summary>
        private void AppendStepsToTail(IReadOnlyList<SequenceStep> steps, int intervalMs)
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }
            if (steps == null || steps.Count == 0) return;

            int interval = Math.Max(0, intervalMs);
            int baseTimeMs = EditingSteps.Count > 0
                ? EditingSteps.Last().TimeMs + (interval > 0 ? interval : 1000)
                : 0;
            int firstIndex = EditingSteps.Count;

            _suppressAutoExecute = true;
            try
            {
                foreach (var step in steps)
                {
                    step.TimeMs = baseTimeMs;
                    if (step.RetransmitCount <= 0) step.RetransmitCount = 3;
                    EditingSteps.Add(new SequenceStepWrapper(step));
                    baseTimeMs += interval;
                }

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
            AppendStepsToTail(new[] { step }, EasyIntervalMs);
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
            AppendStepsToTail(new[] { step }, EasyIntervalMs);
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

            AppendStepsToTail(steps, intervalMs: 1000);
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
            AppendStepsToTail(new[] { step }, intervalMs: 0);
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
            AppendStepsToTail(new[] { step }, intervalMs: 0);
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
            AppendStepsToTail(new[] { step }, intervalMs: 0);
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
            AppendStepsToTail(steps, intervalMs: 1000);
            StatusMessage = "テンプレ：カウントダウン（5 → 0、1 秒間隔）を追加しました。";
        }

        #endregion

        #region コマンド：プリセット選択

        /// <summary>
        /// 色プリセットボタン押下：選択中の行に色を反映する（CommandType は変更しない）
        /// </summary>
        [RelayCommand]
        private void SelectColorPreset(ColorPresetItem? item)
        {
            if (item == null) return;
            SelectedColorPreset = item;
            foreach (var p in ColorPresets) p.IsSelected = p == item;

            if (SelectedStep == null)
            {
                StatusMessage = "色を反映する行を先に選択してください。";
                return;
            }

            SelectedStep.ColorR = item.R;
            SelectedStep.ColorG = item.G;
            SelectedStep.ColorB = item.B;
            StatusMessage = $"色を反映：{item.Name}（{item.R}, {item.G}, {item.B}）";
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
                StatusMessage = "動作を反映する行を先に選択してください。";
                return;
            }

            var (cmdType, effType) = MapActionToCommand(item.Command);
            SelectedStep.CommandType = cmdType;
            SelectedStep.EffectType = effType ?? "";

            // Effect の場合は周期・Fade のデフォルト値を補完
            if (cmdType == "Effect")
            {
                if (SelectedStep.EffectCycleDurationMs <= 0)
                {
                    SelectedStep.EffectCycleDurationMs = 1000;
                }
                if (SelectedStep.FadeSteps <= 0)
                {
                    SelectedStep.FadeSteps = 20;
                }
            }

            // 動作確定の手応えとして、当該行を即時実行（接続中のみ）
            if (_lighting != null && _lighting.IsConnected)
            {
                _ = ExecuteStepWithoutAdvanceAsync();
            }

            StatusMessage = $"動作を確定：{item.DisplayName} → 次の空行を自動挿入";

            // 連続入力のため次の空行を自動挿入する（AddEmptyStep 内で抑制される）
            AddEmptyStep();
        }

        /// <summary>
        /// プリセットボタンの動作名を内部モデル（CommandType + EffectType）に変換
        /// </summary>
        private static (string CommandType, string? EffectType) MapActionToCommand(string? presetCommand)
        {
            return presetCommand switch
            {
                "SetColor" => ("Color", null),
                "Off" => ("Off", null),
                "Flash" => ("Effect", "Flash"),
                "FadeIn" => ("Effect", "FadeIn"),
                "FadeOut" => ("Effect", "FadeOut"),
                "Breath" => ("Effect", "Breathing"),
                "SevenColor" => ("Effect", "SevenColor"),
                _ => ("Color", null),
            };
        }

        /// <summary>
        /// 選択中の行の直後に空行を 1 行挿入する
        /// 概要：時刻は直前行 + 1000ms、コマンドは Color、色は白、エフェクト関連は未指定。
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

            int insertIndex;
            int newTimeMs;
            if (SelectedStep != null && EditingSteps.Contains(SelectedStep))
            {
                insertIndex = EditingSteps.IndexOf(SelectedStep) + 1;
                newTimeMs = SelectedStep.TimeMs + 1000;
            }
            else if (EditingSteps.Count > 0)
            {
                insertIndex = EditingSteps.Count;
                newTimeMs = EditingSteps.Last().TimeMs + 1000;
            }
            else
            {
                insertIndex = 0;
                newTimeMs = 0;
            }

            var step = new SequenceStep
            {
                TimeMs = newTimeMs,
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
            EditingSteps.Insert(insertIndex, wrapper);

            // 空行は実行する意味がないため、自動実行を抑制
            _suppressAutoExecute = true;
            try
            {
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
        /// 現在ステップを実行 + 次ステップへ移動（Enter / Space キー）
        /// 概要：本コマンドは選択行の移動のみを行う。
        ///       実行は <see cref="OnSelectedStepChanged"/> による自動実行に任せる。
        /// </summary>
        [RelayCommand]
        private void ExecuteCurrentStep()
        {
            if (EditingSteps.Count == 0)
            {
                StatusMessage = "実行するステップがありません。";
                return;
            }

            // 未選択時は先頭行を選択（OnSelectedStepChanged が自動実行する）
            if (CurrentStepIndex < 0)
            {
                CurrentStepIndex = 0;
                SelectedStep = EditingSteps[0];
                return;
            }

            // 末尾の場合は移動しない
            if (CurrentStepIndex >= EditingSteps.Count - 1)
            {
                StatusMessage = "最後のステップです。";
                return;
            }

            // 次行へ移動（OnSelectedStepChanged が自動実行する）
            var newIndex = CurrentStepIndex + 1;
            CurrentStepIndex = newIndex;
            SelectedStep = EditingSteps[newIndex];
            StatusMessage = $"実行: ステップ {newIndex + 1} / {EditingSteps.Count}";
        }

        /// <summary>
        /// 前ステップへ移動（← / ↑ キー）
        /// </summary>
        [RelayCommand]
        private void PreviousStep()
        {
            if (EditingSteps.Count == 0) return;
            if (CurrentStepIndex <= 0)
            {
                StatusMessage = "最初のステップです。";
                return;
            }

            var newIndex = CurrentStepIndex - 1;
            CurrentStepIndex = newIndex;
            SelectedStep = EditingSteps[newIndex];
            StatusMessage = $"前ステップ：{newIndex + 1} / {EditingSteps.Count}";
        }

        /// <summary>次ステップへ移動（→ / ↓ キー）</summary>
        [RelayCommand]
        private void NextStep()
        {
            if (EditingSteps.Count == 0) return;
            if (CurrentStepIndex >= EditingSteps.Count - 1)
            {
                StatusMessage = "最後のステップです。";
                return;
            }

            var newIndex = CurrentStepIndex + 1;
            CurrentStepIndex = newIndex;
            SelectedStep = EditingSteps[newIndex];
            StatusMessage = $"次ステップへ移動：{newIndex + 1} / {EditingSteps.Count}";
        }

        /// <summary>実行中エフェクトを停止（Esc キー）</summary>
        [RelayCommand]
        private async Task StopExecutionAsync()
        {
            if (_lighting == null) return;
            try
            {
                await _lighting.StopEffectAsync();
                StatusMessage = "停止しました。";
                AppendLog("TX", "EffectStop (Esc)");
            }
            catch (Exception ex)
            {
                StatusMessage = $"停止失敗: {ex.Message}";
                AppendLog("ERR", $"EffectStop 失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 現在ステップを実行する（共通ロジック、advance なし）
        /// 概要：continuous=true で実行 → 次の Enter で上書き、Stop 不要
        /// </summary>
        private async Task ExecuteStepWithoutAdvanceAsync()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            // SelectedStep を直接参照することで、CurrentStepIndex のバインド同期タイミングに左右されない
            var wrapper = SelectedStep;
            if (wrapper == null) return;

            var step = wrapper.ToModel();
            var color = new Rgb(step.ColorR, step.ColorG, step.ColorB);
            var target = Target.All;

            try
            {
                switch (step.CommandType)
                {
                    case "Color":
                        await _lighting.SetColorAsync(target, color);
                        AppendLog("TX", $"Color ({step.ColorR},{step.ColorG},{step.ColorB})");
                        break;

                    case "Off":
                        await _lighting.SetColorAsync(target, new Rgb(0, 0, 0));
                        AppendLog("TX", "Off");
                        break;

                    case "Effect":
                        if (string.IsNullOrEmpty(step.EffectType))
                        {
                            StatusMessage = "エフェクト種別が未設定です。";
                            return;
                        }
                        await _lighting.StartEffectAsync(
                            effectType: step.EffectType,
                            color: color,
                            cycleDurationMs: step.GetEffectCycleDurationOrDefault(),
                            flashIntervalMs: step.EffectType == "Flash"
                                ? Math.Max(50, step.GetEffectCycleDurationOrDefault() / 2)
                                : (int?)null,
                            fadeSteps: step.GetFadeStepsOrDefault(),
                            continuous: true);
                        AppendLog("TX", $"Effect {step.EffectType} ({step.ColorR},{step.ColorG},{step.ColorB}) cycle={step.GetEffectCycleDurationOrDefault()}ms fade={step.GetFadeStepsOrDefault()}");
                        break;

                    case "EffectStop":
                        await _lighting.StopEffectAsync();
                        AppendLog("TX", "EffectStop");
                        break;

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
        }

        #endregion

        #region コマンド：API 一括再生

        [RelayCommand]
        private async Task PlayOrStopAsync()
        {
            if (_lighting == null) { StatusMessage = "API 連携が無効のため再生できません。"; return; }

            if (IsPlaying)
            {
                try
                {
                    await _lighting.StopSequenceAsync();
                    StatusMessage = "シーケンスを停止しました。";
                    AppendLog("TX", "Sequence Stop");
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

                // 再生中は高頻度、停止中は通常頻度に切替
                AdjustPollingInterval(playing);
            }
            catch
            {
                IsPlaying = false;
                PlayingSequenceName = null;
                UpdatePlayingStepHighlight(-1);
                AdjustPollingInterval(false);
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
        /// </summary>
        private void AdjustPollingInterval(bool playing)
        {
            if (_statusTimer == null) return;
            var desired = playing ? PollIntervalPlaying : PollIntervalIdle;
            if (_statusTimer.Interval != desired)
            {
                _statusTimer.Interval = desired;
            }
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
            }
            return false;
        }

        private static ObservableCollection<ColorPresetItem> CreateColorPresets() => new()
        {
            new("Red",     255,   0,   0, "#FF0000"),
            new("Orange",  255, 128,   0, "#FF8000"),
            new("Yellow",  255, 255,   0, "#FFFF00"),
            new("Lime",    128, 255,   0, "#80FF00"),
            new("Green",     0, 255,   0, "#00FF00"),
            new("Cyan",      0, 255, 255, "#00FFFF"),
            new("Sky",       0, 128, 255, "#0080FF"),
            new("Blue",      0,   0, 255, "#0000FF"),
            new("Purple",  128,   0, 255, "#8000FF"),
            new("Magenta", 255,   0, 255, "#FF00FF"),
            new("Pink",    255, 128, 192, "#FF80C0"),
            new("White",   255, 255, 255, "#FFFFFF"),
        };

        private static ObservableCollection<ActionPresetItem> CreateActionPresets() => new()
        {
            new("SetColor",   "色固定",     "#FFC107"),
            new("FadeIn",     "Fade In",    "#4CAF50"),
            new("FadeOut",    "Fade Out",   "#2196F3"),
            new("Flash",      "Flash",      "#FFEB3B"),
            new("Breath",     "Breath",     "#00BCD4"),
            new("SevenColor", "7 Color",    "#E91E63"),
            new("Off",        "消灯",       "#9E9E9E"),
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
}
