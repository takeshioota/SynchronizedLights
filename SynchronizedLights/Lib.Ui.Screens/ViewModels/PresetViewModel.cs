using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Lib.Transport.Interfaces;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Preset画面用ViewModel
    /// 概要：Preset画面における基本操作（色変更、点灯、消灯）の状態管理と
    /// コマンド実行を担当するViewModel。
    /// UseCaseを通じて制御コマンド送信を行い、画面表示用の状態も保持する。
    /// </summary>
    public partial class PresetViewModel : ObservableObject
    {
        #region フィールド
        /// <summary>
        /// Preset制御UseCase
        /// 概要：色変更、点灯、消灯などのPreset操作を実行するためのUseCase。
        /// </summary>
        private readonly IPresetUseCase _presetUseCase;

        /// <summary>
        /// 送信処理インターフェース
        /// 概要：送信状態やキュー件数を取得するために使用するTransport。
        /// </summary>
        private readonly ITransport _transport;
        #endregion フィールド

        #region プロパティ
        /// <summary>
        /// 状態表示文字列
        /// 概要：直近の操作結果や画面状態をユーザーへ表示するためのメッセージ。
        /// </summary>
        [ObservableProperty]
        private string statusText = "Preset ready";

        /// <summary>
        /// キュー表示文字列
        /// 概要：送信キュー件数を画面上に表示するための文字列。
        /// </summary>
        [ObservableProperty]
        private string queueText = "Queue: 0";

        /// <summary>
        /// 現在色
        /// 概要：Preset画面で現在選択中の色を保持する。
        /// </summary>
        [ObservableProperty]
        private Rgb currentColor = new(255, 255, 255);

        /// <summary>
        /// 現在色表示文字列
        /// 概要：画面に表示する現在色の文字列表現を返す。
        /// </summary>
        public string CurrentColorLabel => $"Color : R={CurrentColor.R}, G={CurrentColor.G}, B={CurrentColor.B}";

        /// <summary>
        /// 色変更通知イベント
        /// 概要：Preset画面で選択した色を親画面へ通知する。
        /// </summary>
        public event Action<Rgb>? ColorChanged;
        #endregion プロパティ

        #region コンストラクタ
        /// <summary>
        /// Preset画面用ViewModelを生成する。
        /// 概要：UseCaseとTransportの依存を受け取り初期化する。
        /// </summary>
        public PresetViewModel(IPresetUseCase presetUseCase, ITransport transport)
        {
            _presetUseCase = presetUseCase;
            _transport = transport;
            RefreshStatus();
        }

        partial void OnCurrentColorChanged(Rgb value)
        {
            OnPropertyChanged(nameof(CurrentColorLabel));
            ColorChanged?.Invoke(value);
        }
        #endregion コンストラクタ

        #region コマンド
        /// <summary>
        /// 赤色選択コマンド
        /// 概要：現在色を赤に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectRed()
        {
            CurrentColor = new Rgb(255, 0, 0);
        }

        /// <summary>
        /// 緑色選択コマンド
        /// 概要：現在色を緑に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectGreen()
        {
            CurrentColor = new Rgb(0, 255, 0);
        }

        /// <summary>
        /// 青色選択コマンド
        /// 概要：現在色を青に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectBlue()
        {
            CurrentColor = new Rgb(0, 0, 255);
        }

        /// <summary>
        /// 白色選択コマンド
        /// 概要：現在色を白に設定する。
        /// </summary>
        [RelayCommand]
        private void SelectWhite()
        {
            CurrentColor = new Rgb(255, 255, 255);
        }
        /// <summary>
        /// 点灯コマンド
        /// 概要：全体ターゲットに対して点灯操作を実行し、状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private async Task TurnOnAsync()
        {
            await _presetUseCase.TurnOnAsync(Target.All, Rgb.White);
            RefreshStatus("TurnOn sent");
        }

        /// <summary>
        /// 消灯コマンド
        /// 概要：全体ターゲットに対して消灯操作を実行し、状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private async Task TurnOffAsync()
        {
            await _presetUseCase.TurnOffAsync(Target.All);
            RefreshStatus("TurnOff sent");
        }
        #endregion コマンド

        #region メソッド
        /// <summary>
        /// 状態表示を更新する
        /// 概要：Transportから現在のキュー件数を取得し、
        /// 状態メッセージとキュー表示を最新化する。
        /// </summary>
        private void RefreshStatus(string? message = null)
        {
            var status = _transport.GetStatus();
            StatusText = message ?? "Preset ready";
            QueueText = $"Queue: {status.QueueLength}";
        }
        #endregion メソッド
    }
}
