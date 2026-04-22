using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Facades;
using Lib.Application.Interfaces;
using Lib.Domain.Enums;
using Lib.Domain.States;
using Lib.Domain.ValueObjects;
using Lib.Ui.Screens.ViewModels;
using Lib.Ui.Screens.Views;
using System.Windows;
using Lib.Application.Models;
using Lib.Application.Services;
namespace SynchronizedLights.UI.ViewModels
{
    /// <summary>
    /// ViewModel基底クラス
    /// 概要：画面用ViewModelの共通基底クラス。
    /// ObservableObjectを継承し、プロパティ変更通知機能を提供する。
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        #region フィールド

        /// <summary>
        /// ライティング制御ファセード
        /// 概要：UIと通信層の境界。現時点はDummy実装。
        /// SynchrolightAPI.Core連携の実装に差し替える。
        /// </summary>
        private readonly ILightingFacade _lighting = new DummyLightingFacade();
        /// <summary>
        /// エラートースト自動消去タイマー
        /// 概要：ErrorMessageが設定されてから一定時間経過で自動消去するタイマー。
        /// </summary>
        private DispatcherTimer? _errorDismissTimer;

        /// <summary>
        /// エラートースト表示時間（秒）
        /// </summary>
        private const int ErrorToastDurationSeconds = 5;

        /// <summary>
        /// カラーピッカー表示コマンド
        /// 概要：DlgColorPicker をモーダル表示し、選択された色を
        /// AppState.SelectedColor に反映する。
        /// 起動時は現在のAppState.SelectedColorを初期色として渡す。
        /// </summary>
        [RelayCommand]
        private void OpenColorPicker()
        {
            var dialog = new DlgColorPicker
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            dialog.SetInitialColor(
                AppState.SelectedColor.R,
                AppState.SelectedColor.G,
                AppState.SelectedColor.B);

            if (dialog.ShowDialog() == true)
            {
                AppState.SelectedColor = new Rgb(
                    dialog.SelectedR,
                    dialog.SelectedG,
                    dialog.SelectedB);
                OnPropertyChanged(nameof(CurrentColorLabel));
                StatusMessage =
                    $"Custom color : R={dialog.SelectedR}, G={dialog.SelectedG}, B={dialog.SelectedB}";
                SyncPresetStateIfActive();
            }
        }
        #endregion フィールド

        #region プロパティ
        /// <summary>
        /// ウィンドウタイトル
        /// 概要：メイン画面タイトルバーに表示する文字列。
        /// </summary>
        [ObservableProperty]
        private string windowTitle = "Synchronized Lights Control";

        /// <summary>
        /// 現在の画面カテゴリ
        /// 概要：現在選択中の画面カテゴリを表す。
        /// Preset / Mode / Animation / Sequence / Setting の切替に使用する。
        /// </summary>
        [ObservableProperty]
        private UiCategory currentCategory = UiCategory.Preset;

        /// <summary>
        /// アプリケーション共通状態
        /// 概要：現在の選択対象、色、速度、接続情報など、
        /// 画面全体で共有する状態を保持する。
        /// </summary>
        [ObservableProperty]
        private AppState appState = new();

        /// <summary>
        /// 現在表示中の画面ViewModel
        /// 概要：Main Area に表示する画面用ViewModelを保持する。
        /// CurrentCategory の値に応じて切り替わる。
        /// </summary>
        [ObservableProperty]
        private object? currentViewModel;

        /// <summary>
        /// 現在選択中の対象表示文字列
        /// 概要：TopBar などに表示する対象名。
        /// </summary>
        public string CurrentTargetLabel => AppState.SelectedTarget switch
        {
            Target.All => "ALL",
            Target.Group01 => "Group01",
            Target.Group02 => "Group02",
            Target.Group03 => "Group03",
            Target.Group04 => "Group04",
            Target.Group05 => "Group05",
            Target.Group06 => "Group06",
            Target.Group07 => "Group07",
            Target.Group08 => "Group08",
            _ => "ALL"
        };

        /// <summary>
        /// 現在速度表示文字列
        /// 概要：TopBar などに表示する現在速度文字列。
        /// AppStateの速度値を画面表示用に整形して返す。
        /// </summary>
        public string CurrentSpeedLabel => $"{AppState.SpeedValueMs} ms";

