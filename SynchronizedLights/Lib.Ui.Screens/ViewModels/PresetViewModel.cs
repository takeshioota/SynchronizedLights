using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Preset画面用ViewModel
    /// 概要：Preset画面における基本操作（色変更、点灯、消灯）の状態管理と
    /// コマンド実行を担当するViewModel。
    /// ILightingFacade 経由でファセードを呼び出し、
    /// 画面表示用の状態も保持する。
    /// </summary>
    public partial class PresetViewModel : ObservableObject
    {
        #region フィールド
        /// <summary>
        /// ライティング制御ファセード
        /// 概要：UIと通信層の境界。Dummy / Real API を差し替えるための統一インターフェース。
        /// </summary>
        private readonly ILightingFacade _lighting;

        #endregion フィールド

        #region プロパティ

        /// <summary>
        /// 送信実行中フラグ
        /// 概要：Turn On / Turn Off の実行中 true になり、ボタンを無効化
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TurnOnCommand))]
        [NotifyCanExecuteChangedFor(nameof(TurnOffCommand))]
        private bool isBusy;

        /// <summary>
        /// Transport 接続状態
        /// 概要：ILightingFacade.IsConnected を追跡し、
        /// 未接続時は TurnOn/TurnOff/Execute 系ボタンを無効化する。
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TurnOnCommand))]
        [NotifyCanExecuteChangedFor(nameof(TurnOffCommand))]
        private bool isTransportConnected;

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
        /// 現在対象
        /// 概要：Preset画面で現在選択中の対象を保持する。
        /// MainWindow 側のAppStateと同期して利用する。
        /// </summary>
        [ObservableProperty]
        private Target selectedTarget = Target.All;

        /// <summary>
        /// 現在色表示文字列
        /// 概要：画面に表示する現在色の文字列表現を返す。
        /// </summary>
        public string CurrentColorLabel => $"Color : R={CurrentColor.R}, G={CurrentColor.G}, B={CurrentColor.B}";

        /// <summary>
        /// 赤選択中かどうか
        /// 概要：CurrentColor が (255,0,0) のとき true。ボタンの選択ハイライトに使用。
        /// </summary>
        public bool IsColorRedSelected
            => CurrentColor.R == 255 && CurrentColor.G == 0 && CurrentColor.B == 0;

        /// <summary>
        /// 緑選択中かどうか
        /// </summary>
        public bool IsColorGreenSelected
            => CurrentColor.R == 0 && CurrentColor.G == 255 && CurrentColor.B == 0;

        /// <summary>
        /// 青選択中かどうか
        /// </summary>
        public bool IsColorBlueSelected
            => CurrentColor.R == 0 && CurrentColor.G == 0 && CurrentColor.B == 255;

        /// <summary>
        /// 白選択中かどうか
        /// </summary>
        public bool IsColorWhiteSelected
            => CurrentColor.R == 255 && CurrentColor.G == 255 && CurrentColor.B == 255;

        /// <summary>
        /// カスタム色選択中かどうか（固定 5 色以外）
        /// 概要：Red/Green/Blue/White のいずれでもない色が選択されているとき true。
        /// ユーザーが DlgColorPicker で選んだ任意色のとき Custom ボタンが光る。
        /// </summary>
        public bool IsColorCustomSelected
            => !IsColorRedSelected
            && !IsColorGreenSelected
            && !IsColorBlueSelected
            && !IsColorWhiteSelected;

        /// <summary>
        /// 色変更通知イベント
        /// 概要：Preset画面で選択した色を親画面へ通知する。
        /// </summary>
        public event Action<Rgb>? ColorChanged;
        #endregion プロパティ

        #region コンストラクタ
        /// <summary>
        /// Preset画面用ViewModelを生成する。
        /// 概要：ILightingFacadeを受け取り初期化する。
        /// </summary>
        public PresetViewModel(ILightingFacade lighting)
        {
            _lighting = lighting;
            _lighting.StatusChanged += OnFacadeStatusChanged;

            // 初期状態反映
            IsTransportConnected = _lighting.IsConnected;
            RefreshStatus();
        }

        /// <summary>
        /// ファセードの状態変化通知ハンドラ
        /// 概要：接続/切断・キュー長変化・エラー発生時に発火する。
        /// UIスレッドで IsTransportConnected と QueueText を更新する。
        /// </summary>
        private void OnFacadeStatusChanged(object? sender, EventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsTransportConnected = _lighting.IsConnected;
                QueueText = $"Queue: {_lighting.QueueLength}";
            });
        }

        /// <summary>
        /// 現在色変更時処理
        /// 概要：現在色表示文字列を再描画し、親画面へ色変更を通知する。
        /// </summary>
        partial void OnCurrentColorChanged(Rgb value)
        {
            OnPropertyChanged(nameof(CurrentColorLabel));
            OnPropertyChanged(nameof(IsColorRedSelected));
            OnPropertyChanged(nameof(IsColorGreenSelected));
            OnPropertyChanged(nameof(IsColorBlueSelected));
            OnPropertyChanged(nameof(IsColorWhiteSelected));
            OnPropertyChanged(nameof(IsColorCustomSelected));
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
        /// 概要：現在選択中の対象と色を使用して点灯操作を実行し、状態表示を更新する。
        /// ILightingFacade.SetColorAsync 経由で送信。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExecuteTurnCommand))]
        private async Task TurnOnAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await _lighting.SetColorAsync(SelectedTarget, CurrentColor);
                RefreshStatus($"TurnOn sent : {SelectedTarget} / {CurrentColorLabel}");
            }
            catch (Exception ex)
            {
                RefreshStatus($"TurnOn failed : {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 消灯コマンド
        /// 概要：現在選択中の対象に対して黒(0,0,0)色を設定することで消灯する。
        /// ILightingFacade.SetColorAsync 経由で送信。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExecuteTurnCommand))]
        private async Task TurnOffAsync()
        {
            if (IsBusy) return;

            // ALL 対象での消灯は会場ブラックアウトを招くので確認
            if (SelectedTarget == Target.All)
            {
                var result = System.Windows.MessageBox.Show(
                    "全ての端末を消灯します（会場全体がブラックアウトします）。\n本番中の場合は演出に影響します。\n\n実行してよろしいですか？",
                    "全体消灯確認 / Turn Off All Confirmation",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No);  // 既定は No（誤タップ防止）

                if (result != System.Windows.MessageBoxResult.Yes)
                {
                    RefreshStatus("全体消灯をキャンセルしました");
                    return;
                }
            }

            IsBusy = true;
            try
            {
                await _lighting.SetColorAsync(SelectedTarget, new Rgb(0, 0, 0));
                RefreshStatus($"TurnOff sent : {SelectedTarget}");
            }
            catch (Exception ex)
            {
                RefreshStatus($"TurnOff failed : {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
        #endregion コマンド

        #region メソッド

        /// <summary>
        /// TurnOn / TurnOff 実行可否
        /// 概要：未接続時 + 送信中は false を返し、ボタンを無効化する。
        /// </summary>
        private bool CanExecuteTurnCommand() => IsTransportConnected && !IsBusy;

        /// <summary>
        /// 状態表示を更新する
        /// 概要：ファセードから現在のキュー件数を取得し、
        /// 状態メッセージとキュー表示を最新化する。
        /// </summary>
        private void RefreshStatus(string? message = null)
        {
            StatusText = message ?? "Preset ready";
            QueueText = $"Queue: {_lighting.QueueLength}";
        }

        /// <summary>
        /// 外部状態をPreset画面へ反映する
        /// 概要：MainWindow 側で保持している対象・色の状態をPreset画面へ同期する。
        /// </summary>
        public void ApplyState(Target target, Rgb color)
        {
            SelectedTarget = target;
            CurrentColor = color;
        }
        #endregion メソッド
    }
}