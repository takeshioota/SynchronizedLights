using System.IO;
using System.Reflection;
using Lib.Domain.Enums;
using Lib.Ui.Screens.ViewModels;
using Lib.Ui.Screens.Views;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.12.0 追加開発】Rainbow 入力制限緩和の回帰ガード。
/// 下限の独自補正を撤廃し受付値をそのまま保持・送出する一方、上限5000ms・整数・色数2〜7・比率0〜9 の
/// 形式制約は維持し、範囲外は丸めずエラーとする（設計書 第3/5章）。
/// DlgRainbowRowEditor.ValidateRow（private static・純ロジック＝実機不要）をリフレクションで検証する。
/// </summary>
public class RainbowInputRelaxationTests
{
    // private static bool ValidateRow(SequenceStepWrapper row, out string error)
    private static readonly MethodInfo ValidateRowMethod =
        typeof(DlgRainbowRowEditor).GetMethod(
            "ValidateRow",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ValidateRow が見つかりません（メソッド名/シグネチャ変更の可能性）。");

    private static bool ValidateRow(SequenceStepWrapper row, out string error)
    {
        var args = new object?[] { row, null };
        bool ok = (bool)ValidateRowMethod.Invoke(null, args)!;
        error = (string)args[1]!;
        return ok;
    }

    private static SequenceStepWrapper NewValidRow()
    {
        var w = new SequenceStepWrapper
        {
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.FadeInOut,
            RainbowCycleDurationMs = 1000,
            RainbowBlinkPeriodMs = 500,
            RainbowFadeInMs = 1000,
            RainbowFadeOutMs = 1000,
            RainbowDutyRatio = 5,
        };
        w.RainbowColors.Clear();
        w.RainbowColors.Add(new RgbColorItem(255, 0, 0));
        w.RainbowColors.Add(new RgbColorItem(0, 255, 0));
        return w;
    }

    [Theory]
    [InlineData(0)]     // 下限0は許容（補正しない）
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]    // 仕様下限未満でも受付（端末補正に委ねる）
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(5000)]  // 上限ちょうどは許容
    public void 時間は0から5000を許容する(int t)
    {
        var w = NewValidRow();
        w.RainbowCycleDurationMs = t;
        w.RainbowBlinkPeriodMs = t;
        w.RainbowFadeInMs = t;
        w.RainbowFadeOutMs = t;
        Assert.True(ValidateRow(w, out _));
    }

    [Theory]
    [InlineData(5001)]   // 上限超過は不可
    [InlineData(65535)]
    [InlineData(-1)]     // 負数は不可
    public void 時間の範囲外は丸めずエラーになる(int t)
    {
        var w = NewValidRow();
        w.RainbowFadeInMs = t;
        Assert.False(ValidateRow(w, out string err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void 点灯比率は0を許容する()
    {
        var w = NewValidRow();
        w.RainbowDutyRatio = 0; // 下限0は維持（端末側で1に補正）
        Assert.True(ValidateRow(w, out _));
    }

    [Theory]
    [InlineData(1)] // 色数1はエラー（最低2）
    public void 色数が2未満はエラー(int n)
    {
        var w = NewValidRow();
        while (w.RainbowColors.Count > n) w.RainbowColors.RemoveAt(w.RainbowColors.Count - 1);
        Assert.False(ValidateRow(w, out _));
    }

    [Fact]
    public void 色数が7超はエラー()
    {
        var w = NewValidRow();
        while (w.RainbowColors.Count < 8) w.RainbowColors.Add(new RgbColorItem(1, 2, 3));
        Assert.False(ValidateRow(w, out _));
    }

    [Fact]
    public void 点灯比率0はToModelでも0のまま保持される()
    {
        var w = new SequenceStepWrapper
        {
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.Blink,
            RainbowBlinkPeriodMs = 800,
            RainbowDutyRatio = 0,
        };
        var m = w.ToModel();
        Assert.Equal((byte?)0, m.RainbowDutyRatio);
    }

    // 点灯比率の下限は 0。行エディタ用(TimeSequenceViewModel)・コマンドパネル用(SequenceEditorViewModel) の
    // 両方の setter が下限 1 ではなく 0 でクランプしていること（下限 1 への逆戻りを防ぐ回帰ガード）。
    [Theory]
    [InlineData("ViewModels", "TimeSequenceViewModel.cs")]
    [InlineData("ViewModels", "SequenceEditorViewModel.cs")]
    public void 点灯比率setterの下限は0である(string sub, string file)
    {
        var src = ReadSource("Lib.Ui.Screens", sub, file);
        Assert.Contains("Math.Clamp(value, (byte)0, (byte)9)", src);
        Assert.DoesNotContain("Math.Clamp(value, (byte)1, (byte)9)", src);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        Assert.Fail($"ソースが見つかりません: {Path.Combine(relativeParts)}");
        return string.Empty;
    }
}
