using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Lib.Ui.Screens.Views
{
    /// <summary>
    /// 色選択ダイアログ
    /// 部品 ID:
    ///   PckSV       = 彩度×明度ピッカー（Canvas）
    ///   SldHue      = 色相バー（Canvas）
    ///   LstSwatches = よく使う色リスト（UniformGrid）
    ///   BtnOK / BtnCancel = 確定・破棄ボタン
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

        /// <summary>v3.9: 0.5秒停止検出タイマー（カーソル停止時に実機送信）</summary>
        private readonly DispatcherTimer _idleTimer;

        /// <summary>RGBスライダーからの更新中フラグ</summary>
        private bool _isRgbSliderUpdating;

        #endregion フィールド

        #region プロパティ

        /// <summary>選択されたR値</summary>
        public byte SelectedR { get; private set; } = 255;

        /// <summary>選択されたG値</summary>
        public byte SelectedG { get; private set; } = 255;

        /// <summary>選択されたB値</summary>
        public byte SelectedB { get; private set; } = 255;

        /// <summary>
        /// 色変更時のリアルタイムコールバック
        /// 概要：ピッカー操作のたびに (R, G, B) を通知する。
        ///       実機への即時反映に使用。
        /// </summary>
        public Action<byte, byte, byte>? OnColorChanged { get; set; }

        #endregion プロパティ

        #region コンストラクタ

        /// <summary>
        /// DlgColorPickerを生成する
        /// </summary>
        public DlgColorPicker()
        {
            InitializeComponent();
            InitializeSwatches();

            // v3.9: 0.5秒停止検出タイマー
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _idleTimer.Tick += IdleTimer_Tick;

            UpdateSVBase();
            UpdatePreview();
            UpdateSVThumbPosition();
            UpdateHueThumbPosition();
            UpdateRgbSliders();
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
            // 2026-05-30 修正: 初期化時に OnColorChanged が発火して意図しない色送信が発生するのを防止
            // --- 修正前 ---
            // UpdatePreview();
            // --- 修正後: コールバックを一時退避して UpdatePreview → 復元 ---
            var savedCallback = OnColorChanged;
            OnColorChanged = null;
            UpdatePreview();
            OnColorChanged = savedCallback;
        }

        #endregion 公開メソッド

        #region PckSV（彩度×明度ピッカー）イベントハンドラ

        /// <summary>PckSV 押下</summary>
        private void PckSV_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            PckSV.CaptureMouse();
            UpdateSVFromMouse(e.GetPosition(PckSV));
        }

        /// <summary>PckSV 移動</summary>
        private void PckSV_MouseMove(object sender, MouseEventArgs e)
        {
            if (PckSV.IsMouseCaptured)
            {
                UpdateSVFromMouse(e.GetPosition(PckSV));
            }
        }

        /// <summary>PckSV 離脱</summary>
        private void PckSV_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            PckSV.ReleaseMouseCapture();
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

        #endregion PckSV イベントハンドラ

        #region SldHue（色相バー）イベントハンドラ

        /// <summary>SldHue 押下</summary>
        private void SldHue_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SldHue.CaptureMouse();
            UpdateHueFromMouse(e.GetPosition(SldHue));
        }

        /// <summary>SldHue 移動</summary>
        private void SldHue_MouseMove(object sender, MouseEventArgs e)
        {
            if (SldHue.IsMouseCaptured)
            {
                UpdateHueFromMouse(e.GetPosition(SldHue));
            }
        }

        /// <summary>SldHue 離脱</summary>
        private void SldHue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            SldHue.ReleaseMouseCapture();
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

        #endregion SldHue イベントハンドラ

        #region UI 更新

        /// <summary>
        /// SV ピッカー（PckSV）の基本色（色相）を更新する
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
            UpdateRgbSliders();

            // v3.9: 即時送信ではなく 0.5 秒停止検出で送信
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        /// <summary>
        /// v3.9: RGBスライダーの値を現在のRGB値に同期する
        /// </summary>
        private void UpdateRgbSliders()
        {
            if (_isRgbSliderUpdating) return;
            _isRgbSliderUpdating = true;
            try
            {
                if (SldR != null) SldR.Value = SelectedR;
                if (SldG != null) SldG.Value = SelectedG;
                if (SldB != null) SldB.Value = SelectedB;
                if (TxtR != null) TxtR.Text = SelectedR.ToString();
                if (TxtG != null) TxtG.Text = SelectedG.ToString();
                if (TxtB != null) TxtB.Text = SelectedB.ToString();
            }
            finally { _isRgbSliderUpdating = false; }
        }

        /// <summary>
        /// v3.9: RGBスライダー変更ハンドラ
        /// </summary>
        private void SldRgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating || _isRgbSliderUpdating) return;
            if (SldR == null || SldG == null || SldB == null) return;

            byte r = (byte)Math.Round(SldR.Value);
            byte g = (byte)Math.Round(SldG.Value);
            byte b = (byte)Math.Round(SldB.Value);

            SetColorFromRgb(r, g, b);
        }

        /// <summary>
        /// v3.9: RGBテキスト変更ハンドラ
        /// </summary>
        private void TxtRgb_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _isRgbSliderUpdating) return;
            if (TxtR == null || TxtG == null || TxtB == null) return;

            if (!byte.TryParse(TxtR.Text, out byte r)) return;
            if (!byte.TryParse(TxtG.Text, out byte g)) return;
            if (!byte.TryParse(TxtB.Text, out byte b)) return;

            SetColorFromRgb(r, g, b);
        }

        /// <summary>
        /// RGB値からHSVを逆算してピッカーを更新する
        /// </summary>
        private void SetColorFromRgb(byte r, byte g, byte b)
        {
            _isUpdating = true;
            try
            {
                var (h, s, v) = RgbToHsv(r, g, b);
                _hue = h;
                _saturation = s;
                _value = v;
                SelectedR = r;
                SelectedG = g;
                SelectedB = b;

                UpdateSVBase();
                UpdateSVThumbPosition();
                UpdateHueThumbPosition();
                ColorPreview.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
                TxtRgb.Text = $"R: {r}  G: {g}  B: {b}";
                TxtHex.Text = $"#{r:X2}{g:X2}{b:X2}";

                _isRgbSliderUpdating = true;
                try
                {
                    if (SldR != null) SldR.Value = r;
                    if (SldG != null) SldG.Value = g;
                    if (SldB != null) SldB.Value = b;
                    if (TxtR != null) TxtR.Text = r.ToString();
                    if (TxtG != null) TxtG.Text = g.ToString();
                    if (TxtB != null) TxtB.Text = b.ToString();
                }
                finally { _isRgbSliderUpdating = false; }
            }
            finally { _isUpdating = false; }

            // 停止検出タイマーリセット
            _idleTimer.Stop();
            _idleTimer.Start();
        }

        /// <summary>
        /// v3.9: 0.5秒カーソル停止検出 → 実機送信
        /// </summary>
        private void IdleTimer_Tick(object? sender, EventArgs e)
        {
            _idleTimer.Stop();
            OnColorChanged?.Invoke(SelectedR, SelectedG, SelectedB);
        }

        #endregion UI 更新

        #region LstSwatches

        /// <summary>
        /// よく使う色パレット（LstSwatches）を初期化する
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
                LstSwatches.Children.Add(btn);
            }
        }

        #endregion LstSwatches

        #region OK / Cancel

        /// <summary>OK押下</summary>
        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            // アイドルタイマーが保留中の場合、最終色を即時コールバックしてから閉じる
            _idleTimer.Stop();
            OnColorChanged?.Invoke(SelectedR, SelectedG, SelectedB);

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