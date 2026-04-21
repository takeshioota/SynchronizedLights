using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Net.Sockets;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Mode画面ViewModel
    /// 点滅・プレス・フェードイン・フェードアウト の4モードから1つを選択する排他選択UI。
    /// 選択されたモードは、以降の演出操作時のデフォルト挙動として参照する想定。
    /// </summary>
    public partial class ModeViewModel : ObservableObject
    {
        #region プロパティ

        /// <summary>
        /// モード一覧
        /// </summary>
        public ObservableCollection<ModeItemViewModel> Modes { get; } = new();

        /// <summary>
        /// 選択中モード
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        [NotifyPropertyChangedFor(nameof(SelectedModeDetail))]
        private ModeItemViewModel? selectedMode;

        /// <summary>
        /// 選択済みかどうか（適用ボタンの有効/無効判定）
        /// </summary>
        public bool HasSelection => SelectedMode != null;

        /// <summary>
        /// 選択中モードの詳細表示文字列
        /// </summary>
        public string SelectedModeDetail
            => SelectedMode == null
                ? "モードを選択すると、ここに詳細説明が表示されます。"
                : $"【{SelectedMode.DisplayName}】{System.Environment.NewLine}{SelectedMode.Description}";

        /// <summary>
        /// ステータスメッセージ
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "モードを選択してください";

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// ModeViewModelを生成する。
        /// </summary>
        public ModeViewModel()
        {
            LoadDummyModes();
        }

        #endregion コンストラクタ

        #region コマンド

        /// <summary>
        /// モード選択コマンド
        /// 概要：タイル押下時に呼ばれ、選択状態を排他的に更新する。
        /// </summary>
        [RelayCommand]
        private void SelectMode(ModeItemViewModel? mode)
        {
            if (mode == null) return;

            foreach (var m in Modes)
            {
                m.IsSelected = m == mode;
            }

            SelectedMode = mode;
            StatusMessage = $"{mode.NameJa} モードが選択されました";
        }

        /// <summary>
        /// モード適用コマンド
        /// 概要：選択中モードをシステム全体の既定挙動として適用する。
        /// </summary>
        [RelayCommand]
        private void ApplyMode()
        {
            if (SelectedMode == null)
            {
                StatusMessage = "モードを選択してください";
                return;
            }

            StatusMessage = $"{SelectedMode.NameJa} モードを適用しました（ID={SelectedMode.Id}）";
        }

        #endregion コマンド

        #region メソッド

        /// <summary>
        /// ダミーの4モードを読み込む
        /// 概要：点滅・プレス・フェードイン・フェードアウト を用意する。
        /// </summary>
        private void LoadDummyModes()
        {
            Modes.Add(new ModeItemViewModel(
                id: 1,
                nameJa: "点滅",
                nameEn: "Flash",
                description: "等間隔で繰り返し点滅します。速度は上部のSpeedプリセット、またはPresetパネルのスライダーで調整できます。",
                accentColorHex: "#FFC107"));

            Modes.Add(new ModeItemViewModel(
                id: 2,
                nameJa: "プレス",
                nameEn: "Press",
                description: "ボタン押下中のみ発光します。短時間のアクセント演出やビート合わせに最適です。",
                accentColorHex: "#E91E63"));

            Modes.Add(new ModeItemViewModel(
                id: 3,
                nameJa: "フェードイン",
                nameEn: "FadeIn",
                description: "指定時間をかけてゆっくりと明るくなる演出。サビや登場シーンに使います。",
                accentColorHex: "#4CAF50"));

            Modes.Add(new ModeItemViewModel(
                id: 4,
                nameJa: "フェードアウト",
                nameEn: "FadeOut",
                description: "指定時間をかけてゆっくりと暗くなる演出。場面転換やエンディングに使います。",
                accentColorHex: "#2196F3"));
        }

        #endregion メソッド
    }
}