using System.Globalization;
using System.Windows.Data;
using Lib.Ui.Screens.Converters;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.0.0】シーケンス「No」列 小数1桁「00.0」形式コンバータの単体テスト。
/// 対象仕様: シーケンスNO仕様0825.xlsx（整数部2桁・小数部1桁・範囲00.0〜99.9・空欄不可＝00.0扱い）。
/// 純ロジック（実機不要）。画面上の赤枠表示自体は手動UI確認項目。
/// </summary>
public class SequenceNoConverterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly SequenceNoConverter _conv = SequenceNoConverter.Instance;

    // ───────────────────────── Convert（double? → 表示文字列）─────────────────────────

    [Fact]
    public void Convert_nullは00_0()
        => Assert.Equal("00.0", _conv.Convert(null, typeof(string), null, Inv));

    [Theory]
    [InlineData(1.0, "01.0")]
    [InlineData(1.1, "01.1")]
    [InlineData(0.0, "00.0")]
    [InlineData(99.9, "99.9")]
    [InlineData(10.0, "10.0")]
    public void Convert_00_0形式に整形(double value, string expected)
        => Assert.Equal(expected, _conv.Convert(value, typeof(string), null, Inv));

    // ───────────────────────── ConvertBack（入力文字列 → double?）─────────────────────────

    [Theory]
    [InlineData("", 0.0)]      // 空欄は不可＝00.0(0.0)として確定
    [InlineData("1", 1.0)]
    [InlineData("01", 1.0)]
    [InlineData("1.1", 1.1)]
    [InlineData("01.1", 1.1)]
    [InlineData("00.0", 0.0)]
    [InlineData("99.9", 99.9)]
    public void ConvertBack_有効値はdoubleへ(string input, double expected)
    {
        var result = _conv.ConvertBack(input, typeof(double?), null, Inv);
        Assert.Equal(expected, Assert.IsType<double>(result));
    }

    [Theory]
    [InlineData("01.10")]  // 小数2桁
    [InlineData("1.11")]   // 小数2桁
    [InlineData("100")]    // 整数3桁
    [InlineData("100.0")]  // 整数3桁
    [InlineData("-1")]     // 符号
    [InlineData("abc")]    // 非数値
    public void ConvertBack_不正値はBindingDoNothing_元値保持(string input)
    {
        var result = _conv.ConvertBack(input, typeof(double?), null, Inv);
        Assert.Same(Binding.DoNothing, result);
    }

    // ───────────────────────── ValidationRule（赤枠表示の判定ロジック）─────────────────────────

    [Theory]
    [InlineData("")]       // 空欄はエラーにしない（00.0扱い）
    [InlineData("1")]
    [InlineData("01.0")]
    [InlineData("99.9")]
    public void ValidationRule_有効値はValid(string input)
    {
        var rule = new SequenceNoValidationRule();
        Assert.True(rule.Validate(input, Inv).IsValid);
    }

    [Theory]
    [InlineData("01.10")]
    [InlineData("100")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void ValidationRule_不正値はInvalid(string input)
    {
        var rule = new SequenceNoValidationRule();
        Assert.False(rule.Validate(input, Inv).IsValid);
    }
}
