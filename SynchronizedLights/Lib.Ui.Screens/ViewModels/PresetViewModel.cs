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
        #endregion プロパティ

        #region コンストラクタ
        /// <summary>
        /// PresetViewModelを生成する
        /// 概要：Preset操作用UseCaseとTransportを受け取り、
        /// 初期状態の表示内容を設定する。
        /// </summary>
        public PresetViewModel(IPresetUseCase presetUseCase, ITransport transport)
        {
            _presetUseCase = presetUseCase;
            _transport = transport;
            RefreshStatus();
        }
        #endregion コンストラクタ

        #region コマンド
        /// <summary>
        /// 赤色設定コマンド
        /// 概要：全体ターゲットに対して赤色変更を実行し、状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private async Task SetRedAsync()
        {
            await _presetUseCase.SetColorAsync(Target.All, Rgb.Red);
            RefreshStatus("Red sent");
        }

        /// <summary>
        /// 緑色設定コマンド
        /// 概要：全体ターゲットに対して緑色変更を実行し、状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private async Task SetGreenAsync()
        {
            await _presetUseCase.SetColorAsync(Target.All, Rgb.Green);
            RefreshStatus("Green sent");
        }

        /// <summary>
        /// 青色設定コマンド
        /// 概要：全体ターゲットに対して青色変更を実行し、状態表示を更新する。
        /// </summary>
        [RelayCommand]
        private async Task SetBlueAsync()
        {
            await _presetUseCase.SetColorAsync(Target.All, Rgb.Blue);
            RefreshStatus("Blue sent");
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
