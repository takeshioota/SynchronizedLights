using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.UseCases;
using Lib.Domain.Enums;
using Lib.Domain.States;
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
                UiCategory.Preset => new PresetViewModel(_presetUseCase, _transport),
                UiCategory.Mode => new ModeViewModel(),
                UiCategory.Animation => new AnimationViewModel(),
                UiCategory.Sequence => new SequenceViewModel(),
                UiCategory.Setting => new SettingViewModel(),
                _ => new PresetViewModel(_presetUseCase, _transport)
            };
        }
        #endregion UpdateCurrentViewModel

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
        #endregion コマンド

    }
}