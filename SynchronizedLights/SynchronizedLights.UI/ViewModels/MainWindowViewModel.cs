using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.UseCases;
using Lib.Domain.Enums;
using Lib.Domain.States;
using Lib.Domain.ValueObjects;
using Lib.Protocol.Builders;
using Lib.Transport.Transports;
using Lib.Ui.Screens.ViewModels;

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

            UpdateCurrentViewModel();
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
        }
        #endregion OnCurrentCategoryChanged

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
                UiCategory.Animation => new AnimationViewModel(),
                UiCategory.Sequence => new SequenceViewModel(),
                UiCategory.Setting => new SettingViewModel(),
                _ => CreatePresetViewModel()
            };
        }
        #endregion UpdateCurrentViewModel

        #region メソッド
        /// <summary>
        /// Preset画面用ViewModelを生成する。
        /// 概要：PresetViewModel生成時に色変更イベントを購読し、
        /// AppState.SelectedColor と同期する。
        /// </summary>
        private PresetViewModel CreatePresetViewModel()
        {
            var vm = new PresetViewModel(_presetUseCase, _transport);
            vm.ColorChanged += OnPresetColorChanged;
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
        }
        #endregion メソッド

        #region コマンド
        /// <summary>
        /// Preset画面表示コマンド
        /// 概要：現在の画面カテゴリをPresetに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowPreset()
        {
            CurrentCategory = UiCategory.Preset;
        }

        /// <summary>
        /// Mode画面表示コマンド
        /// 概要：現在の画面カテゴリをModeに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowMode()
        {
            CurrentCategory = UiCategory.Mode;
        }

        /// <summary>
        /// Animation画面表示コマンド
        /// 概要：現在の画面カテゴリをAnimationに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowAnimation()
        {
            CurrentCategory = UiCategory.Animation;
        }

        /// <summary>
        /// Sequence画面表示コマンド
        /// 概要：現在の画面カテゴリをSequenceに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowSequence()
        {
            CurrentCategory = UiCategory.Sequence;
        }

        /// <summary>
        /// Setting画面表示コマンド
        /// 概要：現在の画面カテゴリをSettingに切り替える。
        /// </summary>
        [RelayCommand]
        private void ShowSetting()
        {
            CurrentCategory = UiCategory.Setting;
        }
        /// <summary>
        /// ALL選択コマンド
        /// 概要：現在の対象をALLに設定する。
        /// </summary>
        [RelayCommand]
        private void SelectTargetAll()
        {
            AppState.SelectedTarget = Target.All;
            OnPropertyChanged(nameof(CurrentTargetLabel));
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
        }
        /// <summary>
        /// Flash実行コマンド
        /// 概要：現在対象に対してFlash操作を実行する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteFlash()
        {
            try
            {
                await _presetUseCase.ExecuteFlashAsync(
                    AppState.SelectedTarget,
                    AppState.SelectedColor,
                    AppState.SpeedValueMs);

                StatusMessage = $"Flash executed for {CurrentTargetLabel}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Flash failed: {ex.Message}";
            }
        }
        /// <summary>
        /// FadeIn実行コマンド
        /// 概要：現在対象に対してFadeIn操作を実行する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteFadeIn()
        {
            try
            {
                await _presetUseCase.ExecuteFadeInAsync(
                    AppState.SelectedTarget,
                    AppState.SelectedColor,
                    AppState.SpeedValueMs);

                StatusMessage = $"FadeIn executed for {CurrentTargetLabel}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"FadeIn failed: {ex.Message}";
            }
        }
        /// <summary>
        /// FadeOut実行コマンド
        /// 概要：現在対象に対してFadeOut操作を実行する。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteFadeOut()
        {
            try
            {
                await _presetUseCase.ExecuteFadeOutAsync(
                    AppState.SelectedTarget,
                    AppState.SelectedColor,
                    AppState.SpeedValueMs);

                StatusMessage = $"FadeOut executed for {CurrentTargetLabel}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"FadeOut failed: {ex.Message}";
            }
        }
        /// <summary>
        /// StrobeOff実行コマンド
        /// 概要：現在対象に対してStrobeOff操作を実行する。
        /// </summary>
        [RelayCommand]
        private void ExecuteStrobeOff()
        {
            StatusMessage = $"StrobeOff executed for {CurrentTargetLabel}";
        }
        /// <summary>
        /// Speed01適用コマンド
        /// 概要：速度プリセット1を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed01()
        {
            AppState.SpeedValueMs = 250;
            StatusMessage = $"Speed01 applied : {CurrentSpeedLabel}";
            OnPropertyChanged(nameof(CurrentSpeedLabel));
        }

        /// <summary>
        /// Speed02適用コマンド
        /// 概要：速度プリセット2を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed02()
        {
            AppState.SpeedValueMs = 500;
            StatusMessage = $"Speed02 applied : {CurrentSpeedLabel}";
            OnPropertyChanged(nameof(CurrentSpeedLabel));
        }

        /// <summary>
        /// Speed03適用コマンド
        /// 概要：速度プリセット3を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed03()
        {
            AppState.SpeedValueMs = 1000;
            StatusMessage = $"Speed03 applied : {CurrentSpeedLabel}";
            OnPropertyChanged(nameof(CurrentSpeedLabel));
        }

        /// <summary>
        /// Speed04適用コマンド
        /// 概要：速度プリセット4を適用し、現在速度を更新する。
        /// </summary>
        [RelayCommand]
        private void ApplySpeed04()
        {
            AppState.SpeedValueMs = 2000;
            StatusMessage = $"Speed04 applied : {CurrentSpeedLabel}";
            OnPropertyChanged(nameof(CurrentSpeedLabel));
        }
        #endregion コマンド

    }
}