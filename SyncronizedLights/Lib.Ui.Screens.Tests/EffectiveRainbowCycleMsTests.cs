using System.Reflection;
using Lib.Domain.Enums;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.12.0 追加開発】Rainbow 入力制限緩和の回帰ガード。
/// 旧仕様(§3.18 注記3)では色切替間隔 cycle を フェード時間(FI/FO) 以上へ自動補正(=256/512等への切上げ)していたが、
/// 追加開発でこの PC 側補正を撤廃し、受付値をそのまま用いる（色切替間隔とフェード時間の整合は端末側へ委ねる）。
/// 本テストは EffectiveRainbowCycleMs が「如何なるモード/フェード時間でも cycle を変えずに返す（パススルー）」ことを縛る。
/// private static メソッドをリフレクションで直接検証（純ロジック＝実機不要）。
/// </summary>
public class EffectiveRainbowCycleMsTests
{
    // private static int EffectiveRainbowCycleMs(RainbowMode mode, int cycleMs, int fadeInMs, int fadeOutMs)
    private static readonly MethodInfo Method =
        typeof(SequenceEditorViewModel).GetMethod(
            "EffectiveRainbowCycleMs",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("EffectiveRainbowCycleMs が見つかりません（メソッド名/シグネチャ変更の可能性）。");

    private static int Invoke(RainbowMode mode, int cycle, int fadeIn, int fadeOut)
        => (int)Method.Invoke(null, new object[] { mode, cycle, fadeIn, fadeOut })!;

    [Fact]
    public void FadeInOut_cycleがFIプラスFO未満でも補正せずそのまま()
    {
        // 旧仕様では 2000 へ切上げていたが、緩和後は受付値 1000 のまま返す。
        Assert.Equal(1000, Invoke(RainbowMode.FadeInOut, 1000, 1000, 1000));
    }

    [Fact]
    public void FadeIn_cycleがFI未満でも補正しない()
    {
        Assert.Equal(100, Invoke(RainbowMode.FadeIn, 100, 500, 0));
    }

    [Fact]
    public void FadeOut_cycleがFO未満でも補正しない()
    {
        Assert.Equal(100, Invoke(RainbowMode.FadeOut, 100, 0, 500));
    }

    [Theory]
    [InlineData(RainbowMode.Solid)]
    [InlineData(RainbowMode.Blink)]
    [InlineData(RainbowMode.FadeIn)]
    [InlineData(RainbowMode.FadeOut)]
    [InlineData(RainbowMode.FadeInOut)]
    [InlineData(RainbowMode.Random)]
    public void 全モードでcycleはパススルー(RainbowMode mode)
    {
        Assert.Equal(300, Invoke(mode, 300, 1000, 1000));
    }

    [Fact]
    public void 下限0や小さい値もそのまま返す()
    {
        // 緩和後は下限補正しない（0/1 もそのまま）。
        Assert.Equal(0, Invoke(RainbowMode.FadeInOut, 0, 1000, 1000));
        Assert.Equal(1, Invoke(RainbowMode.FadeIn, 1, 6000, 0));
    }

    [Fact]
    public void フェード時間が範囲外でもcycleには影響しない()
    {
        // フェード時間(FI/FO)は cycle の計算に一切使われない。
        Assert.Equal(2500, Invoke(RainbowMode.FadeInOut, 2500, 6000, 99999));
    }
}
