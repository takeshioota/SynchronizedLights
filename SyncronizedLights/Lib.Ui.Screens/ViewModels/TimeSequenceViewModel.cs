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
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// 時間ベースシーケンス編集画面の ViewModel
    /// 概要：追加機能③に基づくシーケンス編集UI。
    ///       一覧表示・新規作成・保存・削除・ステップ編集（DataGrid）を提供。
    /// 構造：
    ///   - 左ペイン：シーケンス一覧（ListBox）
    ///   - 右ペイン：選択中シーケンスの編集（名前、説明、ステップDataGrid）
    /// </summary>
    public partial class TimeSequenceViewModel : ObservableObject
    {
        #region フィールド
        private readonly TimeBasedSequenceStore _store;
        private TimeBasedSequence? _editingSequence;  // 編集中シーケンスの実体
        private readonly ILightingFacade? _lighting;       // 再生エンジン（null 許容：旧コンストラクタ互換）
        private readonly DispatcherTimer? _statusTimer;    // 再生状態ポーリング Timer
        #endregion

        #region コンストラクタ
        /// <summary>旧コンストラクタ（API 連携なし、Lighting=null）</summary>
        public TimeSequenceViewModel() : this(null)
        {
        }

        /// <summary>新コンストラクタ：ILightingFacade を受け取って API 再生エンジンと連携</summary>
        public TimeSequenceViewModel(ILightingFacade? lighting)
        {
            string? dataDir = null;
            try
            {
                var appType = System.Windows.Application.Current?.GetType();
                var prop = appType?.GetProperty("DataDirectory", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                dataDir = prop?.GetValue(null) as string;
            }
            catch { }
            _store = new TimeBasedSequenceStore(dataDir);
            _lighting = lighting;

            Sequences = new ObservableCollection<TimeBasedSequence>();
            EditingSteps = new ObservableCollection<SequenceStepWrapper>();
            ReloadSequences();

            if (_lighting != null)
            {
                // 1.5 秒ごとに再生状態をポーリング（軽量）
                _statusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5),
                };
                _statusTimer.Tick += async (_, _) => await PollStatusAsync();
                _statusTimer.Start();
            }
        }
        #endregion

        #region プロパティ

        /// <summary>シーケンス一覧（左ペイン用）</summary>
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

        /// <summary>編集中ステップ一覧（DataGrid 用）</summary>
        public ObservableCollection<SequenceStepWrapper> EditingSteps { get; }

        /// <summary>選択中ステップ（行削除用）</summary>
        [ObservableProperty]
        private SequenceStepWrapper? selectedStep;

        /// <summary>状態メッセージ</summary>
        [ObservableProperty]
        private string statusMessage = "シーケンスを選択するか、「新規」で作成してください。";

        /// <summary>保存先ディレクトリのパス（情報表示用）</summary>
        public string StoreDirectoryPath => _store.GetStoreDirectoryPath();

        /// <summary>API 連携が有効か（ILightingFacade 注入有無）</summary>
        public bool IsApiEnabled => _lighting != null;

        /// <summary>シーケンス再生中かどうか（API 状態ポーリング由来）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayButtonText))]
        private bool isPlaying;

        /// <summary>再生中シーケンス名（API 状態ポーリング由来）</summary>
        [ObservableProperty]
        private string? playingSequenceName;

        /// <summary>再生ボタン表示テキスト（実行中は "■ 停止"）</summary>
        public string PlayButtonText => IsPlaying ? "■ 停止" : "▶ 再生";
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
                StatusMessage = "シーケンスを選択するか、「新規」で作成してください。";
                return;
            }

            // 編集ペインに値を流し込む
            _editingSequence = value;
            EditingName = value.Name;
            EditingDescription = value.Description;

            EditingSteps.Clear();
            foreach (var step in value.Steps)
            {
                EditingSteps.Add(new SequenceStepWrapper(step));
            }

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

            // 名前の重複チェック（自分以外）
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

            // 編集ペインの内容を _editingSequence に書き戻す
            _editingSequence.Name = EditingName.Trim();
            _editingSequence.Description = EditingDescription ?? "";
            _editingSequence.Steps = EditingSteps.Select(w => w.ToModel()).ToList();

            //   再選択に必要な情報をローカル変数に退避してから ReloadSequences する。
            var savedId = _editingSequence.Id;
            var savedName = _editingSequence.Name;
            var savedStepCount = _editingSequence.Steps.Count;

            if (_store.Save(_editingSequence))
            {
                ReloadSequences();
                // 保存後の再選択（ローカル変数を使う：_editingSequence は null になっている可能性あり）
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

        /// <summary>編集破棄（一覧から再読込）</summary>
        [RelayCommand]
        private void DiscardChanges()
        {
            if (_editingSequence == null) return;
            // SelectedSequence を一度クリアして再選択することで OnSelectedSequenceChanged 経由で復元
            var id = _editingSequence.Id;
            ReloadSequences();
            SelectedSequence = Sequences.FirstOrDefault(s => s.Id == id);
            StatusMessage = "編集を破棄して最後の保存状態に戻しました。";
        }

        /// <summary>
        /// 再生/停止トグル（API 経由）
        /// 概要：未再生時 → API に Upsert + Play 指示
        ///       再生中    → Stop 指示
        /// </summary>
        [RelayCommand]
        private async Task PlayOrStopAsync()
        {
            if (_lighting == null)
            {
                StatusMessage = "API 連携が無効のため再生できません。";
                return;
            }

            // 既に再生中なら停止
            if (IsPlaying)
            {
                try
                {
                    await _lighting.StopSequenceAsync();
                    StatusMessage = "シーケンスを停止しました。";
                }
                catch (Exception ex)
                {
                    StatusMessage = $"停止失敗: {ex.Message}";
                    Log.Warning(ex, "TimeSequence Stop failed");
                }
                finally
                {
                    await PollStatusAsync();
                }
                return;
            }

            // 未再生 → 現在のシーケンスを再生
            if (SelectedSequence == null || _editingSequence == null)
            {
                StatusMessage = "再生対象のシーケンスを選択してください。";
                return;
            }

            // 未保存編集があれば確認
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
                // 1) UI モデル → API 形式 に変換
                var apiSteps = SequenceApiMapper.ToApiSteps(_editingSequence);

                // 2) API へ Upsert（同名上書き）
                await _lighting.UpsertSequenceAsync(_editingSequence.Name, apiSteps);

                // 3) 再生開始
                await _lighting.PlaySequenceAsync(_editingSequence.Name);

                StatusMessage = $"再生開始: {_editingSequence.Name}（{apiSteps.Count} ステップ）";
            }
            catch (Exception ex)
            {
                StatusMessage = $"再生失敗: {ex.Message}";
                Log.Warning(ex, "TimeSequence Play failed");
            }
            finally
            {
                await PollStatusAsync();
            }
        }

        /// <summary>
        /// 編集中シーケンスを API へ同期（Upsert のみ・再生しない）
        /// </summary>
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
                // 編集中の値を Steps に書き戻し
                _editingSequence.Steps = EditingSteps.Select(w => w.ToModel()).ToList();
                var apiSteps = SequenceApiMapper.ToApiSteps(_editingSequence);
                await _lighting.UpsertSequenceAsync(_editingSequence.Name, apiSteps);
                StatusMessage = $"API へ同期完了: {_editingSequence.Name}（{apiSteps.Count} ステップ）";
            }
            catch (Exception ex)
            {
                StatusMessage = $"同期失敗: {ex.Message}";
                Log.Warning(ex, "TimeSequence SyncToApi failed");
            }
        }

        /// <summary>未保存編集の有無を判定</summary>
        private bool HasUnsavedEdits()
        {
            if (_editingSequence == null) return false;
            // 名前 / 説明 / ステップ数 / ステップ内容のいずれかが異なれば未保存
            if (_editingSequence.Name != EditingName.Trim()) return true;
            if ((_editingSequence.Description ?? "") != (EditingDescription ?? "")) return true;
            if (_editingSequence.Steps.Count != EditingSteps.Count) return true;
            // ステップごとの簡易ハッシュ比較
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
        #endregion

        #region コマンド：ステップ編集

        /// <summary>ステップ追加（末尾に追加）</summary>
        [RelayCommand]
        private void AddStep()
        {
            if (_editingSequence == null)
            {
                StatusMessage = "シーケンスを選択するか新規作成してください。";
                return;
            }

            // NO.25: デフォルトは無限時間（TimeMs=0）— 数字を入れた場合のみ次行へ進む
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
            EditingSteps.Add(new SequenceStepWrapper(step));
            StatusMessage = "ステップ追加（RGB固定:FF,FF,FF エフェクト無し）。 "
                          + "「保存」を押すまでは未保存です。";
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
            StatusMessage = "ステップを削除しました。「保存」を押すまでは未保存です。";
        }

        /// <summary>ステップを時刻順にソート</summary>
        [RelayCommand]
        private void SortSteps()
        {
            var sorted = EditingSteps.OrderBy(s => s.TimeMs).ToList();
            EditingSteps.Clear();
            foreach (var s in sorted) EditingSteps.Add(s);
            StatusMessage = $"時刻順にソートしました（{EditingSteps.Count} ステップ）。";
        }

        #endregion

        #region 内部メソッド

        /// <summary>一覧を再読込</summary>
        private void ReloadSequences()
        {
            Sequences.Clear();
            foreach (var seq in _store.LoadAll())
            {
                Sequences.Add(seq);
            }
        }

        /// <summary>API 再生状態をポーリング更新</summary>
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
                // ポーリングは失敗しても黙殺（接続切れ等）
                IsPlaying = false;
                PlayingSequenceName = null;
            }
        }
        #endregion
    }

    /// <summary>
    /// SequenceStep の DataGrid 編集用ラッパー
    /// 概要：CommandType + EffectType の 2 軸構造に対応。
    ///       ToModel() で永続化用 SequenceStep に戻す。
    /// </summary>
    public partial class SequenceStepWrapper : ObservableObject
    {
        private int _timeMs;
        private int _effectCycleDurationMs;
        private int _fadeSteps;
        private int _transitionMs;

        public SequenceStepWrapper() { }

        public SequenceStepWrapper(SequenceStep src)
        {
            _timeMs = src.TimeMs;
            commandType = string.IsNullOrEmpty(src.CommandType) ? "Color" : src.CommandType;
            effectType = src.EffectType ?? "";
            colorR = src.ColorR;
            colorG = src.ColorG;
            colorB = src.ColorB;
            _effectCycleDurationMs = src.EffectCycleDurationMs ?? 0;
            _fadeSteps = src.FadeSteps ?? 0;
            retransmitCount = src.RetransmitCount;
            comment = src.Comment ?? "";
            note = src.Note ?? "";
            continuous = src.Continuous;
            _transitionMs = src.TransitionMs;
            color2R = src.Color2R ?? 0;
            color2G = src.Color2G ?? 0;
            color2B = src.Color2B ?? 0;
            _bpm = src.Bpm ?? 120;
            // F1: StepNumber があればそのまま使用（なければ RenumberEditingSteps で自動採番される）
            if (src.StepNumber.HasValue) rowNumber = src.StepNumber.Value;
            isLocked = src.IsLocked;
            subSequenceComment = src.SubSequenceComment ?? "";
            presetSequenceName = src.PresetSequenceName ?? "";
            frameNo = src.FrameNo ?? 0;
            // Rainbow (V4.5)
            rainbowMode = src.RainbowMode ?? RainbowMode.Solid;
            rainbowColors = src.RainbowColors != null
                ? new ObservableCollection<RgbColorItem>(src.RainbowColors.Select(c => new RgbColorItem(c.R, c.G, c.B)))
                : new ObservableCollection<RgbColorItem>();
            rainbowCycleDurationMs = src.RainbowCycleDurationMs ?? 1000;
            rainbowBlinkPeriodMs = src.RainbowBlinkPeriodMs ?? 500;
            _rainbowDutyRatio = src.RainbowDutyRatio ?? 5;
            rainbowFadeInMs = src.RainbowFadeInMs ?? 1000;
            rainbowFadeOutMs = src.RainbowFadeOutMs ?? 1000;
        }

        /// <summary>開始時刻（ms）— 内部値</summary>
        public int TimeMs
        {
            get => _timeMs;
            set
            {
                if (_timeMs != value)
                {
                    _timeMs = Math.Max(0, value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TimeSec));
                }
            }
        }

        /// <summary>開始時刻（秒、UI表示用）— NO.25: null=無限（空白表示）</summary>
        public double? TimeSec
        {
            get => _timeMs == 0 ? null : _timeMs / 1000.0;
            set
            {
                var ms = value.HasValue ? (int)Math.Max(0, Math.Round(value.Value * 1000)) : 0;
                if (_timeMs != ms)
                {
                    _timeMs = ms;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TimeMs));
                }
            }
        }

        /// <summary>コマンド種別："Color" / "Off" / "Effect" / "EffectStop"</summary>
        [ObservableProperty]
        private string commandType = "Color";

        /// <summary>エフェクト種別："Flash" / "FadeIn" / "FadeOut" / "Breathing" / "SevenColor" / ""</summary>
        [ObservableProperty]
        private string effectType = "";

        [ObservableProperty]
        private byte colorR = 255;

        [ObservableProperty]
        private byte colorG = 255;

        [ObservableProperty]
        private byte colorB = 255;

        /// <summary>エフェクトサイクル時間（ms）— Effect 時のみ意味を持つ</summary>
        public int EffectCycleDurationMs
        {
            get => _effectCycleDurationMs;
            set
            {
                if (_effectCycleDurationMs != value)
                {
                    _effectCycleDurationMs = Math.Max(0, value);
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Fade ステップ数 — Effect 時のみ意味を持つ</summary>
        public int FadeSteps
        {
            get => _fadeSteps;
            set
            {
                if (_fadeSteps != value)
                {
                    _fadeSteps = Math.Max(0, value);
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 行間遷移時間（ms）— 0=即時切替、N>0=Nミリ秒かけてスムーズ遷移
        /// </summary>
        public int TransitionMs
        {
            get => _transitionMs;
            set
            {
                if (_transitionMs != value)
                {
                    _transitionMs = Math.Max(0, value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TransitionSec));
                }
            }
        }

        /// <summary>遷移時間（秒、UI表示用）</summary>
        public double TransitionSec => _transitionMs / 1000.0;

        /// <summary>2色目 R（Color2 時のみ有効）</summary>
        [ObservableProperty]
        private byte color2R;

        /// <summary>2色目 G（Color2 時のみ有効）</summary>
        [ObservableProperty]
        private byte color2G;

        /// <summary>2色目 B（Color2 時のみ有効）</summary>
        [ObservableProperty]
        private byte color2B;

        /// <summary>BPM（Color2 時のみ有効、2色交互点灯の周期）— 0〜6000。
        /// NO.31: 0 を許可（0=未設定。自動送りせず Time(sec)/手動送りにフォールバック）。</summary>
        private int _bpm = 120;
        public int Bpm
        {
            get => _bpm;
            set
            {
                if (_bpm != value)
                {
                    _bpm = Math.Clamp(value, 0, 6000);
                    OnPropertyChanged();
                }
            }
        }

        [ObservableProperty]
        private int retransmitCount = 3;

        /// <summary>コメント（曲名・場面名など、15 文字程度を想定）</summary>
        [ObservableProperty]
        private string comment = "";

        /// <summary>メモ（自由記述）</summary>
        [ObservableProperty]
        private string note = "";

        /// <summary>
        /// エフェクト連続実行フラグ（CommandType="Effect" 時のみ意味を持つ）
        /// 値： null = 未指定（繰り返し相当）、
        ///        true = 繰り返し実行、
        ///        false = 1 回実行後 最終色保持（「Fade In/In」「Fade Out/Out」などで使用）
        /// </summary>
        [ObservableProperty]
        private bool? continuous;

        /// <summary>
        /// DataGrid のエフェクト列に表示する文字列。
        /// EffectType と Continuous の組み合わせを 1 列で見分けられるようにする：
        ///   FadeIn + Continuous=false  → "FadeIn/In"
        ///   FadeOut + Continuous=false → "FadeOut/Out"
        ///   それ以外                    → EffectType そのまま
        /// セット時は逆変換して EffectType / Continuous を同時に更新する。
        /// </summary>
        public string EffectTypeDisplay
        {
            get
            {
                if (Continuous == false && EffectType == "FadeIn") return "FadeIn/In";
                if (Continuous == false && EffectType == "FadeOut") return "FadeOut/Out";
                return EffectType ?? "";
            }
            set
            {
                switch (value)
                {
                    case "FadeIn/In":
                        EffectType = "FadeIn";
                        Continuous = false;
                        break;
                    case "FadeOut/Out":
                        EffectType = "FadeOut";
                        Continuous = false;
                        break;
                    default:
                        EffectType = value;
                        // 通常 Fade In / Fade Out へ戻したときは保持フラグもクリアする
                        if ((value == "FadeIn" || value == "FadeOut") && Continuous == false)
                        {
                            Continuous = null;
                        }
                        break;
                }
                OnPropertyChanged();
            }
        }

        partial void OnEffectTypeChanged(string value) => OnPropertyChanged(nameof(EffectTypeDisplay));
        partial void OnContinuousChanged(bool? value) => OnPropertyChanged(nameof(EffectTypeDisplay));

        /// <summary>
        /// 連続再生中の現在ステップかどうか（DataGrid のハイライト表示に使用）
        /// 概要：API 側の play status から取得した currentStepIndex に該当する行で true。
        ///       永続化対象外（[JsonIgnore] 相当だが、ToModel に含めないことで JSON 出力されない）。
        /// </summary>
        [ObservableProperty]
        private bool isCurrentlyPlaying;

        /// <summary>
        /// F1: DataGrid 表示用のステップ番号（小数入力対応、例: 1.0, 1.1, 2.5）
        /// 概要：オペレータが手入力で小数番号を振り、ソートに使用できる。
        ///       行追加・削除・移動・ソート時に SequenceEditorViewModel 側で振り直される。
        ///       永続化対象（StepNumber として ToModel に含める）。
        /// </summary>
        [ObservableProperty]
        private double rowNumber;

        /// <summary>
        /// ロック状態（true = 選択時に実行しない）
        /// 概要：本番中に先の演出を安全に修正するため、ロックした行は自動実行をスキップ。
        /// </summary>
        [ObservableProperty]
        private bool isLocked;

        /// <summary>
        /// サブシーケンスコメント（シーケンス内の区切り・グループ名）
        /// </summary>
        [ObservableProperty]
        private string subSequenceComment = "";

        /// <summary>
        /// プリセットシーケンス名（CommandType="Preset" 時に呼び出すシーケンス名）
        /// </summary>
        [ObservableProperty]
        private string presetSequenceName = "";

        /// <summary>
        /// 内蔵プログラムのフレーム番号（CommandType="InternalProgram" 時のみ有効）
        /// </summary>
        [ObservableProperty]
        private uint frameNo;

        // ─── Rainbow（V4.5: 3.15-3.22）─────────────────────────────────────

        /// <summary>レインボーモード（CommandType="Rainbow" 時のみ有効）</summary>
        [ObservableProperty]
        private RainbowMode rainbowMode = RainbowMode.Solid;

        /// <summary>レインボーカラーパレット（2〜7色）</summary>
        [ObservableProperty]
        private ObservableCollection<RgbColorItem> rainbowColors = new();

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

        /// <summary>
        /// Chase/Overlap トリガー表示（"Chase" / "OL" / ""）
        /// 概要：DataGrid の Trig 列に表示。永続化対象外（実行時のみ設定）。
        /// </summary>
        [ObservableProperty]
        private string trig = "";

        /// <summary>
        /// カラープレビュー用ブラシ（DataGrid のカラー列にバインド）
        /// </summary>
        public System.Windows.Media.SolidColorBrush ColorPreviewBrush =>
            new(System.Windows.Media.Color.FromRgb(ColorR, ColorG, ColorB));

        partial void OnColorRChanged(byte value) => OnPropertyChanged(nameof(ColorPreviewBrush));
        partial void OnColorGChanged(byte value) => OnPropertyChanged(nameof(ColorPreviewBrush));
        partial void OnColorBChanged(byte value) => OnPropertyChanged(nameof(ColorPreviewBrush));

        /// <summary>永続化用 SequenceStep に変換</summary>
        public SequenceStep ToModel()
        {
            return new SequenceStep
            {
                TimeMs = TimeMs,
                CommandType = string.IsNullOrEmpty(CommandType) ? "Color" : CommandType,
                EffectType = string.IsNullOrEmpty(EffectType) ? null : EffectType,
                ColorR = ColorR,
                ColorG = ColorG,
                ColorB = ColorB,
                EffectCycleDurationMs = (CommandType == "Effect" && EffectCycleDurationMs > 0)
                    ? EffectCycleDurationMs
                    : (int?)null,
                FadeSteps = (CommandType == "Effect" && FadeSteps > 0)
                    ? FadeSteps
                    : (int?)null,
                RetransmitCount = RetransmitCount,
                Comment = Comment ?? "",
                Note = Note ?? "",
                // Effect 以外では連続実行フラグは意味を持たないため null 化する
                Continuous = (CommandType == "Effect") ? Continuous : null,
                TransitionMs = TransitionMs,
                // Color2 コマンド時のみ 2 色目と BPM を保存
                Color2R = (CommandType == "Color2") ? Color2R : null,
                Color2G = (CommandType == "Color2") ? Color2G : null,
                Color2B = (CommandType == "Color2") ? Color2B : null,
                Bpm = (CommandType == "Color2") ? Bpm : null,
                // F1: ステップ番号を永続化
                StepNumber = RowNumber,
                IsLocked = IsLocked,
                SubSequenceComment = SubSequenceComment ?? "",
                PresetSequenceName = (CommandType == "Preset") ? PresetSequenceName : "",
                FrameNo = (CommandType == "InternalProgram") ? FrameNo : null,
                // Rainbow (V4.5)
                RainbowMode = (CommandType == "Rainbow") ? RainbowMode : null,
                RainbowColors = (CommandType == "Rainbow" && RainbowColors.Count > 0)
                    ? RainbowColors.Select(c => new Rgb(c.R, c.G, c.B)).ToList()
                    : null,
                RainbowCycleDurationMs = (CommandType == "Rainbow") ? RainbowCycleDurationMs : null,
                RainbowBlinkPeriodMs = (CommandType == "Rainbow" && RainbowMode == RainbowMode.Blink)
                    ? RainbowBlinkPeriodMs : null,
                RainbowDutyRatio = (CommandType == "Rainbow" && RainbowMode == RainbowMode.Blink)
                    ? RainbowDutyRatio : null,
                RainbowFadeInMs = (CommandType == "Rainbow" && RainbowMode is RainbowMode.FadeInOut or RainbowMode.FadeIn)
                    ? RainbowFadeInMs : null,
                RainbowFadeOutMs = (CommandType == "Rainbow" && RainbowMode is RainbowMode.FadeInOut or RainbowMode.FadeOut)
                    ? RainbowFadeOutMs : null,
            };
        }
    }

}
