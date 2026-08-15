using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;
using Lib.Application.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// ゾーンアイテムViewModel
    /// 概要：Zone1〜4 のそれぞれに対応する 1 エントリのデータ・操作を担当。
    ///       割り当てポート・CH・電力・初期化ボタンを管理する。
    /// </summary>
    public partial class ZoneItemViewModel : ObservableObject
    {
        #region フィールド

        private readonly ILightingFacade _lighting;

        #endregion フィールド

        #region プロパティ

        /// <summary>ゾーン名（"Zone1"〜"Zone4"）</summary>
        [ObservableProperty]
        private string zoneName = "";

        /// <summary>
        /// 割り当てポート名
        /// 概要：ドロップダウンで選択された "-"（未割当）または "COM3" 等。
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InitializeZoneCommand))]
        private string assignedPort = "-";

        /// <summary>受信チャネル（使用可 2〜4。CH1/2405MHzは端末未実装で使用不可）。既定=4（端末既定）。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsChannelValid))]
        [NotifyCanExecuteChangedFor(nameof(InitializeZoneCommand))]
        private byte channel = 4;

        /// <summary>Channel 選択肢（ComboBox 用）。CH1 は表示のみで選択不可。</summary>
        public IReadOnlyList<ChannelOption> ChannelOptions => ChannelOption.All;

        /// <summary>送信電力（0〜3）</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPowerValid))]
        [NotifyCanExecuteChangedFor(nameof(InitializeZoneCommand))]
        private byte power = 3;

        /// <summary>このゾーンの状態メッセージ</summary>
        [ObservableProperty]
        private string zoneStatusMessage = "";

        /// <summary>実行中フラグ（二重押下防止）</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InitializeZoneCommand))]
        private bool isBusy;

        /// <summary>Channel 値の有効性（2〜4）。CH1(2405MHz)は端末未実装のため無効扱い。</summary>
        public bool IsChannelValid => Channel >= 2 && Channel <= 4;

        /// <summary>Power 値の有効性（0〜3）</summary>
        public bool IsPowerValid => Power <= 3;

        /// <summary>
        /// 選択可能ポート一覧
        /// 概要：SettingViewModel 側で一元管理される共通リストへの参照。
        ///       "-"（未割当）+ 利用可能な全 COM ポート名。
        /// </summary>
        [ObservableProperty]
        private IReadOnlyList<string> availablePorts = new List<string> { "-" }.AsReadOnly();

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// ZoneItemViewModel を生成する
        /// </summary>
        public ZoneItemViewModel(ILightingFacade lighting)
        {
            _lighting = lighting;
        }

        #endregion コンストラクタ

        #region コマンド

        /// <summary>
        /// ゾーン初期化コマンド
        /// 概要：当該ゾーンに割り当てられたポートに対して、
        ///       設定済み CH / Power で送信機初期化を実行する。
        ///
        /// ※ 本来の挙動は「ポート指定付き init」だが、
        ///    現状 API は全ポート共通の init しか持たないため、
        ///    暫定的に全体初期化で代替しつつ、ログに対象ゾーン・ポートを記録する。
        ///    REST API 完成後、ポート指定対応に差し替える。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanInitializeZone))]
        private async Task InitializeZoneAsync()
        {
            if (IsBusy) return;

            if (!IsChannelValid)
            {
                ZoneStatusMessage = $"{ZoneName}: Channel は 2〜4（CH1/2405MHzは使用不可）";
                MisOperationTracker.Instance.RecordValidationFailure();
                return;
            }
            if (!IsPowerValid)
            {
                ZoneStatusMessage = $"{ZoneName}: Power 範囲外（0〜3）";
                MisOperationTracker.Instance.RecordValidationFailure();
                return;
            }
            if (string.IsNullOrEmpty(AssignedPort) || AssignedPort == "-")
            {
                ZoneStatusMessage = $"{ZoneName}: ポート未割当";
                return;
            }

            IsBusy = true;
            try
            {
                // API がポート指定 init をサポートしたら差替え
                Log.Information(
                    "[Zone] Initialize {Zone} → Port={Port}, CH={Ch}, Pwr={Pwr}",
                    ZoneName, AssignedPort, Channel, Power);

                await _lighting.InitializeTransmitterAsync(Channel, Power);

                ZoneStatusMessage = $"{ZoneName} → {AssignedPort} 初期化完了 (ch={Channel}, pwr={Power})";
            }
            catch (Exception ex)
            {
                ZoneStatusMessage = $"{ZoneName} 初期化失敗: {ex.Message}";
                MisOperationTracker.Instance.RecordSendFailure();
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 初期化実行可否判定
        /// </summary>
        private bool CanInitializeZone()
            => !IsBusy
            && IsChannelValid
            && IsPowerValid
            && !string.IsNullOrEmpty(AssignedPort)
            && AssignedPort != "-";

        #endregion コマンド

        #region メソッド

        /// <summary>
        /// ZoneConfig から値を流し込む（UserState 復元用）
        /// </summary>
        public void ApplyConfig(Lib.Application.Models.ZoneConfig config)
        {
            if (config == null) return;
            ZoneName = config.ZoneName;
            AssignedPort = string.IsNullOrEmpty(config.AssignedPort) ? "-" : config.AssignedPort;
            Channel = config.Channel;
            Power = config.Power;
        }

        /// <summary>
        /// ZoneConfig に現在値を書き出す（UserState 保存用）
        /// </summary>
        public Lib.Application.Models.ZoneConfig ToConfig()
        {
            return new Lib.Application.Models.ZoneConfig
            {
                ZoneName = ZoneName,
                AssignedPort = AssignedPort,
                Channel = Channel,
                Power = Power
            };
        }

        #endregion メソッド
    }
}