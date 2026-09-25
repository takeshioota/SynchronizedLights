using Lib.Application.Models;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 永続化モデル(Lib.Application.Models.SequenceStep)の単体テスト。
///  ・Effect 周期の下限クランプ（GetMinEffectCycleMs / GetEffectCycleDurationOrDefault）
///  ・Fade ステップ自動算出（GetFadeStepsOrDefault）
///  ・旧 JSON からのマイグレーション（Command / DurationMs / InterpolationIntervalMs）
///  ・FadeInHold/FadeOutHold の正規化
/// を検証する。純ロジック（実機不要）。
/// </summary>
public class SequenceStepModelTests
{
    // ───────────────────────── GetMinEffectCycleMs ─────────────────────────

    [Theory]
    [InlineData("SevenColor", 80)]
    [InlineData("Breathing", 30)]
    [InlineData("Flash", 20)]
    [InlineData("FadeIn", 20)]
    [InlineData("FadeOut", 20)]
    [InlineData("Color", 0)]
    [InlineData(null, 0)]
    [InlineData("Unknown", 0)]
    public void GetMinEffectCycleMs_種別ごとの下限(string? effectType, int expected)
        => Assert.Equal(expected, SequenceStep.GetMinEffectCycleMs(effectType));

    // ───────────────────────── GetEffectCycleDurationOrDefault ─────────────────────────

    [Fact]
    public void EffectCycle既定_未設定Effectは1000へ()
    {
        var s = new SequenceStep { CommandType = "Effect", EffectType = "Flash", EffectCycleDurationMs = null };
        Assert.Equal(1000, s.GetEffectCycleDurationOrDefault());
    }

    [Theory]
    [InlineData("SevenColor", 50, 80)]   // 下限80へ引き上げ
    [InlineData("Breathing", 10, 30)]    // 下限30へ
    [InlineData("Flash", 5, 20)]         // 下限20へ
    [InlineData("Flash", 500, 500)]      // 下限以上はそのまま
    public void EffectCycle_Effect時は種別下限でクランプ(string effect, int input, int expected)
    {
        var s = new SequenceStep { CommandType = "Effect", EffectType = effect, EffectCycleDurationMs = input };
        Assert.Equal(expected, s.GetEffectCycleDurationOrDefault());
    }

    [Fact]
    public void EffectCycle_非Effect行は下限クランプしない()
    {
        var s = new SequenceStep { CommandType = "Color", EffectCycleDurationMs = 10 };
        Assert.Equal(10, s.GetEffectCycleDurationOrDefault());
    }

    // ───────────────────────── GetFadeStepsOrDefault ─────────────────────────

    [Fact]
    public void FadeSteps_明示指定があればそれを使う()
    {
        var s = new SequenceStep { CommandType = "Effect", EffectType = "Flash", FadeSteps = 25 };
        Assert.Equal(25, s.GetFadeStepsOrDefault());
    }

    [Theory]
    [InlineData(200, 10)]    // 200/20=10
    [InlineData(100, 10)]    // 5→下限10へクランプ
    [InlineData(5000, 150)]  // 250→上限150へクランプ
    [InlineData(2000, 100)]  // 100
    public void FadeSteps_未設定は周期から自動算出し10から150にクランプ(int cycle, int expected)
    {
        var s = new SequenceStep { CommandType = "Effect", EffectType = "Flash", FadeSteps = null, EffectCycleDurationMs = cycle };
        Assert.Equal(expected, s.GetFadeStepsOrDefault());
    }

    // ───────────────────────── IsEffect ─────────────────────────

    [Theory]
    [InlineData("Effect", true)]
    [InlineData("Color", false)]
    [InlineData("Rainbow", false)]
    public void IsEffect_CommandTypeで判定(string cmd, bool expected)
        => Assert.Equal(expected, new SequenceStep { CommandType = cmd }.IsEffect);

    // ───────────────────────── マイグレーション: Command ─────────────────────────

    [Theory]
    [InlineData("SetColor", "Color", null)]
    [InlineData("Off", "Off", null)]
    [InlineData("Flash", "Effect", "Flash")]
    [InlineData("FadeIn", "Effect", "FadeIn")]
    [InlineData("FadeOut", "Effect", "FadeOut")]
    [InlineData("Breath", "Effect", "Breathing")]
    [InlineData("SevenColor", "Effect", "SevenColor")]
    [InlineData("何か不明な値", "Color", null)]
    public void 旧Command値は新CommandType_EffectTypeへマイグレートする(string oldCommand, string expectedCmd, string? expectedEffect)
    {
        var s = new SequenceStep { Command = oldCommand };
        Assert.Equal(expectedCmd, s.CommandType);
        Assert.Equal(expectedEffect, s.EffectType);
    }

    [Fact]
    public void Commandゲッタは常にnull_新形式優先で出力しない()
        => Assert.Null(new SequenceStep { Command = "Flash" }.Command);

    // ───────────────────────── FadeInHold / FadeOutHold 正規化 ─────────────────────────

    [Fact]
    public void FadeInHoldはFadeIn_Continuous偽へ正規化()
    {
        var s = new SequenceStep { EffectType = "FadeInHold" };
        Assert.Equal("FadeIn", s.EffectType);
        Assert.False(s.Continuous);
    }

    [Fact]
    public void FadeOutHoldはFadeOut_Continuous偽へ正規化()
    {
        var s = new SequenceStep { EffectType = "FadeOutHold" };
        Assert.Equal("FadeOut", s.EffectType);
        Assert.False(s.Continuous);
    }

    // ───────────────────────── マイグレーション: DurationMs / InterpolationIntervalMs ─────────────────────────

    [Fact]
    public void 旧DurationMsはEffectCycleDurationMsへ移送される()
    {
        var s = new SequenceStep { DurationMs = 2000 };
        Assert.Equal(2000, s.EffectCycleDurationMs);
    }

    [Fact]
    public void 旧DurationMsの0以下は無視()
    {
        var s = new SequenceStep { DurationMs = 0 };
        Assert.Null(s.EffectCycleDurationMs);
    }

    [Fact]
    public void 旧InterpolationIntervalMsはFadeStepsを算出する()
    {
        // EffectCycleDurationMs が先に入っている前提で FadeSteps = cycle / interval
        var s = new SequenceStep { EffectCycleDurationMs = 2000, InterpolationIntervalMs = 20 };
        Assert.Equal(100, s.FadeSteps);
    }
}
