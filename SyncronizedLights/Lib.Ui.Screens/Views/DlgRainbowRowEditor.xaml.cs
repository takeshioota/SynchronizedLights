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

        // 仕様V4.9(3.18-3.20): FI/FO 系も RGB は 0〜255（最小25は誤記と訂正）。
        // どのモードでも色編集値をそのまま反映する（下限補正なし）。
        private void EditColor(RgbColorItem item)
        {
            var dlg = new DlgColorPicker { Owner = this };
            dlg.SetInitialColor(item.R, item.G, item.B);

            // プレビュー：スウォッチをリアルタイム更新（HexColor 通知で反映）
            dlg.OnColorChanged = (r, g, b) =>
            {
                item.R = r;
                item.G = g;
                item.B = b;
            };

            if (dlg.ShowDialog() == true)
            {
                item.R = dlg.SelectedR;
                item.G = dlg.SelectedG;
                item.B = dlg.SelectedB;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var row = Row;
            if (row != null)
            {
                // 追加開発(v2.12.0): Rainbow 入力制限緩和。下限の独自補正は撤廃し受付値をそのまま送出するが、
                // 「上限5000ms・整数・色数2〜7」の形式制約は維持する。範囲外の値は丸めたり桁落ちさせたりせず、
                // エラーとして入力を差し戻す（＝値を受け付けない）。下限未満は端末側の補正に委ねる（設計書3.1/5）。
                if (!ValidateRow(row, out string error))
                {
                    MessageBox.Show(this, error, "Rainbow 設定",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return; // ダイアログを閉じない＝範囲外の値を確定しない
                }
                row.RefreshRainbowSummary();
            }
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// 追加開発(v2.12.0): Rainbow 入力の形式検証。時間系は 0〜5000ms（下限補正なし・上限超過は不可）、
        /// 色数は 2〜7、点灯比率は 0〜9。範囲外は丸めずエラーメッセージを返す。
        /// </summary>
        private static bool ValidateRow(SequenceStepWrapper row, out string error)
        {
            const int TimeMax = 5000;

            int n = row.RainbowColors?.Count ?? 0;
            if (n < 2 || n > 7)
            {
                error = $"レインボーカラーは 2〜7 色で設定してください（現在 {n} 色）。";
                return false;
            }

            var times = new (string Name, int Value)[]
            {
                ("速度(色切替間隔)", row.RainbowCycleDurationMs),
                ("点滅周期", row.RainbowBlinkPeriodMs),
                ("フェードイン時間(FI)", row.RainbowFadeInMs),
                ("フェードアウト時間(FO)", row.RainbowFadeOutMs),
            };
            foreach (var t in times)
            {
                if (t.Value < 0 || t.Value > TimeMax)
                {
                    error = $"{t.Name} は 0〜{TimeMax}ms の範囲で入力してください（現在 {t.Value}ms）。\n"
                          + $"※ 仕様下限未満の値は端末側で補正されます。{TimeMax}ms を超える値は設定できません。";
                    return false;
                }
            }

            if (row.RainbowDutyRatio > 9)
            {
                error = $"点灯比率は 0〜9 で入力してください（現在 {row.RainbowDutyRatio}）。";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
