using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Animationアイテム1件分のViewModel
    /// 概要：Animationパネルのグリッドに並ぶタイル1つの情報を保持する。
    ///       未登録（IsDefined=false）の場合は薄く表示され、クリック時にエラーを発火する。
    /// </summary>
    public partial class AnimationItemViewModel : ObservableObject
    {
        /// <summary>アニメID</summary>
        public int Id { get; }

        /// <summary>表示名</summary>
        public string Name { get; }

        /// <summary>
        /// アイコン色ブラシ
        /// </summary>
        public Brush IconBrush { get; }

        /// <summary>
        /// 選択中かどうか
        /// </summary>
        [ObservableProperty]
        private bool isSelected;

        /// <summary>
        /// 定義済みかどうか
        /// </summary>
        public bool IsDefined { get; }

        /// <summary>
        /// AnimationItemViewModelを生成する。
        /// </summary>
        public AnimationItemViewModel(int id, string name, string iconColorHex, bool isDefined = true)
        {
            Id = id;
            Name = name;
            var color = (Color)ColorConverter.ConvertFromString(iconColorHex);
            IconBrush = new SolidColorBrush(color);
            IsDefined = isDefined;
        }
    }
}