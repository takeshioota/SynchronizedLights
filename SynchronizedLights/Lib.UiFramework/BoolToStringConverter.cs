using System.Globalization;
using System.Windows.Data;

namespace Lib.UiFramework;

/// <summary>
/// bool を "True"/"False" 文字列に変換するコンバーター。
/// Tag プロパティ（object型）経由で bool を渡す際の型不一致を解消する。
/// </summary>
[ValueConversion(typeof(bool), typeof(string))]
public sealed class BoolToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "True" : "False";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is "True";
}
