using Lib.Application.Models;
using Lib.Domain.Enums;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// SequenceStepWrapper の耐久・反復テスト（純ロジック＝実機不要）。
///  ・Cmd 列の Color⇄Rainbow 高速切替でパレット実体化が壊れない／余計に増えない（冪等）
///  ・Wrapper→ToModel→Wrapper のラウンドトリップを多数回繰り返しても主要値がドリフトしない
///  ・パレットの add/remove を多数回繰り返してもサマリ／表示が破綻しない
///  ・BPM を広範囲にスイープしても常に 0〜6000 にクランプされる
/// を確認する。
/// </summary>
public class SequenceStepWrapperEnduranceTests
{
    [Fact]
    public void Cmd列のColorとRainbow切替を5000回繰り返しても実体化が冪等()
    {
        var w = new SequenceStepWrapper { CommandType = "Color" };
        for (int i = 0; i < 5000; i++)
        {
            w.CommandType = "Rainbow";
            Assert.True(w.RainbowColors.Count >= 2, $"iter {i}: Rainbow 行はパレットが実体化される");
            w.CommandType = "Color";
        }
        w.CommandType = "Rainbow";
        // 一度実体化された 7 色は往復では増減しない（冪等）。
        Assert.Equal(7, w.RainbowColors.Count);
        Assert.Equal("7色 (常時)", w.RainbowSummary);
    }

    [Fact]
    public void ToModelラウンドトリップを1万回繰り返しても主要値がドリフトしない()
    {
        var w = new SequenceStepWrapper
        {
            TimeMs = 4567,
            CommandType = "Effect",
            EffectType = "SevenColor",
            EffectCycleDurationMs = 1000,
            Bpm = 123,
            RowNumber = 45.6,
            Comment = "耐久",
        };

        for (int i = 0; i < 10_000; i++)
            w = new SequenceStepWrapper(w.ToModel());

        Assert.Equal(4567, w.TimeMs);
        Assert.Equal("Effect", w.CommandType);
        Assert.Equal("SevenColor", w.EffectType);
        Assert.Equal(1000, w.EffectCycleDurationMs);
        Assert.Equal(123, w.Bpm);
        Assert.Equal(45.6, w.RowNumber!.Value, precision: 6);
        Assert.Equal("耐久", w.Comment);
    }

    [Fact]
    public void パレットのadd_removeを多数回繰り返してもサマリが色数に追従する()
    {
        var w = new SequenceStepWrapper { CommandType = "Rainbow", RainbowMode = RainbowMode.Solid };
        for (int i = 0; i < 2000; i++)
        {
            int before = w.RainbowColors.Count;
            w.RainbowColors.Add(new RgbColorItem((byte)i, 0, 0));
            Assert.Equal(before + 1, w.RainbowColors.Count);
            w.RainbowColors.RemoveAt(w.RainbowColors.Count - 1);
            Assert.Equal(before, w.RainbowColors.Count);
        }
        // 既定7色のまま。サマリは "7色 (常時)"。
        Assert.Equal("7色 (常時)", w.RainbowSummary);
    }

    [Fact]
    public void BPMを広範囲にスイープしても常に0から6000にクランプされる()
    {
        var w = new SequenceStepWrapper();
        for (int bpm = -10_000; bpm <= 10_000; bpm += 7)
        {
            w.Bpm = bpm;
            Assert.InRange(w.Bpm, 0, 6000);
            int expected = Math.Clamp(bpm, 0, 6000);
            Assert.Equal(expected, w.Bpm);
        }
    }

    [Fact]
    public void TimeMsを負から大きい値までスイープしても負にならない()
    {
        var w = new SequenceStepWrapper();
        for (int ms = -5000; ms <= 5000; ms += 3)
        {
            w.TimeMs = ms;
            Assert.Equal(Math.Max(0, ms), w.TimeMs);
        }
    }

    [Fact]
    public void 大量のWrapper生成でも例外なく既定値が安定する()
    {
        for (int i = 0; i < 50_000; i++)
        {
            var w = new SequenceStepWrapper();
            Assert.Equal(0, w.Bpm);
            Assert.Equal(0.0, w.RowNumber);
            Assert.Equal("Color", w.CommandType);
        }
    }
}
