using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Setting画面用ViewModel
    /// 概要：COMポート管理・送信機初期設定・接続状態表示を管理する。
    /// </summary>
    public partial class SettingViewModel : ObservableObject
    {
        #region フィールド

        /// <summary>
        /// ライティング制御ファセード
        /// 概要：UIと通信層の境界。Dummy/Real APIを差し替えるための統一インターフェース。
        /// </summary>
        private readonly ILightingFacade _lighting;

        #endregion フィールド

        #region プロパティ

        /// <summary>
        /// COMポート一覧
        /// 概要：利用可能なポートのViewModelコレクション。
        /// 各ポートの選択状態・接続状態を保持する。
        /// </summary>
        public ObservableCollection<PortItemViewModel> Ports { get; } = new();

        /// <summary>
        /// 操作中かどうか
        /// 概要：非同期コマンド実行中のボタン二重押し防止フラグ。
        /// </summary>
        [ObservableProperty]
        private bool isBusy;

        /// <summary>
        /// ポート状態表示文字列
        /// 概要：Connected / Disconnected を表示する。
        /// </summary>
        [ObservableProperty]
        private string portStatusText = "Port : Unknown";

        /// <summary>
        /// 接続中ポート数表示文字列
        /// 概要：現在接続されているポート件数を表示する。
        /// </summary>
        [ObservableProperty]
        private string portCountText = "Connected Ports : 0";

        /// <summary>
        /// キュー状態表示文字列
        /// 概要：現在の送信キュー件数を表示する。
        /// </summary>
        [ObservableProperty]
        private string queueStatusText = "Queue : 0";

        /// <summary>
        /// 最終エラー表示文字列
        /// 概要：直近のエラー内容を表示する。
        /// </summary>
        [ObservableProperty]
        private string errorStatusText = "Last Error : -";

        /// <summary>
        /// PortA状態表示文字列
        /// 接続順で先頭のポートをPortAとして表示する。
        /// </summary>
        [ObservableProperty]
        private string portAStatusLabel = "PortA : -";

        /// <summary>
        /// PortB状態表示文字列
        /// 接続順で2番目のポートをPortBとして表示する。
        /// </summary>
        [ObservableProperty]
        private string portBStatusLabel = "PortB : -";

        /// <summary>
        /// Setting画面のステータスメッセージ
        /// 概要：直近の操作結果を表示する。
        /// </summary>
        [ObservableProperty]
        private string settingStatusMessage = "準備完了";

        /// <summary>
        /// 送信機チャネル設定値
        /// 概要：送信機初期化コマンド（FA）で送信するチャネル番号。
        /// </summary>
        [ObservableProperty]
        private byte channelValue = 3;

        /// <summary>
        /// 送信機送信電力設定値
        /// 概要：送信機初期化コマンド（FB）で送信する送信電力値。
        /// </summary>
        [ObservableProperty]
        private byte powerValue = 10;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// Setting画面用ViewModelを生成する。
        /// 概要：ILightingFacadeを受け取り、状態変化イベントを購読し、
        /// 初期ポート一覧を非同期で取得する。
        /// </summary>
        public SettingViewModel(ILightingFacade lighting)
        {
            _lighting = lighting;
            _lighting.StatusChanged += OnFacadeStatusChanged;

            // 初期ポート読み込み（UI表示ブロックしない）
            _ = RefreshPortsAsync();
            LoadStatus();
        }

        #endregion コンストラクタ

        #region コマンド

        /// <summary>
        /// 状態再読込コマンド
        /// 概要：ファセードから最新状態を再取得して画面表示を更新する。
        /// </summary>
        [RelayCommand]
        private void RefreshStatus()
        {
            LoadStatus();
        }

        /// <summary>
        /// ポート一覧更新コマンド
        /// 利用可能なCOMポートを再取得し、一覧を更新する。
        /// 選択状態は可能な限り維持する。
        /// </summary>
        [RelayCommand]
        private async Task RefreshPortsAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                var available = await _lighting.GetAvailablePortsAsync();

                // 既存の選択状態を保持しつつ一覧更新
                var previousSelections = Ports
                    .Where(p => p.IsSelected)
                    .Select(p => p.PortName)
                    .ToHashSet();

                Ports.Clear();
                foreach (var name in available)
                {
                    var item = new PortItemViewModel(name)
                    {
                        IsSelected = previousSelections.Contains(name),
                        IsConnected = _lighting.ConnectedPorts.Contains(name)
                    };
                    Ports.Add(item);
                }

                SettingStatusMessage = $"ポート一覧更新（{Ports.Count}件）";
                LoadStatus();
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"ポート一覧取得失敗: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 接続コマンド
        /// 選択中のポートを一括で接続する。
        /// </summary>
        [RelayCommand]
        private async Task ConnectAsync()
        {
            if (IsBusy) return;
            var selected = Ports.Where(p => p.IsSelected).Select(p => p.PortName).ToList();
            if (selected.Count == 0)
            {
                SettingStatusMessage = "接続対象のポートが選択されていません";
                return;
            }

            IsBusy = true;
            try
            {
                await _lighting.ConnectAsync(selected);
                SettingStatusMessage = $"接続完了: {string.Join(", ", selected)}";
                UpdatePortConnectionFlags();
                LoadStatus();
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"接続失敗: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 切断コマンド
        /// 概要：接続中の全ポートを閉じる。
        /// </summary>
        [RelayCommand]
        private async Task DisconnectAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await _lighting.DisconnectAsync();
                SettingStatusMessage = "全ポート切断完了";
                UpdatePortConnectionFlags();
                LoadStatus();
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"切断失敗: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 送信機初期化コマンド
        /// 概要：FA（チャネル）/FB（電力）を送信機に送信する。
        /// ポート未接続の場合はエラーメッセージを表示する。
        /// </summary>
        [RelayCommand]
        private async Task InitializeTransmitterAsync()
        {
            if (IsBusy) return;

            IsBusy = true;
            try
            {
                await _lighting.InitializeTransmitterAsync(ChannelValue, PowerValue);
                SettingStatusMessage = $"送信機初期化完了 (ch={ChannelValue}, pwr={PowerValue})";
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"送信機初期化失敗: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        #endregion コマンド

        #region メソッド

        /// <summary>
        /// 通信状態を読込む
        /// 概要：ファセードから状態を取得し、画面表示用プロパティへ反映する。
        /// PortA/PortBは接続順で先頭2つを割り当てる。
        /// </summary>
        private void LoadStatus()
        {
            var connected = _lighting.ConnectedPorts;

            PortStatusText = _lighting.IsConnected
                ? "Port : Connected"
                : "Port : Disconnected";
            PortCountText = $"Connected Ports : {connected.Count}";
            QueueStatusText = $"Queue : {_lighting.QueueLength}";
            ErrorStatusText = $"Last Error : {_lighting.LastError ?? "-"}";

            // PortA/PortB
            PortAStatusLabel = connected.Count >= 1
                ? $"PortA : {connected[0]} - Connected"
                : "PortA : -";
            PortBStatusLabel = connected.Count >= 2
                ? $"PortB : {connected[1]} - Connected"
                : "PortB : -";
        }

        /// <summary>
        /// 各ポートの接続フラグを、ファセードの状態に合わせて更新する。
        /// 概要：Ports コレクション内の各PortItemViewModelのIsConnectedを更新する。
        /// </summary>
        private void UpdatePortConnectionFlags()
        {
            var connected = _lighting.ConnectedPorts;
            foreach (var port in Ports)
            {
                port.IsConnected = connected.Contains(port.PortName);
            }
        }

        /// <summary>
        /// ファセードの状態変化通知
        /// 概要：接続ポート・キュー長・エラーが変化した際に発火する。
        /// UIスレッドで画面表示を更新する。
        /// </summary>
        private void OnFacadeStatusChanged(object? sender, EventArgs e)
        {

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdatePortConnectionFlags();
                LoadStatus();
            });
        }

        #endregion メソッド
    }
}