        /// <summary>
        /// 編集可能な速度値
        /// 概要：Preset画面のスライダー・数値入力からの双方向バインド用プロパティ。
        /// </summary>
        public int EditableSpeedValueMs
        {
            get => AppState.SpeedValueMs;
            set
            {
                var clamped = Math.Clamp(value, 0, 10000);
                if (AppState.SpeedValueMs != clamped)
                {
                    AppState.SpeedValueMs = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CurrentSpeedLabel));
                    RefreshSpeedSelection();
                    StatusMessage = $"Speed set : {CurrentSpeedLabel}";

                    // スライダー操作・プリセット適用時に TextBox の表示も同期
                    EditableSpeedText = clamped.ToString();
                }
            }
        }

        /// <summary>
        /// 速度値の入力文字列
        /// 概要：TextBox に表示・入力される生文字列。
        /// 範囲外（<0 or >10000）や非数値でも保持し、IsSpeedValid=false で赤枠表示。
        /// 有効値に戻ると EditableSpeedValueMs（AppState）にも反映される。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSpeedValid))]
        private string editableSpeedText = "1000";

        /// <summary>
        /// Speed 入力値の有効性
        /// 概要：0〜10000 の整数なら true。範囲外 or 非数値なら false。
        /// PresetView の NumSpeed TextBox で赤枠表示に使用。
        /// </summary>
        public bool IsSpeedValid
        {
            get
            {
                if (!int.TryParse(EditableSpeedText, out var n)) return false;
                return n >= 0 && n <= 10000;
            }
        }

        /// <summary>
        /// EditableSpeedText 変更時：有効範囲内なら AppState に反映
        /// 概要：範囲外のときは AppState を更新しない（Slider は最後の有効値のまま）
        /// </summary>
        partial void OnEditableSpeedTextChanged(string value)
        {
            if (int.TryParse(value, out var n) && n >= 0 && n <= 10000)
            {
                if (AppState.SpeedValueMs != n)
                {
                    AppState.SpeedValueMs = n;
                    OnPropertyChanged(nameof(EditableSpeedValueMs));
                    OnPropertyChanged(nameof(CurrentSpeedLabel));
                    RefreshSpeedSelection();
                    StatusMessage = $"Speed set : {CurrentSpeedLabel}";
                }
            }
            // 範囲外 or 非数値 → AppState は維持、赤枠だけ表示
        }

        /// <summary>
        /// 現在色表示文字列
        /// 概要：AppStateに保持している現在色を画面表示用の文字列として返す。
        /// </summary>
        public string CurrentColorLabel
            => $"R={AppState.SelectedColor.R}, G={AppState.SelectedColor.G}, B={AppState.SelectedColor.B}";

        /// <summary>
        /// 直近操作メッセージ
        /// 概要：Effectボタン押下時の確認表示用。
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "Ready";

        #region カテゴリ
        /// <summary>
        /// Presetカテゴリが選択中かどうか
        /// </summary>
        public bool IsPresetSelected => CurrentCategory == UiCategory.Preset;

        /// <summary>
        /// Modeカテゴリが選択中かどうか
        /// </summary>
        public bool IsModeSelected => CurrentCategory == UiCategory.Mode;

        /// <summary>
        /// Animationカテゴリが選択中かどうか
        /// </summary>
        public bool IsAnimationSelected => CurrentCategory == UiCategory.Animation;

        /// <summary>
        /// Settingカテゴリが選択中かどうか
        /// </summary>
        public bool IsSettingSelected => CurrentCategory == UiCategory.Setting;
        #endregion カテゴリ

        #region ターゲット
        /// <summary>
        /// ALLターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetAllSelected => AppState.SelectedTarget == Target.All;

        /// <summary>
        /// Group01ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup01Selected => AppState.SelectedTarget == Target.Group01;

        /// <summary>
        /// Group02ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup02Selected => AppState.SelectedTarget == Target.Group02;

        /// <summary>
        /// Group03ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup03Selected => AppState.SelectedTarget == Target.Group03;

        /// <summary>
        /// Group04ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup04Selected => AppState.SelectedTarget == Target.Group04;

        /// <summary>
        /// Group05ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup05Selected => AppState.SelectedTarget == Target.Group05;

        /// <summary>
        /// Group06ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup06Selected => AppState.SelectedTarget == Target.Group06;

        /// <summary>
        /// Group07ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup07Selected => AppState.SelectedTarget == Target.Group07;

        /// <summary>
        /// Group08ターゲットが選択中かどうか
        /// </summary>
        public bool IsTargetGroup08Selected => AppState.SelectedTarget == Target.Group08;

        #endregion ターゲット

        #region スプレッド
        /// <summary>
        /// Speed01が選択中かどうか
        /// </summary>
        public bool IsSpeed01Selected => AppState.SpeedValueMs == 250;

        /// <summary>
        /// Speed02が選択中かどうか
        /// </summary>
        public bool IsSpeed02Selected => AppState.SpeedValueMs == 500;

        /// <summary>
        /// Speed03が選択中かどうか
        /// </summary>
        public bool IsSpeed03Selected => AppState.SpeedValueMs == 1000;

        /// <summary>
        /// Speed04が選択中かどうか
        /// </summary>
        public bool IsSpeed04Selected => AppState.SpeedValueMs == 2000;
        #endregion スプレッド

        /// <summary>
        /// 接続状態表示文字列
        /// 概要：Transportの接続状態を画面表示用に返す。
        /// </summary>
        public string ConnectionStatusLabel => IsTransportConnected ? "Connected" : "Disconnected";

        /// <summary>
        /// Transport接続中かどうか
        /// 概要：Effect有効/無効の判定に使用する。
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteFlashCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteFadeInCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteFadeOutCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteCurrentSettingsCommand))]
        private bool isTransportConnected;

        /// <summary>
        /// 送信処理実行中フラグ
        /// 概要：Flash/FadeIn/FadeOut/Execute/Sequence01-07 の実行中 true になる。
        /// true のとき全ての送信系ボタンが無効化
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteFlashCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteFadeInCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteFadeOutCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteCurrentSettingsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence01Command))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence02Command))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence03Command))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence04Command))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence05Command))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence06Command))]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence07Command))]
        private bool isBusy;

        /// <summary>
        /// エラー表示文字列
        /// 概要：Transportの最終エラーを画面表示する。
        /// エラーがない場合は "-" を表示する。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsErrorVisible))]
        private string errorMessage = "-";

        /// <summary>
        /// エラートースト表示可否
        /// 概要：ErrorMessageが空・未設定以外の場合にtrue。
        /// XAMLのDataTriggerで表示/非表示を切り替える。
        /// </summary>
        public bool IsErrorVisible
            => !string.IsNullOrEmpty(ErrorMessage) && ErrorMessage != "-";

        /// <summary>
        /// ストロボ停止中かどうか
        /// 概要：ONの間、ストロボ発光を停止する。
        /// </summary>
        [ObservableProperty]
        private bool isStrobeOffActive;

        /// <summary>
        /// 送信機チャネル値（最後に Initialize 実行時の値）
        /// 概要：SettingViewModel で設定された Channel を記録。UserState 永続化用。
        /// </summary>
        [ObservableProperty]
        private byte lastTransmitterChannel = 1;

        /// <summary>
        /// 送信機電力値（最後に Initialize 実行時の値）
        /// 概要：SettingViewModel で設定された Power を記録。UserState 永続化用。
        /// </summary>
        [ObservableProperty]
        private byte lastTransmitterPower = 3;

        /// <summary>
        /// 命令名
        /// 概要：ログ用の命令名。0〜32文字まで許容。空文字も許容。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCommandNameValid))]
        [NotifyPropertyChangedFor(nameof(CommandNameCountText))]
        private string commandName = "";

        /// <summary>
        /// 命令名が有効かどうか
        /// 概要：32文字以内なら true。XAML側の DataTrigger で赤枠表示に使う。
        /// </summary>
        public bool IsCommandNameValid => (CommandName?.Length ?? 0) <= 32;

        /// <summary>
        /// 命令名の文字数カウンタ表示
        /// 概要：「文字数超過でカウンタ赤」
        /// XAML側のDataTriggerでForegroundを赤に切り替える。
        /// </summary>
        public string CommandNameCountText
            => $"{(CommandName?.Length ?? 0)} / 32";

        /// <summary>
        /// 送信先ポート
        /// 概要：ALL / PortA / PortB のいずれか。
        /// </summary>
        [ObservableProperty]
        private string selectedPort = "ALL";

        /// <summary>
        /// ポート選択肢
        /// </summary>
        public IReadOnlyList<string> PortOptions { get; } = new[] { "ALL", "PortA", "PortB" };

        /// <summary>
        /// PortA状態表示
        /// 概要：TopBarに表示するPortAの接続状態。
        /// ファセードの接続ポートリストのうち、接続順で先頭のものを割り当てる。
        /// </summary>
        [ObservableProperty]
        private string portAStatusLabel = "PortA : -";

        /// <summary>
        /// PortB状態表示
        /// 概要：TopBarに表示するPortBの接続状態。
        /// ファセードの接続ポートリストのうち、接続順で2番目のものを割り当てる。
        /// </summary>
        [ObservableProperty]
        private string portBStatusLabel = "PortB : -";

        /// <summary>
        /// Sequence01が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence01Command))]
        private bool isSequence01Defined = true;

        /// <summary>
        /// Sequence02が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence02Command))]
        private bool isSequence02Defined = true;

        /// <summary>
        /// Sequence03が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence03Command))]
        private bool isSequence03Defined = false;

        /// <summary>
        /// Sequence04が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence04Command))]
        private bool isSequence04Defined = false;
        /// <summary>
        /// Sequence05が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence05Command))]
        private bool isSequence05Defined = false;

        /// <summary>
        /// Sequence06が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence06Command))]
        private bool isSequence06Defined = false;

        /// <summary>
        /// Sequence07が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteSequence07Command))]
        private bool isSequence07Defined = false;
        /// <summary>
        /// KPI バー用 ViewModel
        /// </summary>
        public KpiViewModel Kpi { get; } = new KpiViewModel();
        #endregion プロパティ

        #region コンストラクタ
        /// <summary>
        /// MainWindowViewModelを生成する
        /// </summary>
        public MainWindowViewModel()
            : this(App.LightingFacade ?? new DummyLightingFacade())
        {
        }

        /// <summary>
        /// MainWindowViewModelを生成する
        /// </summary>
        public MainWindowViewModel(ILightingFacade lighting)
        {
            _lighting = lighting;
            _lighting.StatusChanged += OnFacadeStatusChanged;
            UpdateCurrentViewModel();
            RefreshCategorySelection();
            RefreshTargetSelection();
            RefreshSpeedSelection();
            RefreshTransportState();
        }
        #endregion コンストラクタ

        #region OnCurrentCategoryChanged
        /// <summary>
        /// MainWindowViewModelを生成する
        /// 概要：画面切替に必要なUseCaseやダミー通信機能を初期化し、
        /// 初期カテゴリに応じた画面ViewModelを設定する。
        /// </summary>
        partial void OnCurrentCategoryChanged(UiCategory value)
        {
            UpdateCurrentViewModel();
            RefreshCategorySelection();
        }
        #endregion OnCurrentCategoryChanged

        #region OnIsStrobeOffActiveChanged
        /// <summary>
        /// ストロボ停止トグル変更時の処理
        /// 概要：ストロボ停止状態が切り替わった際にステータス表示を更新する。
        /// </summary>
        partial void OnIsStrobeOffActiveChanged(bool value)
        {
            StatusMessage = value ? "Strobe stopped (保持)" : "Strobe normal";
        }
        #endregion OnIsStrobeOffActiveChanged

        #region OnErrorMessageChanged

        /// <summary>
        /// ErrorMessage変更時の処理
        /// 概要：エラーが設定されたら自動消去タイマーを起動する。
        /// </summary>
        partial void OnErrorMessageChanged(string value)
        {
            if (!string.IsNullOrEmpty(value) && value != "-")
            {
                StartErrorAutoDismissTimer();
            }
        }

        #endregion OnErrorMessageChanged

        #region UpdateCurrentViewModel
        /// <summary>
        /// 表示中の画面ViewModelを更新する
        /// 概要：現在のカテゴリに応じて、
        /// Main Area に表示する画面用ViewModelを生成・切り替える。
        /// </summary>
        private void UpdateCurrentViewModel()
        {
            CurrentViewModel = CurrentCategory switch
            {
                UiCategory.Preset => CreatePresetViewModel(),
                UiCategory.Mode => new ModeViewModel(),
                UiCategory.Animation => new AnimationViewModel(_lighting),
                UiCategory.Sequence => new SequenceViewModel(),
                UiCategory.Setting => CreateSettingViewModel(),
                _ => CreatePresetViewModel()
            };
        }
        #endregion UpdateCurrentViewModel

        #region メソッド
        /// <summary>
        /// Preset画面用ViewModelを生成する。
        /// 概要：PresetViewModel生成時に色変更イベントを購読し、
        /// AppStateの対象・色と同期する。
        /// </summary>
        private PresetViewModel CreatePresetViewModel()
        {
            var viewModel = new PresetViewModel(_lighting);
            viewModel.ColorChanged += OnPresetColorChanged;
            viewModel.ApplyState(AppState.SelectedTarget, AppState.SelectedColor);
            return viewModel;
        }

        /// <summary>
        /// Setting画面用ViewModelを生成する（UserState復元対応）
        /// 概要：初期値として LastTransmitterChannel / Power を渡し、
        ///       SettingViewModel での変更を追跡する。
        /// </summary>
        private SettingViewModel CreateSettingViewModel()
        {
            var vm = new SettingViewModel(_lighting);
            // 前回値を反映
            vm.ChannelValue = LastTransmitterChannel;
            vm.PowerValue = LastTransmitterPower;
            // 変更追跡
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingViewModel.ChannelValue))
                {
                    LastTransmitterChannel = vm.ChannelValue;
                }
                else if (e.PropertyName == nameof(SettingViewModel.PowerValue))
                {
                    LastTransmitterPower = vm.PowerValue;
                }
            };
            return vm;
        }

        /// <summary>
        /// Preset画面の色変更通知を処理する。
        /// 概要：PresetViewModelで選択された色をAppStateへ反映し、
        /// 画面表示を更新する。
        /// </summary>
        private void OnPresetColorChanged(Rgb color)
        {
            AppState.SelectedColor = color;
            OnPropertyChanged(nameof(CurrentColorLabel));
            StatusMessage = $"Color changed : {CurrentColorLabel}";
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Preset画面表示中の状態同期を行う。
        /// 概要：現在表示中の画面がPresetViewModelの場合、
        /// AppStateの対象・色をPresetViewModelへ反映する。
        /// </summary>
        private void SyncPresetStateIfActive()
        {
            if (CurrentViewModel is PresetViewModel presetViewModel)
            {
                presetViewModel.ApplyState(AppState.SelectedTarget, AppState.SelectedColor);
            }
        }
        /// <summary>
        /// カテゴリ選択状態表示を更新する。
        /// </summary>
        private void RefreshCategorySelection()
        {
            OnPropertyChanged(nameof(IsPresetSelected));
            OnPropertyChanged(nameof(IsModeSelected));
            OnPropertyChanged(nameof(IsAnimationSelected));
            OnPropertyChanged(nameof(IsSettingSelected));
        }

        /// <summary>
        /// ターゲット選択状態表示を更新する。
        /// </summary>
        private void RefreshTargetSelection()
        {
            OnPropertyChanged(nameof(IsTargetAllSelected));
            OnPropertyChanged(nameof(IsTargetGroup01Selected));
            OnPropertyChanged(nameof(IsTargetGroup02Selected));
            OnPropertyChanged(nameof(IsTargetGroup03Selected));
            OnPropertyChanged(nameof(IsTargetGroup04Selected));
            OnPropertyChanged(nameof(IsTargetGroup05Selected));
            OnPropertyChanged(nameof(IsTargetGroup06Selected));
            OnPropertyChanged(nameof(IsTargetGroup07Selected));
            OnPropertyChanged(nameof(IsTargetGroup08Selected));
        }

        /// <summary>
        /// 速度選択状態表示を更新する。
        /// </summary>
        private void RefreshSpeedSelection()
        {
            OnPropertyChanged(nameof(IsSpeed01Selected));
            OnPropertyChanged(nameof(IsSpeed02Selected));
            OnPropertyChanged(nameof(IsSpeed03Selected));
            OnPropertyChanged(nameof(IsSpeed04Selected));
        }

        /// <summary>
        /// UserState を現在の画面状態に反映する（起動時に呼ばれる）
        /// 概要：永続化された値を復元
        /// </summary>
        public void ApplyUserState(UserState state)
        {
            if (state == null) return;

            // AppState 復元
            AppState.SpeedValueMs = state.SpeedValueMs;
            AppState.SelectedColor = new Rgb(state.ColorR, state.ColorG, state.ColorB);

            EditableSpeedText = state.SpeedValueMs.ToString();

            // Target 復元（文字列→Enum変換）
            if (Enum.TryParse<Target>(state.SelectedTarget, out var target))
            {
                AppState.SelectedTarget = target;
            }

            // Main画面のフィールド復元
            CommandName = state.CommandName ?? "";
            SelectedPort = state.SelectedPort ?? "ALL";
            LastTransmitterChannel = state.TransmitterChannel;
            LastTransmitterPower = state.TransmitterPower;

            // 画面表示を更新
            OnPropertyChanged(nameof(CurrentTargetLabel));
            OnPropertyChanged(nameof(CurrentColorLabel));
            OnPropertyChanged(nameof(CurrentSpeedLabel));
            RefreshTargetSelection();
            RefreshSpeedSelection();

            // Preset画面が開いている場合、色・対象を反映
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// 現在の画面状態から UserState を生成する（終了時に呼ばれる）
        /// 概要：永続化すべき全フィールドを収集。
        /// </summary>
        public UserState CaptureUserState()
        {
            return new UserState
            {
                SpeedValueMs = AppState.SpeedValueMs,
                ColorR = AppState.SelectedColor.R,
                ColorG = AppState.SelectedColor.G,
                ColorB = AppState.SelectedColor.B,
                SelectedTarget = AppState.SelectedTarget.ToString(),
                CommandName = CommandName ?? "",
                SelectedPort = SelectedPort ?? "ALL",
                TransmitterChannel = LastTransmitterChannel,
                TransmitterPower = LastTransmitterPower
            };
        }

        /// <summary>
        /// Transport状態を画面へ反映する。
        /// 概要：ファセードの接続状態・エラーを取得し、
        /// IsTransportConnected、ErrorMessage、PortA/PortB表示を更新する。
        /// </summary>
        private void RefreshTransportState()
        {
            IsTransportConnected = _lighting.IsConnected;
            ErrorMessage = _lighting.LastError ?? "-";
            UpdatePortStatusLabels();
            OnPropertyChanged(nameof(ConnectionStatusLabel));
        }

        /// <summary>
        /// シーケンス実行ダイアログを開いて実行する
        /// 概要：DlgSequence を表示し、
        /// OK時に開始番号/終了番号/遅延時間/色 のパラメータでシーケンスを送信する。
        /// </summary>
        private async Task RunSequenceWithDialogAsync(int seqId, string seqName)
        {
            var dlg = new Lib.Ui.Screens.Views.DlgSequence
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            dlg.SetSequence(seqId, seqName);

            if (dlg.ShowDialog() != true)
            {
                StatusMessage = $"Sequence cancelled : {seqName}";
                return;
            }

            try
            {
                if (!_lighting.IsConnected)
                {
                    StatusMessage = $"Sequence skipped : transport disconnected";
                    return;
                }

                // 任意色指定ありなら先に色を設定
                if (dlg.UseColor)
                {
                    var rgb = new Lib.Domain.ValueObjects.Rgb(dlg.ColorR, dlg.ColorG, dlg.ColorB);
                    await _lighting.SetColorAsync(AppState.SelectedTarget, rgb);
                }

                await _lighting.ExecuteSequenceAsync(AppState.SelectedTarget, seqId);
                StatusMessage =
                    $"Sequence executed : {seqName} " +
                    $"(開始={dlg.StartNumber}, 終了={dlg.EndNumber}, 遅延={dlg.DelayMs}ms)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Sequence failed : {ex.Message}";
            }
        }

        /// <summary>
        /// PortA/PortB 表示文字列を更新する
        /// 概要：ファセードの接続ポートリストを参照し、
        /// 接続順で先頭をPortA、2番目をPortBとして表示する。
        /// </summary>
        private void UpdatePortStatusLabels()
        {
            var connected = _lighting.ConnectedPorts;
            PortAStatusLabel = connected.Count >= 1
                ? $"PortA : {connected[0]} - Connected"
                : "PortA : -";
            PortBStatusLabel = connected.Count >= 2
                ? $"PortB : {connected[1]} - Connected"
                : "PortB : -";
        }

        /// <summary>
        /// ファセードの状態変化通知ハンドラ
        /// 概要：Setting画面でポート接続/切断が行われた際に発火する。
        /// UIスレッドでRefreshTransportStateを実行し、TopBarの表示を即時反映する。
        /// </summary>
        private void OnFacadeStatusChanged(object? sender, EventArgs e)
        {
            // 注意: Application は System.Windows.Application のこと。
            //       Lib.Application 名前空間との衝突を避けるため完全修飾で書く。
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                RefreshTransportState();
            });
        }
        /// <summary>
        /// エラートースト自動消去タイマーを起動する
        /// 概要：ErrorToastDurationSeconds秒経過後にエラーを自動消去する。
        /// 既存タイマーがあれば停止してから再起動する。
        /// </summary>
        private void StartErrorAutoDismissTimer()
        {
            if (_errorDismissTimer == null)
            {
                _errorDismissTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(ErrorToastDurationSeconds)
                };
                _errorDismissTimer.Tick += (s, e) =>
                {
                    _errorDismissTimer?.Stop();
                    _lighting.ClearError();
                };
            }
            _errorDismissTimer.Stop();
            _errorDismissTimer.Start();
        }

        /// <summary>
        /// EFFECT/Execute 系コマンド実行可否判定
        /// </summary>
        private bool CanExecuteSendCommand()
            => IsTransportConnected && !IsBusy;

        /// <summary>
        /// シーケンス01：ヘルパー関数
        /// </summary>
        /// <returns></returns>
        private bool CanExecuteSequence01() => IsSequence01Defined && !IsBusy;

        /// <summary>
        /// シーケンス02：ヘルパー関数
        /// </summary>
        /// <returns></returns>
        private bool CanExecuteSequence02() => IsSequence02Defined && !IsBusy;

        /// <summary>
        /// シーケンス03：ヘルパー関数
        /// </summary>
        /// <returns></returns>
        private bool CanExecuteSequence03() => IsSequence03Defined && !IsBusy;
        /// <summary>
        /// シーケンス04：ヘルパー関数
        /// </summary>
        /// <returns></returns>
        private bool CanExecuteSequence04() => IsSequence04Defined && !IsBusy;

        /// <summary>
        /// シーケンス05：ヘルパー関数
        /// </summary>
        /// <returns></returns>
        private bool CanExecuteSequence05() => IsSequence05Defined && !IsBusy;

        /// <summary>
        /// シーケンス06：ヘルパー関数
        /// </summary>
        /// <returns></returns>
        private bool CanExecuteSequence06() => IsSequence06Defined && !IsBusy;

        /// <summary>
        /// シーケンス07：ヘルパー関数
        /// </summary>
        /// <returns></returns>
        private bool CanExecuteSequence07() => IsSequence07Defined && !IsBusy;

        #endregion メソッド

        #region コマンド
        /// <summary>
        /// Preset画面表示コマンド
        /// 概要：現在の画面カテゴリをPresetに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowPreset() => CurrentCategory = UiCategory.Preset;

        /// <summary>
        /// Mode画面表示コマンド
        /// 概要：現在の画面カテゴリをModeに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowMode() => CurrentCategory = UiCategory.Mode;

        /// <summary>
        /// Animation画面表示コマンド
        /// 概要：現在の画面カテゴリをAnimationに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowAnimation() => CurrentCategory = UiCategory.Animation;

        /// <summary>
        /// Sequence画面表示コマンド
        /// 概要：現在の画面カテゴリをSequenceに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowSequence() => CurrentCategory = UiCategory.Sequence;

        /// <summary>
        /// Setting画面表示コマンド
        /// 概要：現在の画面カテゴリをSettingに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowSetting() => CurrentCategory = UiCategory.Setting;
        /// <summary>
        /// ALL選択コマンド
        /// 概要：現在の対象をALLに設定する。
        /// </summary>
        [RelayCommand]
        private void SelectTargetAll()
        {
            AppState.SelectedTarget = Target.All;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group01選択コマンド
        /// 概要：現在の対象をGroup01に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup01()
        {
            AppState.SelectedTarget = Target.Group01;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group02選択コマンド
        /// 概要：現在の対象をGroup02に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup02()
        {
            AppState.SelectedTarget = Target.Group02;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group03選択コマンド
        /// 概要：現在の対象をGroup03に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup03()
        {
            AppState.SelectedTarget = Target.Group03;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group04選択コマンド
        /// 概要：現在の対象をGroup04に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup04()
        {
            AppState.SelectedTarget = Target.Group04;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group05選択コマンド
        /// 概要：現在の対象をGroup05に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup05()
        {
            AppState.SelectedTarget = Target.Group05;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group06選択コマンド
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup06()
        {
            AppState.SelectedTarget = Target.Group06;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group07選択コマンド
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup07()
        {
            AppState.SelectedTarget = Target.Group07;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Group08選択コマンド
        /// </summary>
        [RelayCommand]
        private void SelectTargetGroup08()
        {
            AppState.SelectedTarget = Target.Group08;
            OnPropertyChanged(nameof(CurrentTargetLabel));
            RefreshTargetSelection();
            SyncPresetStateIfActive();
        }

        /// <summary>
        /// Flash実行コマンド
        /// 概要：現在対象に対してFlash操作を実行する。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExecuteSendCommand))]
        private async Task ExecuteFlash()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                RefreshTransportState();
                if (!IsTransportConnected)
                {
                    StatusMessage = "Flash skipped : transport disconnected";
                    return;
                }

                await _lighting.FlashAsync(
                    AppState.SelectedTarget,
                    AppState.SpeedValueMs,
                    AppState.SelectedColor);

                StatusMessage = $"Flash executed for {CurrentTargetLabel}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Flash failed: {ex.Message}";
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
                RefreshTransportState();
            }
        }

        /// <summary>
        /// FadeIn実行コマンド
        /// 概要：現在対象に対してFadeIn操作を実行する。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExecuteSendCommand))]
        private async Task ExecuteFadeIn()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                RefreshTransportState();
                if (!IsTransportConnected)
                {
                    StatusMessage = "FadeIn skipped : transport disconnected";
                    return;
                }

                await _lighting.FadeInAsync(
                    AppState.SelectedTarget,
                    AppState.SpeedValueMs,
                    AppState.SelectedColor);

                StatusMessage = $"FadeIn executed for {CurrentTargetLabel}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"FadeIn failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                RefreshTransportState();
            }
        }

        /// <summary>
        /// FadeOut実行コマンド
        /// 概要：現在対象に対してFadeOut操作を実行する。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExecuteSendCommand))]
        private async Task ExecuteFadeOut()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                RefreshTransportState();
                if (!IsTransportConnected)
                {
                    StatusMessage = "FadeOut skipped : transport disconnected";
                    return;
                }

                await _lighting.FadeOutAsync(
                    AppState.SelectedTarget,
                    AppState.SpeedValueMs,
                    AppState.SelectedColor);

                StatusMessage = $"FadeOut executed for {CurrentTargetLabel}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"FadeOut failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                RefreshTransportState();
            }
        }

        /// <summary>
        /// ストロボ停止トグルコマンド
        /// 概要：ストロボ停止状態をトグル切替する。
        /// </summary>
        [RelayCommand]
        private void ToggleStrobeOff()
        {
            IsStrobeOffActive = !IsStrobeOffActive;
        }


        /// <summary>
        /// 現在設定で送信コマンド
        /// 概要：現在の色・速度・対象・命令名・ポートで送信を実行する。
        /// 命令名が不正な場合はエラーメッセージを表示。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExecuteSendCommand))]
        private async Task ExecuteCurrentSettingsAsync()
        {
            if (IsBusy) return;
            if (!IsCommandNameValid)
            {
                StatusMessage = "命令名は32文字以内で入力してください。";
                return;
            }

            IsBusy = true;
            try
            {
                if (!_lighting.IsConnected)
                {
                    StatusMessage = "Execute skipped : transport disconnected";
                    return;
                }

                await _lighting.SetColorAsync(AppState.SelectedTarget, AppState.SelectedColor);
                StatusMessage =
                    $"Execute : {CurrentTargetLabel} / " +
                    $"R={AppState.SelectedColor.R},G={AppState.SelectedColor.G},B={AppState.SelectedColor.B} / " +
                    $"{AppState.SpeedValueMs}ms / Port={SelectedPort}" +
                    (string.IsNullOrEmpty(CommandName) ? "" : $" / name={CommandName}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Execute failed : {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// アプリ終了コマンド
        /// 概要：確認ダイアログを表示し、OK時にアプリケーションを終了する。
        /// </summary>
        [RelayCommand]
        private void PowerExit()
        {
            var result = MessageBox.Show(
                "シンクロライト制御アプリを終了しますか？",
                "終了確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                System.Windows.Application.Current?.Shutdown();
            }
        }

        /// <summary>
        /// エラートースト手動消去コマンド
        /// 概要：トーストの×ボタン押下時に、ファセードのエラーと自動消去タイマーをクリアする。
        /// </summary>
        [RelayCommand]
        private void DismissError()
        {
            _errorDismissTimer?.Stop();
            _lighting.ClearError();
        }

        /// <summary>
        /// Speed01適用コマンド
        /// 概要：速度プリセット1を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed01()
        {
            EditableSpeedValueMs = 250;
            StatusMessage = $"Speed01 applied : {CurrentSpeedLabel}";
        }

        /// <summary>
        /// Speed02適用コマンド
        /// 概要：速度プリセット2を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed02()
        {
            EditableSpeedValueMs = 500;
            StatusMessage = $"Speed02 applied : {CurrentSpeedLabel}";
        }

        /// <summary>
        /// Speed03適用コマンド
        /// 概要：速度プリセット3を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed03()
        {
            EditableSpeedValueMs = 1000;
            StatusMessage = $"Speed03 applied : {CurrentSpeedLabel}";
        }

        /// <summary>
        /// Speed04適用コマンド
        /// 概要：速度プリセット4を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed04()
        {
            EditableSpeedValueMs = 2000;
            StatusMessage = $"Speed04 applied : {CurrentSpeedLabel}";
        }

        /// <summary>
        /// Sequence01実行コマンド
        /// 概要：Sequence01ボタン押下時に状態表示を更新する。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExecuteSequence01))]
        private async Task ExecuteSequence01()
        {
            if (IsBusy) return;
            if (!IsSequence01Defined)
            {
                StatusMessage = "Sequence01 is undefined";
                return;
            }
            IsBusy = true;
            try
            {
                await RunSequenceWithDialogAsync(1, "Sequence01");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Sequence02実行コマンド
        /// 概要：Sequence02ボタン押下時に状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteSequence02()
        {
            if (IsBusy) return;
            if (!IsSequence02Defined)
            {
                StatusMessage = "Sequence02 is undefined";
                return;
            }
            IsBusy = true;
            try
            {
                await RunSequenceWithDialogAsync(2, "Sequence02");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Sequence03実行コマンド
        /// 概要：Sequence03ボタン押下時に状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteSequence03()
        {
            if (IsBusy) return;
            if (!IsSequence03Defined)
            {
                StatusMessage = "Sequence03 is undefined";
                return;
            }
            IsBusy = true;
            try
            {
                await RunSequenceWithDialogAsync(3, "Sequence03");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Sequence04実行コマンド
        /// </summary>
        [RelayCommand]
        private async Task ExecuteSequence04()
        {
            if (IsBusy) return;
            if (!IsSequence04Defined)
            {
                StatusMessage = "Sequence04 is undefined";
                return;
            }
            IsBusy = true;
            try
            {
                await RunSequenceWithDialogAsync(4, "Sequence04");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Sequence05実行コマンド
        /// </summary>
        [RelayCommand]
        private async Task ExecuteSequence05()
        {
            if (IsBusy) return;
            if (!IsSequence05Defined)
            {
                StatusMessage = "Sequence05 is undefined";
                return;
            }
            IsBusy = true;
            try
            {
                await RunSequenceWithDialogAsync(5, "Sequence05");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Sequence06実行コマンド
        /// </summary>
        [RelayCommand]
        private async Task ExecuteSequence06()
        {
            if (IsBusy) return;
            if (!IsSequence06Defined)
            {
                StatusMessage = "Sequence06 is undefined";
                return;
            }
            IsBusy = true;
            try
            {
                await RunSequenceWithDialogAsync(6, "Sequence06");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Sequence07実行コマンド
        /// </summary>
        [RelayCommand]
        private async Task ExecuteSequence07()
        {
            if (IsBusy) return;
            if (!IsSequence07Defined)
            {
                StatusMessage = "Sequence07 is undefined";
                return;
            }
            IsBusy = true;
            try
            {
                await RunSequenceWithDialogAsync(7, "Sequence07");
            }
            finally
            {
                IsBusy = false;
            }
        }

    /// <summary>
    /// Stops the current sequence and updates the status message to indicate that the sequence has been stopped.
    /// </summary>
        [RelayCommand]
        private void StopSequence()
        {
            StatusMessage = "Sequence stopped";
        }
        #endregion コマンド

    }
}
