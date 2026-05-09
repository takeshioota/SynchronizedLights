using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;
using Lib.Application.Models;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Serilog;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// シーケンス編集ウィンドウ用 ViewModel
    /// 概要：時刻ベースシーケンスの編集・保存・再生・ステップ実行を提供する。
    ///       - 上半分：シーケンスステップ DataGrid + ステップ実行（コンサート進行向け）
    ///       - 下半分：プリセット選択パネル（12 色 + 動作）
    ///       - キーボードショートカット対応
    ///
    /// 使い方：
    ///   var vm = new SequenceEditorViewModel(lighting);
    ///   var window = new SequenceEditorWindow { DataContext = vm };
    ///   window.Show();
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

        /// <summary>シーケンス編集 ViewModel を生成する</summary>
        /// <param name="lighting">ILightingFacade（API 連携用、null 不可）</param>
        public SequenceEditorViewModel(ILightingFacade lighting)
        {
            _store = new TimeBasedSequenceStore();
            _lighting = lighting;

            Sequences = new ObservableCollection<TimeBasedSequence>();
            EditingSteps = new ObservableCollection<SequenceStepWrapper>();
            ColorPresets = CreateColorPresets();
            ActionPresets = CreateActionPresets();
            SelectedColorPreset = ColorPresets.First();   // Red を初期選択
            SelectedActionPreset = ActionPresets.First(); // SetColor を初期選択

            ReloadSequences();

            // 1.5 秒ごとに API の再生状態をポーリング
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _statusTimer.Tick += async (_, _) => await PollStatusAsync();
            _statusTimer.Start();
        }

        #endregion

        #region プロパティ：シーケンス管理（既存 TimeSequenceViewModel から継承）

        /// <summary>シーケンス一覧</summary>
        public ObservableCollection<TimeBasedSequence> Sequences { get; }

        /// <summary>選択中シーケンス</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedSequence))]
        private TimeBasedSequence? selectedSequence;

        /// <summary>選択中があるかどうか</summary>
        public bool HasSelectedSequence => SelectedSequence != null;

        /// <summary>編集中シーケンス名</summary>
        [ObservableProperty]
        private string editingName = "";

        /// <summary>編集中シーケンスの説明</summary>
        [ObservableProperty]
        private string editingDescription = "";

        /// <summary>編集中ステップ一覧</summary>
        public ObservableCollection<SequenceStepWrapper> EditingSteps { get; }

        /// <summary>選択中ステップ（行削除用）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
        private SequenceStepWrapper? selectedStep;

        /// <summary>状態メッセージ</summary>
        [ObservableProperty]
        private string statusMessage = "シーケンスを選択するか、「新規」で作成してください。";

        #endregion

        #region プロパティ：ステップ実行モード（v2.1 新規）

        /// <summary>現在ステップのインデックス（0 始まり、-1 = 未選択）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
        private int currentStepIndex = -1;

        /// <summary>現在ステップの位置表示（"3 / 10" など）</summary>
        public string CurrentStepLabel
            => EditingSteps.Count == 0
                ? "ステップなし"
                : $"現在ステップ: {(CurrentStepIndex < 0 ? 0 : CurrentStepIndex + 1)} / {EditingSteps.Count}";

        #endregion

        #region プロパティ：プリセット選択（v2.1 新規）

        /// <summary>色プリセット一覧</summary>
        public ObservableCollection<ColorPresetItem> ColorPresets { get; }

        /// <summary>動作プリセット一覧</summary>
        public ObservableCollection<ActionPresetItem> ActionPresets { get; }

        /// <summary>選択中の色プリセット</summary>
        [ObservableProperty]
        private ColorPresetItem? selectedColorPreset;

        /// <summary>選択中の動作プリセット</summary>
        [ObservableProperty]
        private ActionPresetItem? selectedActionPreset;

        /// <summary>新規ステップ用：時刻（秒）</summary>
        [ObservableProperty]
        private double newStepTimeSec = 0.0;

        /// <summary>新規ステップ用：所要時間（秒）</summary>
        [ObservableProperty]
        private double newStepDurationSec = 1.0;

        /// <summary>新規ステップ用：メモ</summary>
        [ObservableProperty]
        private string newStepNote = "";

        #endregion

        #region プロパティ：API 連携（既存 TimeSequenceViewModel から継承）

        /// <summary>シーケンス再生中かどうか</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayButtonText))]
        private bool isPlaying;

        /// <summary>再生中シーケンス名</summary>
        [ObservableProperty]
        private string? playingSequenceName;

        /// <summary>再生ボタン表示テキスト</summary>
        public string PlayButtonText => IsPlaying ? "■ 停止" : "▶ 一括再生";

        #endregion

        #region SelectedSequence 変更時：編集ペインに反映

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

        /// <summary>新規シーケンス作成</summary>
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

        /// <summary>選択中シーケンスを保存</summary>
        [RelayCommand]
        private void SaveSequence()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "保存対象が選択されていません。";
                return;
            }
            if (string.IsNullOrWhiteSpace(EditingName))
            {
                StatusMessage = "シーケンス名を入力してください。";
                return;
            }
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

        /// <summary>選択中シーケンスを削除</summary>
        [RelayCommand]
        private void DeleteSequence()
        {
            if (SelectedSequence == null)
            {
                StatusMessage = "削除対象が選択されていません。";
                return;
            }

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

        /// <summary>編集破棄（最後の保存状態に戻す）</summary>
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

        /// <summary>ステップ追加（末尾に追加、デフォルト値）</summary>
        [RelayCommand]
        private void AddStep()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }

            var lastTimeMs = EditingSteps.Count > 0
                ? EditingSteps.Max(s => s.TimeMs)
                : -1000;

            var step = new SequenceStep
            {
                TimeMs = lastTimeMs + 1000,
                Command = "SetColor",
                ColorR = 255,
                ColorG = 255,
                ColorB = 255,
                DurationMs = 1000,
                RetransmitCount = 3,
                InterpolationIntervalMs = 50,
                Note = ""
            };
            EditingSteps.Add(new SequenceStepWrapper(step));
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"ステップ追加（時刻 {step.TimeMs / 1000.0:F1}s）。";
        }

        /// <summary>選択中ステップを削除</summary>
        [RelayCommand]
        private void RemoveStep()
        {
            if (SelectedStep == null)
            {
                StatusMessage = "削除する行を選択してください。";
                return;
            }
            EditingSteps.Remove(SelectedStep);
            if (CurrentStepIndex >= EditingSteps.Count) CurrentStepIndex = EditingSteps.Count - 1;
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = "ステップを削除しました。";
        }

        /// <summary>ステップを時刻順にソート</summary>
        [RelayCommand]
        private void SortSteps()
        {
            var sorted = EditingSteps.OrderBy(s => s.TimeMs).ToList();
            EditingSteps.Clear();
            foreach (var s in sorted) EditingSteps.Add(s);
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"時刻順にソートしました（{EditingSteps.Count} ステップ）。";
        }

        #endregion

        #region コマンド：プリセット選択（v2.1 新規）

        /// <summary>色プリセット選択</summary>
        [RelayCommand]
        private void SelectColorPreset(ColorPresetItem? item)
        {
            if (item == null) return;
            SelectedColorPreset = item;
            // 排他選択（IsSelected を更新）
            foreach (var p in ColorPresets) p.IsSelected = p == item;
        }

        /// <summary>動作プリセット選択</summary>
        [RelayCommand]
        private void SelectActionPreset(ActionPresetItem? item)
        {
            if (item == null) return;
            SelectedActionPreset = item;
            foreach (var p in ActionPresets) p.IsSelected = p == item;
        }

        /// <summary>プリセットの内容で新規ステップを追加</summary>
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

            var step = new SequenceStep
            {
                TimeMs = (int)Math.Max(0, Math.Round(NewStepTimeSec * 1000)),
                Command = SelectedActionPreset.Command,
                ColorR = SelectedColorPreset.R,
                ColorG = SelectedColorPreset.G,
                ColorB = SelectedColorPreset.B,
                DurationMs = (int)Math.Max(0, Math.Round(NewStepDurationSec * 1000)),
                RetransmitCount = 3,
                InterpolationIntervalMs = 50,
                Note = NewStepNote ?? ""
            };
            EditingSteps.Add(new SequenceStepWrapper(step));
            OnPropertyChanged(nameof(CurrentStepLabel));
            StatusMessage = $"プリセット追加：{SelectedActionPreset.DisplayName} / {SelectedColorPreset.Name}";

            // 次回追加用に時刻と所要時間を進める
            NewStepTimeSec = step.TimeMs / 1000.0 + step.DurationMs / 1000.0;
            NewStepNote = "";
        }

        #endregion

        #region コマンド：ステップ実行（v2.1 新規）

        /// <summary>現在ステップを実行</summary>
        [RelayCommand]
        private async Task ExecuteCurrentStepAsync()
        {
            if (_lighting == null || !_lighting.IsConnected)
            {
                StatusMessage = "未接続のため実行できません。";
                return;
            }
            if (CurrentStepIndex < 0 || CurrentStepIndex >= EditingSteps.Count)
            {
                StatusMessage = "実行するステップがありません。";
                return;
            }

            var wrapper = EditingSteps[CurrentStepIndex];
            var step = wrapper.ToModel();   // SequenceStepWrapper → SequenceStep に変換（CalculateInterpolationSteps を使うため）
            var color = new Rgb(step.ColorR, step.ColorG, step.ColorB);
            var target = Target.All;

            try
            {
                switch (step.Command)
                {
                    case "SetColor":
                        await _lighting.SetColorAsync(target, color);
                        break;
                    case "Off":
                        await _lighting.SetColorAsync(target, new Rgb(0, 0, 0));
                        break;
                    case "Flash":
                        await _lighting.StartEffectAsync(
                            "Flash", color,
                            cycleDurationMs: step.DurationMs,
                            flashIntervalMs: Math.Max(50, step.DurationMs / 2),
                            fadeSteps: step.CalculateInterpolationSteps(),
                            continuous: false);
                        break;
                    case "FadeIn":
                        await _lighting.StartEffectAsync(
                            "FadeIn", color,
                            cycleDurationMs: step.DurationMs,
                            fadeSteps: step.CalculateInterpolationSteps(),
                            continuous: false);
                        break;
                    case "FadeOut":
                        await _lighting.StartEffectAsync(
                            "FadeOut", color,
                            cycleDurationMs: step.DurationMs,
                            fadeSteps: step.CalculateInterpolationSteps(),
                            continuous: false);
                        break;
                    case "Breath":
                        await _lighting.StartEffectAsync(
                            "Breathing", color,
                            cycleDurationMs: step.DurationMs,
                            fadeSteps: step.CalculateInterpolationSteps(),
                            continuous: false);
                        break;
                    case "SevenColor":
                        await _lighting.StartEffectAsync(
                            "SevenColor", color,
                            cycleDurationMs: step.DurationMs,
                            fadeSteps: step.CalculateInterpolationSteps(),
                            continuous: false);
                        break;
                    default:
                        StatusMessage = $"未対応コマンド: {step.Command}";
                        return;
                }

                StatusMessage = $"実行: {step.Command} ({color.R},{color.G},{color.B}) → ステップ {CurrentStepIndex + 1}/{EditingSteps.Count}";

                // 実行後、自動的に次ステップへ移動
                if (CurrentStepIndex < EditingSteps.Count - 1)
                {
                    CurrentStepIndex++;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"実行失敗: {ex.Message}";
                Log.Warning(ex, "ExecuteCurrentStep failed");
            }
        }

        /// <summary>次ステップに移動（実行はしない）</summary>
        [RelayCommand]
        private void NextStep()
        {
            if (EditingSteps.Count == 0) return;
            if (CurrentStepIndex < EditingSteps.Count - 1)
            {
                CurrentStepIndex++;
                StatusMessage = $"次ステップ：{CurrentStepIndex + 1}/{EditingSteps.Count}";
            }
            else
            {
                StatusMessage = "最後のステップです。";
            }
        }

        /// <summary>前ステップに移動</summary>
        [RelayCommand]
        private void PreviousStep()
        {
            if (EditingSteps.Count == 0) return;
            if (CurrentStepIndex > 0)
            {
                CurrentStepIndex--;
                StatusMessage = $"前ステップ：{CurrentStepIndex + 1}/{EditingSteps.Count}";
            }
            else
            {
                StatusMessage = "最初のステップです。";
            }
        }

        /// <summary>ステップ実行を停止（実行中のエフェクトを止める）</summary>
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

        #endregion

        #region コマンド：API 一括再生（既存 TimeSequenceViewModel から継承）

        /// <summary>一括再生／停止トグル（API 経由でシーケンス全体を自動再生）</summary>
        [RelayCommand]
        private async Task PlayOrStopAsync()
        {
            if (_lighting == null)
            {
                StatusMessage = "API 連携が無効のため再生できません。";
                return;
            }

            if (IsPlaying)
            {
                try
                {
                    await _lighting.StopSequenceAsync();
                    StatusMessage = "シーケンスを停止しました。";
                }
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
            finally
            {
                await PollStatusAsync();
            }
        }

        /// <summary>API へ同期（再生はしない）</summary>
        [RelayCommand]
        private async Task SyncToApiAsync()
        {
            if (_lighting == null || _editingSequence == null)
            {
                StatusMessage = "同期対象がありません。";
                return;
            }
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
                if (a.Command != b.Command) return true;
                if (a.ColorR != b.ColorR || a.ColorG != b.ColorG || a.ColorB != b.ColorB) return true;
                if (a.DurationMs != b.DurationMs) return true;
                if (a.RetransmitCount != b.RetransmitCount) return true;
                if (a.InterpolationIntervalMs != b.InterpolationIntervalMs) return true;
            }
            return false;
        }

        /// <summary>12 色プリセットを生成</summary>
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

        /// <summary>動作プリセットを生成</summary>
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


    /// <summary>色プリセット 1 件</summary>
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
        public string Hex { get; }   // "#FF0000" 形式（XAML バインド用）

        [ObservableProperty]
        private bool isSelected;
    }

    /// <summary>動作プリセット 1 件</summary>
    public partial class ActionPresetItem : ObservableObject
    {
        public ActionPresetItem(string command, string displayName, string accentHex)
        {
            Command = command;
            DisplayName = displayName;
            AccentHex = accentHex;
        }
        public string Command { get; }       // 内部値 "SetColor" / "FadeIn" 等
        public string DisplayName { get; }   // 表示名 "色固定" / "Fade In" 等
        public string AccentHex { get; }     // アクセント色 "#FFC107" 等

        [ObservableProperty]
        private bool isSelected;
    }
}
