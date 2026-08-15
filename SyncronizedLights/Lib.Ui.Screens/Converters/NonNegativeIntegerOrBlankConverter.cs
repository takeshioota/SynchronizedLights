using System;
using System.Globalization;
using System.Windows.Data;

namespace Lib.Ui.Screens.Converters
{
    /// <summary>
    /// シーケンス編集「No」列専用コンバータ。
    /// 概要：No 列には「空欄」または「0 以上の整数」だけを入力できるように制限する。
    ///       将来 No 列のソートを再開しても値が揃うよう、小数・マイナス・任意文字列を弾く。
    ///
    ///   表示（Convert : double → string）
    ///       0 以下・非整数 → 空欄（""）／正の整数 → その数字（安全のため四捨五入して整数化）
    ///
    ///   入力（ConvertBack : string → double）
    ///       空欄            → 0.0（＝未設定。従来どおり 0 は空欄表示）
    ///       0 以上の整数    → その値（double）
    ///       小数・マイナス・非数値（"1.5" / "-3" / "abc" / "1-2-2-1" 等）
    ///                       → Binding.DoNothing（確定させず元の値へ戻す。赤枠固着や例外は起きない）
    ///
    /// 適用：IntegratedWindow / SequenceEditorWindow の No 列 DataGridTextColumn.Binding。
    ///       バインド先 RowNumber は double 型のまま（型変更なし）。
    /// </summary>
    public sealed class NonNegativeIntegerOrBlankConverter : IValueConverter
    {
        /// <summary>XAML から {x:Static} で参照する共有インスタンス。</summary>
        public static readonly NonNegativeIntegerOrBlankConverter Instance = new NonNegativeIntegerOrBlankConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return string.Empty;

            double d;
            try { d = System.Convert.ToDouble(value, culture); }
            catch { return string.Empty; }

            // 0 以下（未設定）は空欄。正の整数のみ数字表示。
            var n = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            return n <= 0 ? string.Empty : n.ToString(culture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = (value as string)?.Trim();

            // 空欄＝未設定（0）。従来の「0 は空欄表示」運用に合わせる。
            if (string.IsNullOrEmpty(text)) return 0.0;

            // NumberStyles.None＝符号・小数点・空白を一切許さない＝「数字のみ＝0 以上の整数」だけが通る。
            //   "1.5" / "-3" / "+3" / "abc" / "1-2-2-1" などは TryParse に失敗する。
            if (int.TryParse(text, NumberStyles.None, culture, out var n))
                return (double)n;

            // 不正入力は確定させず、元の値を保持する。
            return Binding.DoNothing;
        }
    }
}
