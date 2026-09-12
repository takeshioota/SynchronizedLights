using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// 行専用 Rainbow 編集ダイアログ（20260904）
    /// 概要：シーケンスグリッドの行を右クリック（またはダブルクリック）して開く。
    ///       その行（SequenceStepWrapper）の RainbowColors / RainbowMode / 速度・周期・FI/FO を
    ///       行単位で編集する。色は 2〜7 色まで可変（行ごとに色数を変えられる）。
    ///       DataContext には対象行の SequenceStepWrapper を設定する。
    ///       Wrapper を直接編集するため、呼び出し側は開く前に SaveUndoState() を行い、
    ///       閉じた後に wrapper.RefreshRainbowSummary() でグリッド表示を更新する。
    /// </summary>
    public partial class DlgRainbowRowEditor : Window
    {
        private SequenceStepWrapper? Row => DataContext as SequenceStepWrapper;

        public DlgRainbowRowEditor()
        {
            InitializeComponent();
        }

        public DlgRainbowRowEditor(SequenceStepWrapper row) : this()
        {
            DataContext = row;
        }

        /// <summary>色を追加する（最大7色）</summary>
        private void AddColor_Click(object sender, RoutedEventArgs e)
        {
            var row = Row;
            if (row == null) return;
            if (row.RainbowColors.Count >= 7)
            {
                MessageBox.Show(this, "レインボーカラーは最大 7 色です。", "Rainbow 設定",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            row.RainbowColors.Add(new RgbColorItem(255, 255, 255));
            row.RefreshRainbowSummary();
        }

        /// <summary>色を削除する（最低2色）</summary>
        private void RainbowColorDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not RgbColorItem item) return;

            var row = Row;
            if (row == null) return;
            if (row.RainbowColors.Count <= 2)
            {
                MessageBox.Show(this, "レインボーカラーは最低 2 色必要です。", "Rainbow 設定",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            row.RainbowColors.Remove(item);
            row.RefreshRainbowSummary();
        }

        /// <summary>色を編集する（右クリック → 「色を編集...」）</summary>
        private void RainbowColorEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;
            if (menuItem.Parent is not ContextMenu contextMenu) return;
            if (contextMenu.PlacementTarget is not FrameworkElement target) return;
            if (target.DataContext is not RgbColorItem item) return;
            EditColor(item);
        }

        /// <summary>スウォッチをダブルクリックしても色編集を開く</summary>
        private void Swatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not RgbColorItem item) return;
            EditColor(item);
            e.Handled = true;
        }

        // 仕様3.18-3.20: FI/FO 系（フェード包絡モード）は RGB 最小25（0x19）。
        // その行がフェードモードのときは、色編集値を 25 未満なら 25 に補正して
        // グリッド表示・端末送出値と一致させる（他モードは 0〜255 をそのまま許容）。
        private static byte ClampForMode(byte v, bool fade)
            => fade && v < SequenceStepWrapper.RainbowFadeRgbMin ? SequenceStepWrapper.RainbowFadeRgbMin : v;

        private void EditColor(RgbColorItem item)
        {
            bool fade = Row?.IsRainbowFadeMode == true;

            var dlg = new DlgColorPicker { Owner = this };
            dlg.SetInitialColor(item.R, item.G, item.B);

            // プレビュー：スウォッチをリアルタイム更新（HexColor 通知で反映）
            dlg.OnColorChanged = (r, g, b) =>
            {
                item.R = ClampForMode(r, fade);
                item.G = ClampForMode(g, fade);
                item.B = ClampForMode(b, fade);
            };

            if (dlg.ShowDialog() == true)
            {
                item.R = ClampForMode(dlg.SelectedR, fade);
                item.G = ClampForMode(dlg.SelectedG, fade);
                item.B = ClampForMode(dlg.SelectedB, fade);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Row?.RefreshRainbowSummary();
            DialogResult = true;
            Close();
        }
    }
}
