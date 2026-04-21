using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Facades;
using Lib.Application.Interfaces;
using Lib.Application.UseCases;
using Lib.Domain.Enums;
using Lib.Domain.States;
using Lib.Domain.ValueObjects;
using Lib.Protocol.Builders;
using Lib.Transport.Interfaces;
using Lib.Transport.Transports;
using Lib.Ui.Screens.ViewModels;
using Lib.Ui.Screens.Views;

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
        /// ダミーコマンド生成処理
        /// 概要：Preset機能の試験用として使用するコマンド生成クラス。
        /// </summary>
        private readonly DummyCommandBuilder _commandBuilder;

        /// <summary>
        /// ダミー送信処理
        /// 概要：Preset機能の試験用として使用する送信処理クラス。
        /// </summary>
        private readonly DummyTransport _transport;

        /// <summary>
        /// Preset制御UseCase
        /// 概要：Preset画面からの基本操作（色変更、点灯、消灯）を実行するUseCase。
        /// </summary>
        private readonly PresetUseCase _presetUseCase;

        /// <summary>
        /// ライティング制御ファセード
        /// 概要：UIと通信層の境界。現時点はDummy実装。
        /// SynchrolightAPI.Core連携の実装に差し替える。
        /// </summary>
        private readonly ILightingFacade _lighting = new DummyLightingFacade();
        /// <summary>
        /// エラートースト自動消去タイマー（STEP6-B）
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
                }
            }
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
        private bool isTransportConnected;

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
        private bool isSequence01Defined = true;

        /// <summary>
        /// Sequence02が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        private bool isSequence02Defined = true;

        /// <summary>
        /// Sequence03が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        private bool isSequence03Defined = false;

        /// <summary>
        /// Sequence04が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        private bool isSequence04Defined = false;
        /// <summary>
        /// Sequence05が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        private bool isSequence05Defined = false;

        /// <summary>
        /// Sequence06が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        private bool isSequence06Defined = false;

        /// <summary>
        /// Sequence07が定義済みかどうか
        /// </summary>
        [ObservableProperty]
        private bool isSequence07Defined = false;
        #endregion プロパティ

        #region コンストラクタ
        /// <summary>
        /// MainWindowViewModelを生成する
        /// 概要：画面切替に必要なUseCaseやダミー通信機能を初期化し、
        /// 初期カテゴリに応じた画面ViewModelを設定する。
        /// </summary>
        public MainWindowViewModel()
        {
            _commandBuilder = new DummyCommandBuilder();
            _transport = new DummyTransport();
            _presetUseCase = new PresetUseCase(_commandBuilder, _transport);
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
        /// ErrorMessage変更時の処理（STEP6-B）
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
                UiCategory.Setting => new SettingViewModel(_lighting),
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
            var viewModel = new PresetViewModel(_presetUseCase, _transport);
            viewModel.ColorChanged += OnPresetColorChanged;
            viewModel.ApplyState(AppState.SelectedTarget, AppState.SelectedColor);
            return viewModel;
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
        /// エラートースト自動消去タイマーを起動する（STEP6-B）
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
        /// Flash実行コマンド
        /// 概要：現在対象に対してFlash操作を実行する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteFlash()
        {
            RefreshTransportState();

            if (!IsTransportConnected)
            {
                StatusMessage = "Flash skipped : transport disconnected";
                return;
            }

            try
            {
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

            RefreshTransportState();
        }

        /// <summary>
        /// FadeIn実行コマンド
        /// 概要：現在対象に対してFadeIn操作を実行する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteFadeIn()
        {
            RefreshTransportState();

            if (!IsTransportConnected)
            {
                StatusMessage = "FadeIn skipped : transport disconnected";
                return;
            }

            try
            {
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
            RefreshTransportState();
        }

        /// <summary>
        /// FadeOut実行コマンド
        /// 概要：現在対象に対してFadeOut操作を実行する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteFadeOut()
        {
            RefreshTransportState();

            if (!IsTransportConnected)
            {
                StatusMessage = "FadeOut skipped : transport disconnected";
                return;
            }

            try
            {
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

            RefreshTransportState();
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
        /// エラートースト手動消去コマンド（STEP6-B）
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
        [RelayCommand]
        private void ExecuteSequence01()
        {
            if (!IsSequence01Defined) { StatusMessage = "Sequence01 is undefined"; return; }
            StatusMessage = "Sequence executed : Sequence01";
        }

        /// <summary>
        /// Sequence02実行コマンド
        /// 概要：Sequence02ボタン押下時に状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private void ExecuteSequence02()
        {
            if (!IsSequence02Defined) { StatusMessage = "Sequence02 is undefined"; return; }
            StatusMessage = "Sequence executed : Sequence02";
        }

        /// <summary>
        /// Sequence03実行コマンド
        /// 概要：Sequence03ボタン押下時に状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private void ExecuteSequence03()
        {
            if (!IsSequence03Defined) { StatusMessage = "Sequence03 is undefined"; return; }
            StatusMessage = "Sequence executed : Sequence03";
        }

        [RelayCommand]
        private void ExecuteSequence04()
        {
            if (!IsSequence04Defined) { StatusMessage = "Sequence04 is undefined"; return; }
            StatusMessage = "Sequence executed : Sequence04";
        }

        /// <summary>
        /// Sequence05実行コマンド
        /// </summary>

        [RelayCommand]
        private void ExecuteSequence05()
        {
            if (!IsSequence05Defined) { StatusMessage = "Sequence05 is undefined"; return; }
            StatusMessage = "Sequence executed : Sequence05";
        }

        /// <summary>
        /// Sequence06実行コマンド
        /// </summary>
        [RelayCommand]
        private void ExecuteSequence06()
        {
            if (!IsSequence06Defined) { StatusMessage = "Sequence06 is undefined"; return; }
            StatusMessage = "Sequence executed : Sequence06";
        }

        /// <summary>
        /// Sequence07実行コマンド
        /// </summary>
        [RelayCommand]
        private void ExecuteSequence07()
        {
            if (!IsSequence07Defined) { StatusMessage = "Sequence07 is undefined"; return; }
            StatusMessage = "Sequence executed : Sequence07";
        }

        [RelayCommand]
        private void StopSequence()
        {
            StatusMessage = "Sequence stopped";
        }
        #endregion コマンド

    }
}
