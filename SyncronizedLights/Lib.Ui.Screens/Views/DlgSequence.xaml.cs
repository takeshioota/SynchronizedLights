using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// シーケンス実行ダイアログ
    /// 概要：シーケンス個別実行時のパラメータ入力ダイアログ。
    /// 開始番号 / 終了番号 / 遅延時間(ms) / 色（任意）を入力し、OKで確定する。
    /// バリデーション：開始 ≤ 終了、遅延 > 0、すべて整数。
    /// </summary>
    public partial class DlgSequence : Window
    {
        #region プロパティ

        /// <summary>シーケンスID</summary>
        public int SequenceId { get; private set; }

        /// <summary>シーケンス名</summary>
        public string SequenceName { get; private set; } = "";

        /// <summary>開始シリアル番号</summary>
        public int StartNumber { get; private set; } = 1;

        /// <summary>終了シリアル番号</summary>
        public int EndNumber { get; private set; } = 100;

        /// <summary>遅延時間（ms）</summary>
        public int DelayMs { get; private set; } = 100;

        /// <summary>色を使用するか</summary>
        public bool UseColor { get; private set; } = false;

        /// <summary>選択色R</summary>
        public byte ColorR { get; private set; } = 255;

        /// <summary>選択色G</summary>
        public byte ColorG { get; private set; } = 255;

        /// <summary>選択色B</summary>
        public byte ColorB { get; private set; } = 255;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// DlgSequenceを生成する
        /// </summary>
        public DlgSequence()
        {
            InitializeComponent();
            UpdateColorPreview();
        }

        #endregion コンストラクタ

        #region 公開メソッド

        /// <summary>
        /// ダイアログタイトルとシーケンス情報を設定する
        /// </summary>
        public void SetSequence(int id, string name)
        {
            SequenceId = id;
            SequenceName = name;
            TitleLabel.Text = $"シーケンス実行 | {name}";
            Title = $"シーケンス実行 | {name}";
        }

        #endregion 公開メソッド

        #region イベントハンドラ

        /// <summary>入力テキスト変更時：リアルタイム検証で赤枠表示 + メッセージ更新</summary>
        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateInputsForDisplay();
        }

        /// <summary>色選択ボタン押下：DlgColorPickerを開いて色を決定する</summary>
        private void BtnPickColor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new DlgColorPicker { Owner = this };
            dlg.SetInitialColor(ColorR, ColorG, ColorB);
            if (dlg.ShowDialog() == true)
            {
                ColorR = dlg.SelectedR;
                ColorG = dlg.SelectedG;
                ColorB = dlg.SelectedB;
                UpdateColorPreview();
                ChkUseColor.IsChecked = true;
            }
        }

        /// <summary>OK押下：入力値を検証してから確定</summary>
        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            if (!TryValidateAndCommit())
            {
                return;
            }
            DialogResult = true;
            Close();
        }

        /// <summary>Cancel押下：破棄</summary>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion イベントハンドラ

        #region バリデーション

        /// <summary>
        /// 入力値を検証し、エラーがあれば赤枠 + メッセージで表示する。
        /// OK確定前のリアルタイム表示用。
        /// </summary>
        private void ValidateInputsForDisplay()
        {
            // XAMLパース中、各TextBoxの Text="..." 初期値セット時に TextChanged が
            // 先行発火する。このタイミングでは他のTextBoxがまだ未初期化のため null ガードする。
            if (TxtStart == null || TxtEnd == null || TxtDelay == null || TxtValidationMessage == null)
            {
                return;
            }
            var errors = new System.Collections.Generic.List<string>();
            var errorBorder = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC3545"));
            var okBorder = (Brush)FindResource("TextBrushMain");

            // 開始番号
            if (!int.TryParse(TxtStart.Text, out var start) || start < 0)
            {
                TxtStart.BorderBrush = errorBorder;
                TxtStart.BorderThickness = new Thickness(2);
                errors.Add("開始番号は 0 以上の整数にしてください。");
            }
            else
            {
                TxtStart.BorderBrush = okBorder;
                TxtStart.BorderThickness = new Thickness(1);
            }

            // 終了番号
            if (!int.TryParse(TxtEnd.Text, out var end) || end < 0)
            {
                TxtEnd.BorderBrush = errorBorder;
                TxtEnd.BorderThickness = new Thickness(2);
                errors.Add("終了番号は 0 以上の整数にしてください。");
            }
            else if (int.TryParse(TxtStart.Text, out var startOk) && end < startOk)
            {
                TxtEnd.BorderBrush = errorBorder;
                TxtEnd.BorderThickness = new Thickness(2);
                errors.Add("終了番号は開始番号以上にしてください。");
            }
            else
            {
                TxtEnd.BorderBrush = okBorder;
                TxtEnd.BorderThickness = new Thickness(1);
            }

            // 遅延
            if (!int.TryParse(TxtDelay.Text, out var delay) || delay <= 0 || delay > 60000)
            {
                TxtDelay.BorderBrush = errorBorder;
                TxtDelay.BorderThickness = new Thickness(2);
                errors.Add("遅延時間は 1 〜 60000 ms の範囲で入力してください。");
            }
            else
            {
                TxtDelay.BorderBrush = okBorder;
                TxtDelay.BorderThickness = new Thickness(1);
            }

            TxtValidationMessage.Text = string.Join(Environment.NewLine, errors);
        }

        /// <summary>
        /// OK押下時：入力値を検証しプロパティに確定する
        /// </summary>
        private bool TryValidateAndCommit()
        {
            ValidateInputsForDisplay();
            if (!string.IsNullOrEmpty(TxtValidationMessage.Text))
            {
                return false;
            }

            StartNumber = int.Parse(TxtStart.Text);
            EndNumber = int.Parse(TxtEnd.Text);
            DelayMs = int.Parse(TxtDelay.Text);
            UseColor = ChkUseColor.IsChecked == true;
            return true;
        }

        #endregion バリデーション

        #region ヘルパー

        /// <summary>色プレビューを更新する</summary>
        private void UpdateColorPreview()
        {
            ColorPreview.Fill = new SolidColorBrush(Color.FromRgb(ColorR, ColorG, ColorB));
        }

        #endregion ヘルパー
    }
}