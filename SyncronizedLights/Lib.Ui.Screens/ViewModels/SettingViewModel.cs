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

        #region イベント
        /// <summary>
        /// 接続成功時に発火するイベント
        /// </summary>
        public event EventHandler? ConnectedSuccessfully;

        /// <summary>
        /// 送信機初期化成功時に発火するイベント
        /// </summary>
        public event EventHandler? TransmitterInitialized;

        /// <summary>
        /// 切断成功時に発火するイベント（Init 状態リセット等に使用）
        /// </summary>
        public event EventHandler? Disconnected;

        /// <summary>
        /// コマンドログへの出力要求（type, message）。
        /// 送信機初期化などの操作を、統合ウィンドウ下部のコマンドログへ流すために使う。
        /// </summary>
        public event Action<string, string>? CommandLogRequested;
        #endregion イベント

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
        /// 送信機初期化(FA/FB)実行中かどうか
        /// 概要：初期化中は接続設定ダイアログの「閉じる」を無効化するためのフラグ。
        ///       手動初期化・接続時の自動初期化の双方でセット/クリアする。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanClose))]
        private bool isInitializing;

        /// <summary>
        /// 接続設定ダイアログを閉じてよいか（初期化中は false）。
        /// </summary>
        public bool CanClose => !IsInitializing;

        /// <summary>
        /// Transport 接続状態
        /// 概要：ILightingFacade.IsConnected を追跡。
        /// Connect / Disconnect ボタン および COM チェックボックスの
        /// 有効/無効判定に使用。
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        private bool isConnected;

        /// <summary>
        /// 未接続フラグ（IsConnected の反転、XAML IsEnabled バインド用）
        /// 概要：未接続時 true、接続中 false。
        ///       Connect ボタン、Refresh ボタン、COM チェックボックスの
        ///       IsEnabled に使用する。
        /// </summary>
        public bool IsNotConnected => !IsConnected;

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
        /// 送信機チャネル設定値（FA）
        /// 概要：使用可能は 2〜4。ch=1(2405MHz) は端末未実装のため使用不可。
        ///       ch=2:2.434GHz / ch=3:2.451GHz / ch=4:2.475GHz（端末既定）
        /// 既定：4（端末の工場既定に合わせる）。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsChannelValid))]
        private byte channelValue = 4;

        /// <summary>
        /// Channel 選択肢（ComboBox 用）。CH1 は表示のみで選択不可。
        /// </summary>
        public IReadOnlyList<ChannelOption> ChannelOptions => ChannelOption.All;

        /// <summary>
        /// 送信機送信電力設定値（FB）
        /// 概要：範囲 0〜3（プロトコル仕様、4段階）。
        ///       電力レベル表（概算）: 0=-18dBm / 1=-12dBm / 2=-6dBm / 3=0dBm(最大)
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPowerValid))]
        private byte powerValue = 3;

        /// <summary>
        /// Channel値が有効範囲内かどうか（2〜4）。
        /// CH1(2405MHz) は端末未実装のため無効扱い（選択・送信させない）。
        /// </summary>
        public bool IsChannelValid => ChannelValue >= 2 && ChannelValue <= 4;

        /// <summary>
        /// Power値が有効範囲内かどうか（0〜3）
        /// </summary>
        public bool IsPowerValid => PowerValue <= 3;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// 自動接続を実行するか（v2.2 新規）
        /// 概要：true の場合、起動時に COM ポートを自動検出・自動チェック・自動 Connect する。
        /// 既定：true（オペレータの手間を最小化）
        /// </summary>
        public bool AutoConnectOnStartup { get; set; } = true;

        public SettingViewModel(ILightingFacade lighting)
        {
            _lighting = lighting;
            _lighting.StatusChanged += OnFacadeStatusChanged;
            IsConnected = _lighting.IsConnected;

            // 初期ポート読み込み + 自動選択 + 自動接続
            _ = StartupAutoConnectFlowAsync();
            LoadStatus();
        }

        /// <summary>
        /// 起動時の自動接続フロー
        /// 概要：
        ///   ① ポート一覧を自動取得（RefreshPortsAsync）
        ///   ② 検出された全 COM ポートを自動チェック
        ///   ③ AutoConnectOnStartup=true なら自動 Connect 実行
        /// </summary>
        private async Task StartupAutoConnectFlowAsync()
        {
            try
            {
                // ① ポート一覧取得
                await RefreshPortsAsync();

                // 既に接続済みの場合はスキップ
                if (_lighting.IsConnected) return;

                // ② 検出された全 COM ポートを自動チェック
                if (Ports.Count == 0)
                {
                    SettingStatusMessage = "COM ポートが検出されませんでした。USB 接続をご確認ください。";
                    return;
                }

                foreach (var port in Ports)
                {
                    port.IsSelected = true;   // 全部チェック
                }

                SettingStatusMessage = $"検出された {Ports.Count} 個のポートを自動選択しました。";

                // ③ 自動接続（任意）
                if (AutoConnectOnStartup)
                {
                    // 0.5 秒待ってから接続（UI 描画が落ち着くまで）
                    await Task.Delay(500);
                    await ConnectAsync();
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "StartupAutoConnectFlow failed");
                SettingStatusMessage = $"自動接続フロー失敗: {ex.Message}";
            }
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
                IsConnected = _lighting.IsConnected;
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
            bool wasSuccess = false;
            try
            {
                await _lighting.ConnectAsync(selected);
                SettingStatusMessage = $"接続完了: {string.Join(", ", selected)}";
                UpdatePortConnectionFlags();
                LoadStatus();
                wasSuccess = true;
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"接続失敗: {ex.Message}";
            }
            finally
            {
                IsConnected = _lighting.IsConnected;
                IsBusy = false;
            }

            // 接続成功時はイベント発火（MainWindowViewModel が拾ってシーケンス編集ウィンドウを開く）
            if (wasSuccess && IsConnected)
            {
                ConnectedSuccessfully?.Invoke(this, EventArgs.Empty);

                // 接続成功後に送信機初期化を自動実行（確認ダイアログなし）
                await AutoInitializeTransmitterAsync();
            }
        }

        /// <summary>
        /// 接続成功後に送信機初期化を自動実行する（確認ダイアログなし）
        /// </summary>
        private async Task AutoInitializeTransmitterAsync()
        {
            if (!IsChannelValid || !IsPowerValid) return;

            IsInitializing = true; // 初期化中は接続設定ダイアログの「閉じる」を無効化
            try
            {
                CommandLogRequested?.Invoke("TX", $"送信機初期化(自動) 開始 (ch={ChannelValue}, pwr={PowerValue})");
                await _lighting.InitializeTransmitterAsync(ChannelValue, PowerValue);
                SettingStatusMessage = $"自動初期化完了 (ch={ChannelValue}, pwr={PowerValue})";
                CommandLogRequested?.Invoke("TX", $"送信機初期化(自動) 完了 (ch={ChannelValue}, pwr={PowerValue})");
                TransmitterInitialized?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"自動初期化失敗: {ex.Message}";
                CommandLogRequested?.Invoke("ERR", $"送信機初期化(自動) 失敗: {ex.Message}");
            }
            finally
            {
                IsInitializing = false;
            }
        }

        /// <summary>
        /// 切断コマンド
        /// 概要：接続中の全ポートを閉じる。
        /// </summary>
        [RelayCommand]
        private async Task DisconnectAsync()
        {
            if (!IsConnected || IsBusy) return;

            // 重要操作の確認ダイアログ
            var result = System.Windows.MessageBox.Show(
                "全ポートを切断します。本番中の場合、演出が停止します。\n実行してよろしいですか？",
                "切断確認 / Disconnect Confirmation",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                SettingStatusMessage = "切断をキャンセルしました";
                return;
            }

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
                IsConnected = _lighting.IsConnected;
                IsBusy = false;
            }

            // 切断成功時にイベント発火（Init 状態リセット用）
            if (!IsConnected)
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
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

            // プロトコル範囲チェック（ch=2..4, pwr=0..3）。CH1(2405MHz)は端末未実装で使用不可。
            if (!IsChannelValid)
            {
                SettingStatusMessage = "Channel は 2〜4 を選択してください（CH1/2405MHzは端末未実装で使用不可）。";
                return;
            }
            if (!IsPowerValid)
            {
                SettingStatusMessage = "Power は 0〜3 の範囲で入力してください。";
                return;
            }

            // 初期化(FA→2秒→FB→2秒 ≈ 約4秒)は確認ダイアログを出さず即実行し、
            // 完了まで接続設定ダイアログの「閉じる」を無効化する（IsInitializing / CanClose）。

            IsBusy = true;
            IsInitializing = true; // 初期化中は接続設定ダイアログの「閉じる」を無効化
            try
            {
                CommandLogRequested?.Invoke("TX", $"送信機初期化 開始 (ch={ChannelValue}, pwr={PowerValue})");
                await _lighting.InitializeTransmitterAsync(ChannelValue, PowerValue);
                SettingStatusMessage = $"送信機初期化完了 (ch={ChannelValue}, pwr={PowerValue})";
                CommandLogRequested?.Invoke("TX", $"送信機初期化 完了 (ch={ChannelValue}, pwr={PowerValue})");
                TransmitterInitialized?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"送信機初期化失敗: {ex.Message}";
                CommandLogRequested?.Invoke("ERR", $"送信機初期化 失敗: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                IsInitializing = false;
            }
        }

        /// <summary>
        /// 受信端末チャンネル変更コマンド（A6/AD）
        /// 概要：選択中の Channel(2〜4) へ「受信端末」の受信chを移動する。正しい順序で実行する：
        ///   ① 送信機を CH4→CH3→CH2 と切替えながら、各chで A6（前後分区・行）＋ AD（左右分区・列）で
        ///      目的 ch を全端末へ送信。A6/AD は「端末が今いるchで送信機が電波を出している時」しか届かず、
        ///      端末の現在chはアプリから観測できないため、全有効chを基準に順次送って必ず届かせる。
        ///      （実行時は端末が点灯＝通信中であること）
        ///   ② 約1.5秒待機（端末が受信chを切替。資料 3.6/3.7）
        ///   ③ 送信機を目的の ch に設定（FA/FB 初期化）→ 端末と送信機の周波数が一致し新chで復帰
        /// この方式により、1度目だけでなく2度目以降の連続変更でも端末の電源OFFは不要。
        /// 注意：CH1(2405MHz)は端末未実装のため送信しない。電源断で端末は既定(CH4/2475MHz)に戻る。
        /// 送信機側のみ変える「送信機初期化」とは別物（こちらは端末側の周波数を動かす）。
        /// </summary>
        [RelayCommand]
        private async Task ChangeReceiverChannelAsync()
        {
            if (IsBusy) return;

            // CH1(2405MHz)は端末未実装で使用不可。有効範囲は 2〜4。
            if (!IsChannelValid)
            {
                SettingStatusMessage = "Channel は 2〜4 を選択してください（CH1/2405MHzは端末未実装で使用不可）。";
                return;
            }
            if (!IsConnected)
            {
                SettingStatusMessage = "ポートが未接続です。先に接続してください。";
                return;
            }

            byte ch = ChannelValue;

            // A6/AD は「端末が今いる ch で送信機が電波を出している時」しか届かない。
            // 端末の現在 ch は電源投入直後は CH4、以降は前回変更した ch になり得るが、アプリからは観測できない。
            // そこで送信機を有効な全 ch(4→3→2) に順に合わせ、各 ch で A6/AD(目的ch) を送る＝
            // 端末が今どの ch に居ても必ず1回は受信できる（=2回目以降も電源OFF不要で切替可能）。
            // CH1(2405MHz)は端末未実装のため基準からも除外。
            byte[] baselineChannels = { 4, 3, 2 };

            var confirm = System.Windows.MessageBox.Show(
                $"受信端末のチャンネルを CH{ch} に変更します。\n\n" +
                "自動手順: 送信機を CH4→CH3→CH2 と切替えながら、各chで A6/AD により CH" + ch + " を全端末へ通知 → 約1.5秒 → 送信機も CH" + ch + " へ。\n" +
                "端末が現在どのch(2/3/4)に居ても切替わります（連続変更でも電源OFFは不要）。\n" +
                $"端末は一瞬消灯し、CH{ch} で復帰します。\n\n" +
                "※実行時は端末が点灯（通信中）していること。CH1(2405MHz)は端末未実装で使用不可。\n\n" +
                "実行してよろしいですか？",
                "受信端チャンネル変更 / Change Receiver Channel",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                SettingStatusMessage = "受信端チャンネル変更をキャンセルしました";
                return;
            }

            IsBusy = true;
            // 端末ch変更は送信機CH4→3→2の各chでA6/AD×3回＋約1.5秒待機＋最終初期化と、
            // 完了まで数秒かかる。途中で閉じると端末と送信機のchが不一致のまま残るため、
            // 送信機初期化と同様に完了まで接続設定ダイアログの「閉じる」を無効化する
            // （IsInitializing / CanClose・×/Alt+F4 も OnClosing でキャンセル）。
            IsInitializing = true;
            try
            {
                CommandLogRequested?.Invoke("TX", $"受信端ch変更 開始 → CH{ch}");

                // ① 端末が現在どの ch に居ても届くよう、送信機を 4→3→2 と切替えながら
                //    各 ch で A6/AD(目的ch) を送信する（A6=前後分区/行, AD=左右分区/列）。
                //    連続する色送信に紛れて取りこぼされないよう各3回送る（実機検証済みの堅牢手順）。
                foreach (var baseCh in baselineChannels)
                {
                    SettingStatusMessage = $"送信機を CH{baseCh} に合わせて CH{ch} を通知中…";
                    await _lighting.InitializeTransmitterAsync(baseCh, PowerValue);
                    await Task.Delay(200);

                    CommandLogRequested?.Invoke("TX", $"[基準CH{baseCh}] A6/AD で CH{ch} を通知（各3回）");
                    for (int i = 0; i < 3; i++) { await _lighting.SetReceiverChannelAsync(ch); await Task.Delay(150); }
                    for (int i = 0; i < 3; i++) { await _lighting.SetReceiverChannelColAsync(ch); await Task.Delay(150); }
                }

                // ② 端末が受信chを切替えるまで待機（資料: 約1.5秒）
                SettingStatusMessage = $"端末を CH{ch} へ切替中…（約1.5秒）";
                await Task.Delay(1500);

                // ③ 送信機を目的の ch に合わせる（FA/FB）。これで端末と送信機の周波数が一致し復帰する。
                await _lighting.InitializeTransmitterAsync(ch, PowerValue);

                SettingStatusMessage = $"受信端チャンネル変更 完了（端末・送信機とも CH{ch}）";
                CommandLogRequested?.Invoke("TX", $"受信端ch変更 完了 → CH{ch}（送信機も CH{ch} に設定）");
                TransmitterInitialized?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                SettingStatusMessage = $"受信端チャンネル変更 失敗: {ex.Message}";
                CommandLogRequested?.Invoke("ERR", $"受信端ch変更 失敗: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                IsInitializing = false;
            }
        }

        /// <summary>
        /// Disconnect 実行可否判定（UX fix）
        /// 概要：接続中かつ送信中でない時のみ Disconnect ボタンが押せる。
        /// 未接続時はボタンがグレーアウトする。
        /// </summary>
        private bool CanDisconnect() => IsConnected && !IsBusy;

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
                IsConnected = _lighting.IsConnected; 
                UpdatePortConnectionFlags();
                LoadStatus();
            });
        }

        #endregion メソッド
    }
}