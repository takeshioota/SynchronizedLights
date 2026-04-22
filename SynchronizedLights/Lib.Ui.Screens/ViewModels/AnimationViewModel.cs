using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
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
    public partial class AnimationViewModel : ObservableObject, IDisposable
    {
        #region フィールド

        /// <summary>ライティング制御ファセード</summary>
        private readonly ILightingFacade? _lighting;
        private DispatcherTimer? _previewTimer;
        private int _previewFrame;

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
        /// 送信実行中フラグ
        /// 概要：実行ボタン押下中は true になり、ボタンを無効化する。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        private bool isBusy;

        /// <summary>
        /// 選択中アニメが存在し、かつ送信中でないとき true
        /// 概要：実行ボタンの IsEnabled バインド先。
        /// </summary>
        public bool HasSelection
            => SelectedAnimation != null && SelectedAnimation.IsDefined && !IsBusy;

        [ObservableProperty]
        private string previewText = "アニメーションを選択してください";

        [ObservableProperty]
        private string statusMessage = "準備完了";

        /// <summary>
        /// プレビュー光色（Ellipse の Fill にバインド）
        /// </summary>
        [ObservableProperty]
        private Brush previewLightBrush = new SolidColorBrush(Color.FromRgb(32, 32, 32));

        /// <summary>
        /// プレビュー光の不透明度（Ellipse の Opacity にバインド）
        /// </summary>
        [ObservableProperty]
        private double previewLightOpacity = 1.0;

        /// <summary>
        /// プレビュー光のサイズ（直径 px）
        /// </summary>
        [ObservableProperty]
        private double previewLightSize = 80.0;

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

            // 未登録アニメのクリック → エラー発火
            if (!animation.IsDefined)
            {
                foreach (var item in Animations) item.IsSelected = false;
                SelectedAnimation = null;
                StopPreview();
                PreviewText = $"『アニメデータなし』: {animation.Name}";
                StatusMessage = $"アニメデータなし ({animation.Name})";
                return;
            }

            // 定義済みアニメの通常選択
            foreach (var item in Animations)
            {
                item.IsSelected = item == animation;
            }

            SelectedAnimation = animation;
            PreviewText = $"選択中: {animation.Name}";
            StatusMessage = $"{animation.Name} selected";
            StartPreview(animation);
        }

        /// <summary>
        /// 実行コマンド
        /// 概要：選択中のアニメーションを全体対象（ALL）で実行する。
        /// 未選択時はエラーメッセージを表示。
        /// </summary>
        [RelayCommand]
        private async Task ExecuteAnimationAsync()
        {
            if (IsBusy) return;
            if (SelectedAnimation == null || !SelectedAnimation.IsDefined)
            {
                StatusMessage = "定義済みアニメーションを選択してください";
                return;
            }

            IsBusy = true;
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
            finally
            {
                IsBusy = false;
            }
        }

        #endregion コマンド

        #region プレビュー描画

        /// <summary>
        /// プレビューを開始する（100ms 周期、10FPS）
        /// </summary>
        private void StartPreview(AnimationItemViewModel animation)
        {
            StopPreview();
            _previewFrame = 0;
            _previewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _previewTimer.Tick += (s, e) => UpdatePreviewFrame(animation);
            _previewTimer.Start();
            UpdatePreviewFrame(animation);
        }

        /// <summary>
        /// プレビューを停止する
        /// </summary>
        private void StopPreview()
        {
            _previewTimer?.Stop();
            _previewTimer = null;
            PreviewLightBrush = new SolidColorBrush(Color.FromRgb(32, 32, 32));
            PreviewLightOpacity = 1.0;
            PreviewLightSize = 80.0;
        }

        /// <summary>
        /// プレビューフレーム更新
        /// 概要：Animation ID ごとに異なる可視化パターンを適用する。
        /// </summary>
        private void UpdatePreviewFrame(AnimationItemViewModel animation)
        {
            _previewFrame++;
            var baseColor = ((SolidColorBrush)animation.IconBrush).Color;

            switch (animation.Id)
            {
                case 1: // 点灯（固定色）
                    PreviewLightBrush = new SolidColorBrush(baseColor);
                    PreviewLightOpacity = 1.0;
                    PreviewLightSize = 80;
                    break;

                case 2: // 高速点滅
                    var blink = _previewFrame % 4 < 2;
                    PreviewLightBrush = new SolidColorBrush(baseColor);
                    PreviewLightOpacity = blink ? 1.0 : 0.15;
                    PreviewLightSize = 80;
                    break;

                case 3: // スローフェード
                    var t3 = (Math.Sin(_previewFrame * 0.15) + 1) / 2;
                    PreviewLightBrush = new SolidColorBrush(baseColor);
                    PreviewLightOpacity = 0.2 + t3 * 0.8;
                    PreviewLightSize = 80;
                    break;

                case 4: // パルス（拡大縮小）
                    var t4 = (Math.Sin(_previewFrame * 0.2) + 1) / 2;
                    PreviewLightBrush = new SolidColorBrush(baseColor);
                    PreviewLightOpacity = 1.0;
                    PreviewLightSize = 60 + t4 * 50;
                    break;

                case 5: // 色相サイクル（虹色）
                    var hue = (_previewFrame * 10) % 360;
                    PreviewLightBrush = new SolidColorBrush(HsvToColor(hue, 1, 1));
                    PreviewLightOpacity = 1.0;
                    PreviewLightSize = 80;
                    break;

                case 6: // ストロボ（非常に速い点滅）
                    var strobe = _previewFrame % 2 == 0;
                    PreviewLightBrush = new SolidColorBrush(baseColor);
                    PreviewLightOpacity = strobe ? 1.0 : 0.0;
                    PreviewLightSize = 80;
                    break;

                default: // 未定義（通常このパスは呼ばれない、定義済みのみStartPreview対象）
                    PreviewLightBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));
                    PreviewLightOpacity = 1.0;
                    PreviewLightSize = 80;
                    break;
            }
        }

        /// <summary>
        /// HSV → Color 変換（プレビュー用の虹色サイクル表現）
        /// </summary>
        private static Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }
            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }

        #endregion プレビュー描画

        #region メソッド

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
                // Animation 01〜06 は定義済み、07〜12 は未登録
                var isDefined = i <= 6;

                Animations.Add(new AnimationItemViewModel(
                    id: i,
                    name: $"Animation {i:D2}",
                    iconColorHex: palette[(i - 1) % palette.Length],
                    isDefined: isDefined));
            }
        }

        public void Dispose()
        {
            StopPreview();
        }

        #endregion メソッド
    }
}