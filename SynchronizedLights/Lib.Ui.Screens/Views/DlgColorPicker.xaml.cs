using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// 色選択ダイアログ
    /// </summary>
    public partial class DlgColorPicker : Window
    {
        #region フィールド

        /// <summary>SV キャンバスのピクセルサイズ</summary>
        private const double SvSize = 256;

        /// <summary>Hue バーの高さ</summary>
        private const double HueHeight = 256;

        /// <summary>現在の色相（0〜360）</summary>
        private double _hue = 0;

        /// <summary>現在の彩度（0〜1）</summary>
        private double _saturation = 0;

        /// <summary>現在の明度（0〜1）</summary>
        private double _value = 1;

        /// <summary>初期化中フラグ（通知の循環防止）</summary>
        private bool _isUpdating;

        #endregion フィールド

        #region プロパティ

        /// <summary>選択されたR値</summary>
        public byte SelectedR { get; private set; } = 255;

        /// <summary>選択されたG値</summary>
        public byte SelectedG { get; private set; } = 255;

        /// <summary>選択されたB値</summary>
        public byte SelectedB { get; private set; } = 255;

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// DlgColorPickerを生成する
        /// </summary>
        public DlgColorPicker()
        {
            InitializeComponent();
            InitializeSwatches();
            UpdateSVBase();
            UpdatePreview();
            UpdateSVThumbPosition();
            UpdateHueThumbPosition();
        }

        #endregion コンストラクタ

        #region 公開メソッド

        /// <summary>
        /// 初期色を設定する
        /// 概要：ダイアログを開く前に現在色を設定して、
        /// ピッカーの初期位置をその色に合わせる。
        /// </summary>
        public void SetInitialColor(byte r, byte g, byte b)
        {
            var (h, s, v) = RgbToHsv(r, g, b);
            _isUpdating = true;
            try
            {
                _hue = h;
                _saturation = s;
                _value = v;
                SelectedR = r;
                SelectedG = g;
                SelectedB = b;
            }
            finally
            {
                _isUpdating = false;
            }
            UpdateSVBase();
            UpdateSVThumbPosition();
            UpdateHueThumbPosition();
            UpdatePreview();
        }

        #endregion 公開メソッド

        #region SV ピッカー イベントハンドラ

        /// <summary>SVキャンバス押下</summary>
        private void SVCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SVCanvas.CaptureMouse();
            UpdateSVFromMouse(e.GetPosition(SVCanvas));
        }

        /// <summary>SVキャンバス移動</summary>
        private void SVCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (SVCanvas.IsMouseCaptured)
            {
                UpdateSVFromMouse(e.GetPosition(SVCanvas));
            }
        }

        /// <summary>SVキャンバス離脱</summary>
        private void SVCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            SVCanvas.ReleaseMouseCapture();
        }

        /// <summary>
        /// マウス位置から彩度・明度を更新する
        /// </summary>
        private void UpdateSVFromMouse(Point pos)
        {
            _saturation = Math.Clamp(pos.X / SvSize, 0, 1);
            _value = Math.Clamp(1 - pos.Y / SvSize, 0, 1);
            UpdateSVThumbPosition();
            UpdatePreview();
        }

        #endregion SV ピッカー イベントハンドラ

        #region Hue バー イベントハンドラ

        /// <summary>Hueバー押下</summary>
        private void HueCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            HueCanvas.CaptureMouse();
            UpdateHueFromMouse(e.GetPosition(HueCanvas));
        }

        /// <summary>Hueバー移動</summary>
        private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (HueCanvas.IsMouseCaptured)
            {
                UpdateHueFromMouse(e.GetPosition(HueCanvas));
            }
        }

        /// <summary>Hueバー離脱</summary>
        private void HueCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            HueCanvas.ReleaseMouseCapture();
        }

        /// <summary>
        /// マウス位置から色相を更新する
        /// </summary>
        private void UpdateHueFromMouse(Point pos)
        {
            var y = Math.Clamp(pos.Y, 0, HueHeight);
            _hue = (y / HueHeight) * 360.0;
            UpdateSVBase();
            UpdateHueThumbPosition();
            UpdatePreview();
        }

        #endregion Hue バー イベントハンドラ

        #region UI 更新

        /// <summary>
        /// SVキャンバスの基本色（色相）を更新する
        /// </summary>
        private void UpdateSVBase()
        {
            var (r, g, b) = HsvToRgb(_hue, 1, 1);
            SVBase.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        /// <summary>
        /// SVサムの位置を彩度・明度に基づいて更新する
        /// </summary>
        private void UpdateSVThumbPosition()
        {
            Canvas.SetLeft(SVThumb, _saturation * SvSize - 7);
            Canvas.SetTop(SVThumb, (1 - _value) * SvSize - 7);
        }

        /// <summary>
        /// Hueサムの位置を色相に基づいて更新する
        /// </summary>
        private void UpdateHueThumbPosition()
        {
            Canvas.SetTop(HueThumb, (_hue / 360.0) * HueHeight - 2);
        }

        /// <summary>
        /// 現在のHSVからRGBプレビューを更新する
        /// </summary>
        private void UpdatePreview()
        {
            if (_isUpdating) return;
            var (r, g, b) = HsvToRgb(_hue, _saturation, _value);
            SelectedR = r;
            SelectedG = g;
            SelectedB = b;
            ColorPreview.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
            TxtRgb.Text = $"R: {r}  G: {g}  B: {b}";
            TxtHex.Text = $"#{r:X2}{g:X2}{b:X2}";
        }

        #endregion UI 更新

        #region スウォッチ

        /// <summary>
        /// よく使う色パレットを初期化する
        /// </summary>
        private void InitializeSwatches()
        {
            var swatches = new (byte r, byte g, byte b)[]
            {
                (255,   0,   0),   // Red
                (255, 128,   0),   // Orange
                (255, 255,   0),   // Yellow
                (  0, 255,   0),   // Green
                (  0, 255, 255),   // Cyan
                (  0, 128, 255),   // Sky
                (  0,   0, 255),   // Blue
                (255,   0, 255),   // Magenta
                (255, 255, 255),   // White
                (  0,   0,   0),   // Black
            };

            foreach (var (r, g, b) in swatches)
            {
                var btn = new Button
                {
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    BorderBrush = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand
                };
                var capturedR = r;
                var capturedG = g;
                var capturedB = b;
                btn.Click += (s, e) => SetInitialColor(capturedR, capturedG, capturedB);
                SwatchPanel.Children.Add(btn);
            }
        }

        #endregion スウォッチ

        #region OK / Cancel

        /// <summary>OK押下</summary>
        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        /// <summary>Cancel押下</summary>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion OK / Cancel

        #region 色空間変換

        /// <summary>
        /// HSVからRGBに変換する
        /// </summary>
        /// <param name="h">色相（0〜360）</param>
        /// <param name="s">彩度（0〜1）</param>
        /// <param name="v">明度（0〜1）</param>
        public static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
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

            byte r = (byte)Math.Round((r1 + m) * 255);
            byte g = (byte)Math.Round((g1 + m) * 255);
            byte b = (byte)Math.Round((b1 + m) * 255);
            return (r, g, b);
        }

        /// <summary>
        /// RGBからHSVに変換する
        /// </summary>
        public static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;

            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            double h;
            if (delta < 1e-10) h = 0;
            else if (Math.Abs(max - rd) < 1e-10) h = 60 * (((gd - bd) / delta) % 6);
            else if (Math.Abs(max - gd) < 1e-10) h = 60 * ((bd - rd) / delta + 2);
            else h = 60 * ((rd - gd) / delta + 4);
            if (h < 0) h += 360;

            double s = max < 1e-10 ? 0 : delta / max;
            double v = max;
            return (h, s, v);
        }

        #endregion 色空間変換
    }
}