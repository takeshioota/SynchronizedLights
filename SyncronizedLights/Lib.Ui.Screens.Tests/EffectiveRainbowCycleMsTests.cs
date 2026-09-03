using System.Reflection;
using Lib.Domain.Enums;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.0.0】Rainbow FI/FO の色切替間隔クランプ(EffectiveRainbowCycleMs)の単体テスト。
/// 対象仕様: 仕様§3.18 注記3「フレーム移行時間はフェード時間以上／未満禁止」。
///   色切替間隔 cycle が フェード時間(FI/FO) 未満だと、フェード途中で色フレームが前進し
///   端末が不定位相でフェードを開始する（光はじめ不定）。cycle ≧ フェード時間 にクランプする。
/// private static メソッドをリフレクションで直接検証（純ロジック＝実機不要）。
/// 実際の“光はじめ”の見た目は実機確認項目。
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
    public void FadeInOut_cycleがFIプラスFO未満なら合計へクランプ()
    {
        // 既定 cycle=1000 < FI(1000)+FO(1000)=2000 → 2000 に補正（バグの本症状）
        Assert.Equal(2000, Invoke(RainbowMode.FadeInOut, 1000, 1000, 1000));
    }

    [Fact]
    public void FadeInOut_cycleが十分大きければそのまま()
    {
        Assert.Equal(5000, Invoke(RainbowMode.FadeInOut, 5000, 1000, 1000));
    }

    [Fact]
    public void FadeIn_cycleはFI以上にクランプ()
    {
        Assert.Equal(500, Invoke(RainbowMode.FadeIn, 100, 500, 0));
    }

    [Fact]
    public void FadeOut_cycleはFO以上にクランプ()
    {
        Assert.Equal(500, Invoke(RainbowMode.FadeOut, 100, 0, 500));
    }

    [Theory]
    [InlineData(RainbowMode.Solid)]
    [InlineData(RainbowMode.Blink)]
    public void 非フェードモードはクランプしない(RainbowMode mode)
    {
        // minCycle=0 → cycle をそのまま返す
        Assert.Equal(300, Invoke(mode, 300, 1000, 1000));
    }

    [Fact]
    public void フェード時間は下限256にクランプしてから評価()
    {
        // FI=100 は 256 に引き上げられる → FadeIn の下限は 256
        Assert.Equal(256, Invoke(RainbowMode.FadeIn, 100, 100, 0));
    }

    [Fact]
    public void フェード時間は上限3000にクランプしてから評価()
    {
        // FI=5000 は 3000 に抑えられる → FadeIn の下限は 3000
        Assert.Equal(3000, Invoke(RainbowMode.FadeIn, 100, 5000, 0));
    }

    [Fact]
    public void cycleが0なら0のまま_ガード()
    {
        Assert.Equal(0, Invoke(RainbowMode.FadeInOut, 0, 1000, 1000));
    }
}
