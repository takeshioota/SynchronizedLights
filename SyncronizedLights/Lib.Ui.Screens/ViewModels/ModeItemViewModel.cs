using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Modeアイテム1件分のViewModel
    /// 概要：Modeパネルのタイル1つが保持する情報。
    /// モード名（日/英）、説明、アクセント色、選択状態を持つ。
    /// </summary>
    public partial class ModeItemViewModel : ObservableObject
    {
        /// <summary>モードID</summary>
        public int Id { get; }

        /// <summary>日本語名（例：点滅）</summary>
        public string NameJa { get; }

        /// <summary>英語名（例：Flash）</summary>
        public string NameEn { get; }

        /// <summary>説明文</summary>
        public string Description { get; }

        /// <summary>
        /// アクセント色ブラシ
        /// 概要：タイル左端の縦帯として表示するモード識別色。
        /// </summary>
        public Brush AccentBrush { get; }

        /// <summary>
        /// 選択中かどうか
        /// 概要：GrandMaSelectableButtonStyle のTagにバインドされ、
        /// アンバー枠のハイライトを切り替える。
        /// </summary>
        [ObservableProperty]
        private bool isSelected;

        /// <summary>
        /// タイル表示用の統合名（例：「点滅 | Flash」）
        /// </summary>
        public string DisplayName => $"{NameJa} | {NameEn}";

        /// <summary>
        /// ModeItemViewModelを生成する。
        /// </summary>
        public ModeItemViewModel(int id, string nameJa, string nameEn, string description, string accentColorHex)
        {
            Id = id;
            NameJa = nameJa;
            NameEn = nameEn;
            Description = description;
            var color = (Color)ColorConverter.ConvertFromString(accentColorHex);
            AccentBrush = new SolidColorBrush(color);
        }
    }
}