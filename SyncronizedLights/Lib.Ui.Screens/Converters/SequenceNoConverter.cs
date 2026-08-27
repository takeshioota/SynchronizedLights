using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lib.Ui.Screens.Converters
{
    /// <summary>
    /// シーケンス「No」欄 機能変更仕様（0825）の共通書式ロジック。
    /// 概要：整数部2桁・小数部1桁の「00.0」形式で表示／格納する。
    ///
    ///   仕様定義
    ///     文字種   : 小数点を含む数値
    ///     桁数     : 整数部2桁、小数部1桁
    ///     表示形式 : 00.0（整数部ゼロ埋め＋小数1桁固定）
    ///     範囲     : 00.0～99.9（それ以外は入力エラー）
    ///     空欄     : 不可。空欄で確定した場合は 00.0 として扱う（改訂: 空欄は残さない）。
    ///
    ///   動作（別フィールドへの移動時＝クリック / Enter / →↑↓ で確定）
    ///     "1"     ⇒ 01.0    "01"    ⇒ 01.0    "1.1"   ⇒ 01.1
    ///     "01.10" ⇒ error   "1.11"  ⇒ error   "01.11" ⇒ error（小数2桁以上）
    /// </summary>
    internal static class SequenceNoFormat
    {
        /// <summary>入力エラー時にツールチップ／検証メッセージへ表示する文言。</summary>
        public const string ErrorMessage = "00.0〜99.9 / 小数1桁で入力";

        // 整数部1〜2桁、小数部は任意で「.」＋1桁のみ。小数2桁以上・符号・3桁整数などは弾く。
        private static readonly Regex Pattern =
            new Regex(@"^\d{1,2}(\.\d)?$", RegexOptions.Compiled);

        /// <summary>
        /// 入力文字列を検証して値に変換する。
        ///   空欄            → true / value=0.0（"00.0" として確定。空欄は残さない）
        ///   00.0〜99.9・小数1桁 → true / value=数値
        ///   それ以外        → false（入力エラー）
        /// </summary>
        public static bool TryParse(string? text, out double? value)
        {
            value = null;
            var t = text?.Trim() ?? string.Empty;

            // 空欄は不可。空欄で確定した場合は 00.0（0.0）として扱う。
            if (t.Length == 0) { value = 0.0; return true; }

            // 書式（整数部2桁／小数部1桁）に一致しない → エラー。
            if (!Pattern.IsMatch(t)) return false;

            if (!double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
                return false;

            // 範囲外 → エラー（書式で概ね保証されるが防御的に確認）。
            if (d < 0.0 || d > 99.9) return false;

            value = d;
            return true;
        }

        /// <summary>double? を「00.0」形式へ整形する。null（未設定）は 00.0 とみなす（空欄不可）。</summary>
        public static string Format(double? value)
            => (value ?? 0.0).ToString("00.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// シーケンス編集「No」列専用コンバータ（0825 仕様）。
    /// 概要：
    ///   表示（Convert : double? → string）      null → "00.0"／値 → "00.0" 形式（例: 1 → 01.0）
    ///   入力（ConvertBack : string → double?）   空欄 → 0.0（"00.0"）／有効値 → double
    ///                                            不正値は Binding.DoNothing（元の値を保持）
    ///
    /// 入力エラーの視覚表示（赤枠＋ツールチップ）は <see cref="SequenceNoValidationRule"/> が担当する。
    /// 適用：IntegratedWindow / SequenceEditorWindow の No 列 DataGridTextColumn.Binding。
    ///       バインド先 RowNumber は double?（空欄不可・null は 00.0 として扱う）。
    /// </summary>
    public sealed class SequenceNoConverter : IValueConverter
    {
        /// <summary>XAML から {x:Static} で参照する共有インスタンス。</summary>
        public static readonly SequenceNoConverter Instance = new SequenceNoConverter();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // 空欄は許容しない。null（未設定）は 00.0 として表示する。
            if (value == null) return SequenceNoFormat.Format(null);   // → "00.0"
            try { return SequenceNoFormat.Format(System.Convert.ToDouble(value, CultureInfo.InvariantCulture)); }
            catch { return SequenceNoFormat.Format(null); }            // → "00.0"
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // ValidationRule を通過した文字列のみ到達する想定だが、防御的に再検証する。
            if (SequenceNoFormat.TryParse(value as string, out var d))
                return d;   // 空欄は 0.0（"00.0"）、それ以外は入力された double

            // 不正入力は確定させず、元の値を保持する。
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// シーケンス編集「No」列の入力検証ルール（0825 仕様）。
    /// 概要：小数2桁以上・範囲外（00.0〜99.9 以外）・非数値をエラーとし、
    ///       セル赤枠＋ツールチップで通知する（値は確定させない）。
    ///       空欄はエラーにせず 00.0 として確定する（空欄不可＝残さない）。
    /// </summary>
    public sealed class SequenceNoValidationRule : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            return SequenceNoFormat.TryParse(value as string, out _)
                ? ValidationResult.ValidResult
                : new ValidationResult(false, SequenceNoFormat.ErrorMessage);
        }
    }
}
