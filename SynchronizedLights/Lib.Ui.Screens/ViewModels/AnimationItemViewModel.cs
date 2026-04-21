using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Animationアイテム1件分のViewModel
    /// 概要：Animationパネルのグリッドに並ぶタイル1つの情報を保持する。
    /// Id / Name / IconBrush / IsSelected を持つ。
    /// </summary>
    public partial class AnimationItemViewModel : ObservableObject
    {
        /// <summary>アニメID（1〜12）</summary>
        public int Id { get; }

        /// <summary>表示名（例：Animation 01）</summary>
        public string Name { get; }

        /// <summary>
        /// アイコン色ブラシ
        /// 概要：タイル上部の小さな色見本。
        /// 実アニメアイコン差し替え前の仮表示として使う。
        /// </summary>
        public Brush IconBrush { get; }

        /// <summary>
        /// 選択中かどうか
        /// 概要：タイルの GrandMaSelectableButtonStyle の Tag にバインドされ、
        /// アンバー枠のハイライト表示を切り替える。
        /// </summary>
        [ObservableProperty]
        private bool isSelected;

        /// <summary>
        /// AnimationItemViewModelを生成する。
        /// </summary>
        public AnimationItemViewModel(int id, string name, string iconColorHex)
        {
            Id = id;
            Name = name;
            var color = (Color)ColorConverter.ConvertFromString(iconColorHex);
            IconBrush = new SolidColorBrush(color);
        }
    }
}