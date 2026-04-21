using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lib.Application.Interfaces;
using Lib.Domain.Enums;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Animation画面ViewModel
    /// 概要：Animation パターン一覧・選択・プレビュー・実行を担当。
    /// </summary>
    public partial class AnimationViewModel : ObservableObject
    {
        #region フィールド

        /// <summary>ライティング制御ファセード</summary>
        private readonly ILightingFacade? _lighting;

        #endregion フィールド

        #region プロパティ

        /// <summary>
        /// アニメーション一覧
        /// 概要：画面グリッドに並ぶ AnimationItemViewModel のコレクション。
        /// </summary>
        public ObservableCollection<AnimationItemViewModel> Animations { get; } = new();

        /// <summary>
        /// 選択中アニメーション
        /// 概要：ユーザーがタップしたタイルの ViewModel。未選択時は null。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        private AnimationItemViewModel? selectedAnimation;

        /// <summary>
        /// 選択中かどうか
        /// 概要：実行ボタンの有効/無効判定に使用する。
        /// </summary>
        public bool HasSelection => SelectedAnimation != null;

        /// <summary>
        /// プレビュー表示文字列
        /// 概要：選択中のアニメーション名を表示する。
        /// 将来はプレビュー映像領域に置き換える想定。
        /// </summary>
        [ObservableProperty]
        private string previewText = "アニメーションを選択してください";

        /// <summary>
        /// 直近操作メッセージ
        /// 概要：選択・実行結果のステータス表示。
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "準備完了";

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// パラメータなしコンストラクタ（既存コード互換用）
        /// </summary>
        public AnimationViewModel() : this(null) { }

        /// <summary>
        /// AnimationViewModelを生成する。
        /// 概要：ILightingFacadeを受け取り、ダミーアニメを読み込む。
        /// </summary>
        public AnimationViewModel(ILightingFacade? lighting)
        {
            _lighting = lighting;
            LoadDummyAnimations();
        }

        #endregion コンストラクタ

        #region コマンド

        /// <summary>
        /// アニメーション選択コマンド
        /// 概要：タイル押下時に呼ばれ、選択状態を更新する。
        /// 他のタイルのIsSelectedをfalseに、押下されたものをtrueにする（排他）。
        /// </summary>
        [RelayCommand]
        private void SelectAnimation(AnimationItemViewModel? animation)
        {
            if (animation == null) return;

            foreach (var item in Animations)
            {
                item.IsSelected = item == animation;
            }

            SelectedAnimation = animation;
            PreviewText = $"選択中: {animation.Name}";
            StatusMessage = $"{animation.Name} selected";
        }

        /// <summary>
        /// 実行コマンド
        /// 概要：選択中のアニメーションを全体対象（ALL）で実行する。
        /// 未選択時はエラーメッセージを表示。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteAnimationAsync()
        {
            if (SelectedAnimation == null)
            {
                StatusMessage = "アニメーションを選択してください";
                return;
            }

            try
            {
                if (_lighting == null)
                {
                    StatusMessage = $"(Dummy) {SelectedAnimation.Name} executed";
                    return;
                }

                if (!_lighting.IsConnected)
                {
                    StatusMessage = "未接続のため実行をスキップしました";
                    return;
                }

                await _lighting.ExecuteSequenceAsync(Target.All, SelectedAnimation.Id);
                StatusMessage = $"実行完了: {SelectedAnimation.Name}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"実行失敗: {ex.Message}";
            }
        }

        #endregion コマンド

        #region メソッド

        /// <summary>
        /// ダミーアニメーション12件を読み込む
        /// 概要：本格実装ではJSON等からの読み込みに置き換える。
        /// タイルのアイコン色は12色のパレットから循環で割り当てる。
        /// </summary>
        private void LoadDummyAnimations()
        {
            var palette = new[]
            {
                "#FFC107", "#E91E63", "#9C27B0", "#3F51B5",
                "#2196F3", "#4CAF50", "#FF9800", "#F44336",
                "#00BCD4", "#8BC34A", "#FF5722", "#673AB7"
            };

            for (int i = 1; i <= 12; i++)
            {
                Animations.Add(new AnimationItemViewModel(
                    id: i,
                    name: $"Animation {i:D2}",
                    iconColorHex: palette[(i - 1) % palette.Length]));
            }
        }

        #endregion メソッド
    }
}