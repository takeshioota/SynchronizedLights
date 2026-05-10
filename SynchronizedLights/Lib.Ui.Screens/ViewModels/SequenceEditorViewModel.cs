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

        [ObservableProperty]
        private double newStepTimeSec = 0.0;

        [ObservableProperty]
        private double newStepDurationSec = 1.0;

        [ObservableProperty]
        private string newStepNote = "";

        #endregion

        #region プロパティ：API 連携

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayButtonText))]
        private bool isPlaying;

        [ObservableProperty]
        private string? playingSequenceName;

        public string PlayButtonText => IsPlaying ? "■ 停止" : "▶ 一括再生";

        #endregion

        #region SelectedSequence 変更時

        partial void OnSelectedSequenceChanged(TimeBasedSequence? value)
        {
            if (value == null)
            {
                EditingName = "";
                EditingDescription = "";
                EditingSteps.Clear();
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
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"編集中: {value.Name}（{EditingSteps.Count} ステップ）";
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
            }
            else
            {
                StatusMessage = "保存に失敗しました。ログを確認してください。";
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
                ReloadSequences();
                SelectedSequence = null;
            }
            else
            {
                StatusMessage = $"削除に失敗しました: {name}";
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
            EditingSteps.Remove(SelectedStep);
            if (CurrentStepIndex >= EditingSteps.Count) CurrentStepIndex = EditingSteps.Count - 1;
            // 削除位置の次の行を新たに選択
            if (idx < EditingSteps.Count) SelectedStep = EditingSteps[idx];
            else if (EditingSteps.Count > 0) SelectedStep = EditingSteps.Last();
            else SelectedStep = null;
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = "ステップを削除しました。";
        }

        #endregion

        #region コマンド：プリセット選択

        [RelayCommand]
        private void SelectColorPreset(ColorPresetItem? item)
        {
            if (item == null) return;
            SelectedColorPreset = item;
            foreach (var p in ColorPresets) p.IsSelected = p == item;
        }

        [RelayCommand]
        private void SelectActionPreset(ActionPresetItem? item)
        {
            if (item == null) return;
            SelectedActionPreset = item;
            foreach (var p in ActionPresets) p.IsSelected = p == item;
        }

        /// <summary>
        /// プリセットの内容で新規ステップを追加（v2.1：選択行の直後に挿入）
        /// </summary>
        [RelayCommand]
        private void AddStepFromPreset()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }
            if (SelectedColorPreset == null || SelectedActionPreset == null)
            {
                StatusMessage = "色と動作を選択してください。";
                return;
            }

            // ① 旧プリセット名を新モデル（CommandType + EffectType）にマップ
            string commandType = "Color";
            string? effectType = null;
            switch (SelectedActionPreset.Command)
            {
                case "SetColor": commandType = "Color"; effectType = null; break;
                case "Off": commandType = "Off"; effectType = null; break;
                case "Flash": commandType = "Effect"; effectType = "Flash"; break;
                case "FadeIn": commandType = "Effect"; effectType = "FadeIn"; break;
                case "FadeOut": commandType = "Effect"; effectType = "FadeOut"; break;
                case "Breath": commandType = "Effect"; effectType = "Breathing"; break;
                case "SevenColor": commandType = "Effect"; effectType = "SevenColor"; break;
                default: commandType = "Color"; effectType = null; break;
            }

            // ② 所要時間（ms）を計算
            int durationMsLocal = (int)Math.Max(0, Math.Round(NewStepDurationSec * 1000));

            // ③ Effect 時のみ EffectCycleDurationMs / FadeSteps を設定（型は明示的に int? でキャスト）
            int? effectCycle = (commandType == "Effect" && durationMsLocal > 0)
                ? (int?)durationMsLocal
                : null;
            int? fadeStepsLocal = (commandType == "Effect" && durationMsLocal > 0)
                ? (int?)Math.Max(1, durationMsLocal / 50)
                : null;

            var step = new SequenceStep
            {
                TimeMs = (int)Math.Max(0, Math.Round(NewStepTimeSec * 1000)),
                CommandType = commandType,
                EffectType = effectType,
                ColorR = SelectedColorPreset.R,
                ColorG = SelectedColorPreset.G,
                ColorB = SelectedColorPreset.B,
                EffectCycleDurationMs = effectCycle,
                FadeSteps = fadeStepsLocal,
                RetransmitCount = 3,
                Note = NewStepNote ?? ""
            };
            var wrapper = new SequenceStepWrapper(step);

            // 選択中の行があればその直後に挿入、なければ末尾に追加
            int insertIndex;
            if (SelectedStep != null && EditingSteps.Contains(SelectedStep))
            {
                insertIndex = EditingSteps.IndexOf(SelectedStep) + 1;
            }
            else
            {
                insertIndex = EditingSteps.Count;
            }
            EditingSteps.Insert(insertIndex, wrapper);

            // 追加された行を選択状態に
            SelectedStep = wrapper;
            CurrentStepIndex = insertIndex;

            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"追加：{SelectedActionPreset.DisplayName} / {SelectedColorPreset.Name}（位置 {insertIndex + 1}）";

            // 次回追加用に時刻と所要時間を進める（連続追加時の利便性）
            NewStepTimeSec = (step.TimeMs + (step.EffectCycleDurationMs ?? 0)) / 1000.0;
            NewStepNote = "";
        }

        /// <summary>
        /// 旧 UI プリセット（ActionPreset.Command 文字列）を
        /// 新モデル（CommandType + EffectType）にマップする。
        /// 旧："SetColor" / "Off" / "Flash" / "FadeIn" / "FadeOut" / "Breath" / "SevenColor"
        /// </summary>
        private static (string CommandType, string? EffectType) MapPresetActionToNewModel(string? legacyCommand)
        {
            return legacyCommand switch
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

        #endregion

        #region コマンド：ステップ実行（v2.1 新仕様）

        /// <summary>
        /// 現在ステップを実行 → 次ステップへ自動移動（Enter キー）
        /// </summary>
        [RelayCommand]
        private async Task ExecuteCurrentStepAsync()
        {
            if (CurrentStepIndex < 0 || CurrentStepIndex >= EditingSteps.Count)
            {
                StatusMessage = "実行するステップがありません。";
                return;
            }

            var stepIndex = CurrentStepIndex;
            await ExecuteStepWithoutAdvanceAsync();

            StatusMessage = $"実行: ステップ {stepIndex + 1} → 次へ移動";

            // 実行後、自動的に次ステップへ移動
            if (CurrentStepIndex < EditingSteps.Count - 1)
            {
                CurrentStepIndex++;
                if (CurrentStepIndex < EditingSteps.Count)
                {
                    SelectedStep = EditingSteps[CurrentStepIndex];
                }
            }
        }

        /// <summary>
        /// 前ステップに移動 + 実行（← キー、v2.1 新仕様）
        /// </summary>
        [RelayCommand]
        private async Task PreviousStepAsync()
        {
            if (EditingSteps.Count == 0) return;
            if (CurrentStepIndex <= 0)
            {
                StatusMessage = "最初のステップです。";
                return;
            }

            // 1 つ前へ戻る
            CurrentStepIndex--;
            if (CurrentStepIndex < EditingSteps.Count)
            {
                SelectedStep = EditingSteps[CurrentStepIndex];
            }
            StatusMessage = $"前ステップ + 実行：{CurrentStepIndex + 1}/{EditingSteps.Count}";

            // 戻った先の行を実行（advance はしない、その行に留まる）
            await ExecuteStepWithoutAdvanceAsync();
        }

        /// <summary>次ステップに移動（実行はしない、→ キー）</summary>
        [RelayCommand]
        private void NextStep()
        {
            if (EditingSteps.Count == 0) return;
            if (CurrentStepIndex < EditingSteps.Count - 1)
            {
                CurrentStepIndex++;
                if (CurrentStepIndex < EditingSteps.Count)
                {
                    SelectedStep = EditingSteps[CurrentStepIndex];
                }
                StatusMessage = $"次ステップへ移動：{CurrentStepIndex + 1}/{EditingSteps.Count}";
            }
            else
            {
                StatusMessage = "最後のステップです。";
            }
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
            }
            catch (Exception ex)
            {
                StatusMessage = $"停止失敗: {ex.Message}";
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
            if (CurrentStepIndex < 0 || CurrentStepIndex >= EditingSteps.Count) return;

            var wrapper = EditingSteps[CurrentStepIndex];
            var step = wrapper.ToModel();
            var color = new Rgb(step.ColorR, step.ColorG, step.ColorB);
            var target = Target.All;

            try
            {
                switch (step.CommandType)
                {
                    case "Color":
                        await _lighting.SetColorAsync(target, color);
                        break;

                    case "Off":
                        await _lighting.SetColorAsync(target, new Rgb(0, 0, 0));
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
                        break;

                    case "EffectStop":
                        await _lighting.StopEffectAsync();
                        break;

                    default:
                        StatusMessage = $"未対応コマンド: {step.CommandType}";
                        return;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"実行失敗: {ex.Message}";
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
                try { await _lighting.StopSequenceAsync(); StatusMessage = "シーケンスを停止しました。"; }
                catch (Exception ex) { StatusMessage = $"停止失敗: {ex.Message}"; }
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
            }
            catch (Exception ex)
            {
                StatusMessage = $"再生失敗: {ex.Message}";
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
            }
            catch (Exception ex)
            {
                StatusMessage = $"同期失敗: {ex.Message}";
                Log.Warning(ex, "SyncToApi failed");
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

        private async Task PollStatusAsync()
        {
            if (_lighting == null) return;
            try
            {
                var (playing, name) = await _lighting.GetSequencePlayStatusAsync();
                IsPlaying = playing;
                PlayingSequenceName = name;
            }
            catch
            {
                IsPlaying = false;
                PlayingSequenceName = null;
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
}
