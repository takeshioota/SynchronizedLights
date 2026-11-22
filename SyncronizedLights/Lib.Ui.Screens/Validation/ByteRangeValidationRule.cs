using System.Globalization;
using System.Windows.Controls;

namespace Lib.Ui.Screens.Validation
{
    /// <summary>
    /// No.45(B-20261120-05): グリッドの R/G/B セル等、0〜255 の整数しか受け付けない入力の検証。
    /// 範囲外・非整数・空は不正とし、ソース更新（＝保存）を拒否してセルにエラー表示（赤枠）する。
    ///
    /// 旧実装は byte プロパティへ UpdateSourceTrigger=PropertyChanged でバインドしていたため、
    /// 「256」を打つと 2→25→(256 は byte 変換失敗で無視)となり、直前の有効値 25 が保存される
    /// （末尾1桁ドロップ＝見かけ上 ÷10）不具合があった。本ルール＋UpdateSourceTrigger=LostFocus で
    /// 「確定時に一括検証し、範囲外はそのまま弾く」挙動に是正する。
    /// </summary>
    public class ByteRangeValidationRule : ValidationRule
    {
        /// <summary>許容下限（既定 0）。</summary>
        public int Min { get; set; } = 0;

        /// <summary>許容上限（既定 255）。</summary>
        public int Max { get; set; } = 255;

        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ValidationResult(false, $"{Min}〜{Max} の整数を入力してください。");
            }

            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                return new ValidationResult(false, "整数を入力してください。");
            }

            if (n < Min || n > Max)
            {
                return new ValidationResult(false, $"{Min}〜{Max} の範囲で入力してください（現在 {n}）。");
            }

            return ValidationResult.ValidResult;
        }
    }
}
