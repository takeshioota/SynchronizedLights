using Lib.Application.Models;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// SequenceStepWrapper の追加単体テスト（既存 SequenceStepWrapperTests の補完）。
///  ・型別クランプ（EffectCycle 種別下限・DutyRatio・TransitionMs・FadeSteps）
///  ・TimeSec ⇄ TimeMs 変換
///  ・RainbowModeIndex の範囲ガード
///  ・ToModel の項目ゲーティング（Color2/Preset/InternalProgram/Rainbow モード別）
///  ・Wrapper→ToModel→Wrapper のラウンドトリップ整合
/// 純ロジック（実機不要）。
/// </summary>
public class SequenceStepWrapperMoreTests
{
    // ───────────────────────── クランプ ─────────────────────────

    [Theory]
    [InlineData("SevenColor", 50, 80)]
    [InlineData("Breathing", 10, 30)]
    [InlineData("Flash", 5, 20)]
    [InlineData("Flash", 500, 500)]
    public void EffectCycleDurationMs_Effect時は種別下限でクランプ(string effect, int input, int expected)
    {
        var w = new SequenceStepWrapper { CommandType = "Effect", EffectType = effect, EffectCycleDurationMs = input };
        Assert.Equal(expected, w.EffectCycleDurationMs);
    }

    [Fact]
    public void EffectCycleDurationMs_非Effect行は下限0()
    {
        var w = new SequenceStepWrapper { CommandType = "Color", EffectCycleDurationMs = 5 };
        Assert.Equal(5, w.EffectCycleDurationMs);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 9)]
    [InlineData(5, 5)]
    public void RainbowDutyRatio_1から9にクランプ(int input, int expected)
    {
        var w = new SequenceStepWrapper { RainbowDutyRatio = (byte)input };
        Assert.Equal((byte)expected, w.RainbowDutyRatio);
    }

    [Fact]
    public void TransitionMsとFadeStepsは負値を0にクランプ()
    {
        var w = new SequenceStepWrapper { TransitionMs = -100, FadeSteps = -5 };
        Assert.Equal(0, w.TransitionMs);
        Assert.Equal(0, w.FadeSteps);
    }

    // ───────────────────────── TimeSec ⇄ TimeMs ─────────────────────────

    [Fact]
    public void TimeSec設定はTimeMsへ変換される()
    {
        var w = new SequenceStepWrapper { TimeSec = 1.5 };
        Assert.Equal(1500, w.TimeMs);
    }

    [Fact]
    public void TimeSecにnull設定でTimeMsは0()
    {
        var w = new SequenceStepWrapper { TimeMs = 5000, TimeSec = null };
        Assert.Equal(0, w.TimeMs);
    }

    // ───────────────────────── RainbowModeIndex 範囲ガード ─────────────────────────

    [Fact]
    public void RainbowModeIndex_有効範囲は反映する()
    {
        var w = new SequenceStepWrapper { RainbowModeIndex = 3 };
        Assert.Equal(RainbowMode.FadeIn, w.RainbowMode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void RainbowModeIndex_範囲外は無視される(int invalid)
    {
        var w = new SequenceStepWrapper { RainbowMode = RainbowMode.Blink };
        w.RainbowModeIndex = invalid;
        Assert.Equal(RainbowMode.Blink, w.RainbowMode); // 変化しない
    }

    // ───────────────────────── ToModel: 項目ゲーティング ─────────────────────────

    [Fact]
    public void ToModel_Color2フィールドはColor2行のみ保存()
    {
        var c2 = new SequenceStepWrapper { CommandType = "Color2", Color2R = 100, Color2G = 110, Color2B = 120, Bpm = 90 };
        var m = c2.ToModel();
        Assert.Equal((byte?)100, m.Color2R);
        Assert.Equal((byte?)110, m.Color2G);
        Assert.Equal((byte?)120, m.Color2B);
        Assert.Equal(90, m.Bpm); // BPM は Chase/OL 兼用のため常時保存

        var color = new SequenceStepWrapper { CommandType = "Color", Color2R = 100 };
        var mc = color.ToModel();
        Assert.Null(mc.Color2R);
        Assert.Null(mc.Color2G);
        Assert.Null(mc.Color2B);
    }

    [Fact]
    public void ToModel_PresetNameはPreset行のみ_FrameNoはInternalProgram行のみ()
    {
        var preset = new SequenceStepWrapper { CommandType = "Preset", PresetSequenceName = "サブ1" };
        Assert.Equal("サブ1", preset.ToModel().PresetSequenceName);
        Assert.Equal("", new SequenceStepWrapper { CommandType = "Color", PresetSequenceName = "サブ1" }.ToModel().PresetSequenceName);

        var ip = new SequenceStepWrapper { CommandType = "InternalProgram", FrameNo = 42 };
        Assert.Equal((uint?)42, ip.ToModel().FrameNo);
        Assert.Null(new SequenceStepWrapper { CommandType = "Color", FrameNo = 42 }.ToModel().FrameNo);
    }

    [Fact]
    public void ToModel_Effect連続フラグは非Effect行でnull化()
    {
        var eff = new SequenceStepWrapper { CommandType = "Effect", EffectType = "Flash", Continuous = false };
        Assert.False(eff.ToModel().Continuous);

        var color = new SequenceStepWrapper { CommandType = "Color", Continuous = false };
        Assert.Null(color.ToModel().Continuous);
    }

    [Fact]
    public void ToModel_RainbowはBlink専用項目をモードで出し分ける()
    {
        var blink = new SequenceStepWrapper
        {
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.Blink,
            RainbowBlinkPeriodMs = 800,
            RainbowDutyRatio = 7,
        };
        var m = blink.ToModel();
        Assert.Equal(800, m.RainbowBlinkPeriodMs);
        Assert.Equal((byte?)7, m.RainbowDutyRatio);
        Assert.Null(m.RainbowFadeInMs);
        Assert.Null(m.RainbowFadeOutMs);
    }

    [Fact]
    public void ToModel_RainbowはFadeInOut専用項目をモードで出し分ける()
    {
        var fio = new SequenceStepWrapper
        {
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.FadeInOut,
            RainbowFadeInMs = 1200,
            RainbowFadeOutMs = 1300,
        };
        var m = fio.ToModel();
        Assert.Equal(1200, m.RainbowFadeInMs);
        Assert.Equal(1300, m.RainbowFadeOutMs);
        Assert.Null(m.RainbowBlinkPeriodMs);
        Assert.Null(m.RainbowDutyRatio);
    }

    [Fact]
    public void ToModel_FadeInモードはFadeOut時間を出さない()
    {
        var fi = new SequenceStepWrapper
        {
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.FadeIn,
            RainbowFadeInMs = 900,
            RainbowFadeOutMs = 900,
        };
        var m = fi.ToModel();
        Assert.Equal(900, m.RainbowFadeInMs);
        Assert.Null(m.RainbowFadeOutMs);
    }

    [Fact]
    public void ToModel_非Rainbow行はRainbow項目を全てnull化()
    {
        var w = new SequenceStepWrapper { CommandType = "Color" };
        var m = w.ToModel();
        Assert.Null(m.RainbowMode);
        Assert.Null(m.RainbowColors);
        Assert.Null(m.RainbowCycleDurationMs);
    }

    // ───────────────────────── ラウンドトリップ ─────────────────────────

    [Fact]
    public void ラウンドトリップ_Wrapper_ToModel_Wrapperで主要項目が保存される()
    {
        var original = new SequenceStepWrapper
        {
            TimeMs = 3200,
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.FadeInOut,
            RainbowFadeInMs = 1111,
            RainbowFadeOutMs = 2222,
            RainbowCycleDurationMs = 1500,
            Comment = "OP",
            RowNumber = 12.3,
            Bpm = 240,
        };

        var restored = new SequenceStepWrapper(original.ToModel());

        Assert.Equal(3200, restored.TimeMs);
        Assert.Equal("Rainbow", restored.CommandType);
        Assert.Equal(RainbowMode.FadeInOut, restored.RainbowMode);
        Assert.Equal(1111, restored.RainbowFadeInMs);
        Assert.Equal(2222, restored.RainbowFadeOutMs);
        Assert.Equal(1500, restored.RainbowCycleDurationMs);
        Assert.Equal("OP", restored.Comment);
        Assert.Equal(12.3, restored.RowNumber!.Value, precision: 6);
        Assert.Equal(240, restored.Bpm);
    }

    [Fact]
    public void ラウンドトリップ_RowNumberの小数と色パレットが保持される()
    {
        var w = new SequenceStepWrapper { CommandType = "Rainbow", RainbowMode = RainbowMode.Solid };
        w.RainbowColors.Clear();
        w.RainbowColors.Add(new RgbColorItem(10, 20, 30));
        w.RainbowColors.Add(new RgbColorItem(40, 50, 60));
        w.RainbowColors.Add(new RgbColorItem(70, 80, 90));

        var restored = new SequenceStepWrapper(w.ToModel());
        Assert.Equal(3, restored.RainbowColors.Count);
        Assert.Equal((byte)40, restored.RainbowColors[1].R);
        Assert.Equal((byte)50, restored.RainbowColors[1].G);
        Assert.Equal((byte)60, restored.RainbowColors[1].B);
    }
}
