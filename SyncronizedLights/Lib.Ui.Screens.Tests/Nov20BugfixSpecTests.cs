using System.Globalization;
using System.IO;
using Lib.Domain.Enums;
using Lib.Ui.Screens.Validation;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 2026-11-20 指摘バッチ(B-20261120-01〜05 / Q-20261120-01)の回帰ガード。
/// 純ロジック(ToModel / RainbowInputValidator / ByteRangeValidationRule)は機能検証、
/// UI 配線(Esc / XAML バインド)はプロダクトソースへ対策の要点が存在することを走査で保証する
/// （実際の見た目・端末挙動は実機確認項目）。
/// </summary>
public class Nov20BugfixSpecTests
{
    // ───────────────────────── No.44(B-20261120-04) ─────────────────────────
    // 点滅周期・点灯比率はモードに関係なく常時保存される（非 Blink で入力しても再読込で消えない）。

    [Theory]
    [InlineData(RainbowMode.Solid)]
    [InlineData(RainbowMode.FadeInOut)]
    [InlineData(RainbowMode.FadeOut)]
    public void No44_点滅周期と比率は非Blinkモードでも保存される(RainbowMode mode)
    {
        var w = new SequenceStepWrapper
        {
            CommandType = "Rainbow",
            RainbowMode = mode,
            RainbowBlinkPeriodMs = 5000,
            RainbowDutyRatio = 3,
        };

        var m = w.ToModel();

        Assert.Equal((int?)5000, m.RainbowBlinkPeriodMs);
        Assert.Equal((byte?)3, m.RainbowDutyRatio);
    }

    [Fact]
    public void No44_非Rainbow行では点滅周期と比率は保存しない()
    {
        var w = new SequenceStepWrapper
        {
            CommandType = "Color",
            RainbowBlinkPeriodMs = 5000,
            RainbowDutyRatio = 3,
        };

        var m = w.ToModel();

        Assert.Null(m.RainbowBlinkPeriodMs);
        Assert.Null(m.RainbowDutyRatio);
    }

    [Fact]
    public void No44_点滅で設定し別モードで保存して再読込しても値が保持される()
    {
        // ユーザ想定シナリオ: 点滅(Blink)で点灯周期・比率を設定 → 別モードに切替えて保存 →
        // 再読込 → 点滅に戻す。元の値が復元される（初期値 500/5 に戻ったら不具合）。
        var w = new SequenceStepWrapper
        {
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.Blink,
            RainbowBlinkPeriodMs = 3200,
            RainbowDutyRatio = 7,
        };

        // 別モード(Solid)へ切替えても in-memory の点滅周期・比率はクリアされない。
        w.RainbowMode = RainbowMode.Solid;
        Assert.Equal(3200, w.RainbowBlinkPeriodMs);
        Assert.Equal((byte)7, w.RainbowDutyRatio);

        // 保存(ToModel) → 再読込(モデルからの復元) の往復。
        var reloaded = new SequenceStepWrapper(w.ToModel());

        // 初期値(500/5)でなく設定値が復元される。保存時モード(Solid)も復元される。
        Assert.Equal(3200, reloaded.RainbowBlinkPeriodMs);
        Assert.Equal((byte)7, reloaded.RainbowDutyRatio);
        Assert.Equal(RainbowMode.Solid, reloaded.RainbowMode);
    }

    // ───────────────────────── No.41(B-20261120-01) ─────────────────────────
    // 点滅周期・FO 等の範囲外(0〜5000ms 外)は RainbowInputValidator が拒否する。

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    [InlineData(999999)]
    public void No41_範囲外の時間は拒否される(int t)
    {
        Assert.False(RainbowInputValidator.Validate(2, t, t, t, t, 5, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public void No41_境界内の時間は許容される(int t)
    {
        Assert.True(RainbowInputValidator.Validate(3, t, t, t, t, 5, out _));
    }

    [Theory]
    [InlineData(1)]  // 色数不足
    [InlineData(8)]  // 色数過多
    public void No41_色数が範囲外は拒否される(int n)
    {
        Assert.False(RainbowInputValidator.Validate(n, 1000, 500, 1000, 1000, 5, out _));
    }

    [Fact]
    public void No41_適用と送信の両経路がValidatorを呼ぶ()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");
        // 「この行に適用」(ApplyRainbowToStep) と「Rainbow Start」(StartRainbow) の両方で検証する。
        int calls = System.Text.RegularExpressions.Regex.Matches(vm, @"RainbowInputValidator\.Validate\(").Count;
        Assert.True(calls >= 2, $"RainbowInputValidator.Validate の呼び出しが2経路に満たない（{calls}）。");
    }

    // ───────────────────────── No.45(B-20261120-05) ─────────────────────────
    // RGB 範囲外(256/441 等)は ByteRangeValidationRule が拒否する（末尾桁ドロップで保存されない）。

    [Theory]
    [InlineData("256")]
    [InlineData("441")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    public void No45_範囲外や非整数のRGBは拒否される(string input)
    {
        var rule = new ByteRangeValidationRule();
        var r = rule.Validate(input, CultureInfo.InvariantCulture);
        Assert.False(r.IsValid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("200")]
    [InlineData("255")]
    public void No45_0から255は許容される(string input)
    {
        var rule = new ByteRangeValidationRule();
        Assert.True(rule.Validate(input, CultureInfo.InvariantCulture).IsValid);
    }

    [Theory]
    [InlineData("Lib.Ui.Screens", "Views", "SequenceEditorWindow.xaml")]
    [InlineData("Lib.Ui.Screens", "Views", "TimeSequenceView.xaml")]
    public void No45_グリッドRGBは検証付きでPropertyChangedを使わない(string a, string b, string c)
    {
        var xaml = ReadSource(a, b, c);
        Assert.Contains("ByteRangeValidationRule", xaml);
        // 逐次パースによる桁ドロップの元凶だった PropertyChanged バインドが残っていないこと。
        Assert.DoesNotContain("ColorR, UpdateSourceTrigger=PropertyChanged", xaml);
        Assert.DoesNotContain("ColorG, UpdateSourceTrigger=PropertyChanged", xaml);
        Assert.DoesNotContain("ColorB, UpdateSourceTrigger=PropertyChanged", xaml);
    }

    [Fact]
    public void No45_統合画面のグリッドRGBも検証付きに是正されている()
    {
        var xaml = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml");
        Assert.Contains("ByteRangeValidationRule", xaml);
        Assert.DoesNotContain("ColorR, UpdateSourceTrigger=PropertyChanged", xaml);
        Assert.DoesNotContain("ColorG, UpdateSourceTrigger=PropertyChanged", xaml);
        Assert.DoesNotContain("ColorB, UpdateSourceTrigger=PropertyChanged", xaml);
    }

    // ─── No.45 追加(2026-11-24): カラーピッカーダイアログ(DlgColorPicker)の RGB 直接入力 ───
    // グリッドだけでなく DlgColorPicker の R/G/B テキストも範囲外をクランプして取り込む。
    // 旧実装は byte.TryParse で "256" が失敗 → 直前の有効値(25)が残り「256→25」の桁落ちになっていた。

    [Theory]
    [InlineData("256", 255)]  // 上限超過はクランプ（←従来は桁落ちで 25 保存だった）
    [InlineData("441", 255)]
    [InlineData("-1", 0)]      // 下限未満はクランプ
    [InlineData("0", 0)]
    [InlineData("255", 255)]
    [InlineData("128", 128)]
    public void No45_カラーピッカーのRGBは範囲外をクランプして取り込む(string input, int expected)
    {
        Assert.True(Lib.Ui.Screens.Views.DlgColorPicker.TryParseClampedByte(input, out byte v));
        Assert.Equal(expected, (int)v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void No45_カラーピッカーの空や非整数は取り込まない(string input)
    {
        // 入力途中とみなして取り込まない（再入力を妨げない）。
        Assert.False(Lib.Ui.Screens.Views.DlgColorPicker.TryParseClampedByte(input, out _));
    }

    // No.45 追加(2026-11-24 B案): R/G/B 欄は数字のみ入力可（マイナス・小数点・文字はそもそも打てない）。

    [Theory]
    [InlineData("0", true)]
    [InlineData("9", true)]
    [InlineData("255", true)]
    [InlineData("-", false)]   // マイナス記号は打てない
    [InlineData(".", false)]
    [InlineData("a", false)]
    [InlineData(" ", false)]
    [InlineData("1a", false)]
    public void No45_カラーピッカーのRGB欄は数字のみ入力可(string input, bool allowed)
    {
        Assert.Equal(allowed, Lib.Ui.Screens.Views.DlgColorPicker.IsAsciiDigits(input));
    }

    [Theory]
    [InlineData("-5", "5")]      // マイナス付き貼り付けは数字のみ残す
    [InlineData("12a3", "123")]
    [InlineData("25.5", "255")]
    [InlineData("abc", "")]      // 数字が無ければ空（貼り付け取消相当）
    public void No45_カラーピッカーの貼り付けは数字のみに正規化する(string pasted, string expected)
    {
        Assert.Equal(expected, Lib.Ui.Screens.Views.DlgColorPicker.KeepAsciiDigits(pasted));
    }

    // ───────────────────────── No.46(Q-20261120-01) ─────────────────────────
    // Esc 長押し(オートリピート)は最初の1回だけ受け付ける(e.IsRepeat ガード)。

    [Fact]
    public void No46_IntegratedWindowのEscはオートリピートを時間デバウンスで抑止する()
    {
        var win = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml.cs");
        Assert.Contains("EscRepeatSuppressMs", win);
        Assert.Contains("_lastEscTick", win);
        // 2026-11-24: VM/リモートで初回から IsRepeat=true になり Esc が全く効かない不具合のため、
        // 「IsRepeat だけで早期 return」する実装に戻っていないこと。
        Assert.DoesNotContain("e.Key == Key.Escape && e.IsRepeat", win);
    }

    [Fact]
    public void No46_SequenceEditorWindowのEscはオートリピートを時間デバウンスで抑止する()
    {
        var win = ReadSource("Lib.Ui.Screens", "Views", "SequenceEditorWindow.xaml.cs");
        Assert.Contains("EscRepeatSuppressMs", win);
        Assert.Contains("_lastEscTick", win);
        Assert.DoesNotContain("e.Key == Key.Escape && e.IsRepeat", win);
    }

    // ─────────────────────── No.42/43(B-20261120-02/-03) ───────────────────────
    // Esc 再開時、Rainbow の停止では先頭色(赤)の単色ホールドを送らない。

    [Fact]
    public void No4243_VMにEsc再開用の赤ホールド抑止ロジックがある()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");
        Assert.Contains("public void PrepareForEscResume()", vm);
        Assert.Contains("_escRainbowResumePending", vm);
        Assert.Contains("suppressRainbowHold", vm);
        // 抑止対象は Rainbow 行のみ（通常停止の色保持は不変）。
        Assert.Contains("SelectedStep.CommandType == \"Rainbow\"", vm);
    }

    [Fact]
    public void No4243_両画面のEscハンドラがPrepareForEscResumeを呼ぶ()
    {
        var seq = ReadSource("Lib.Ui.Screens", "Views", "SequenceEditorWindow.xaml.cs");
        Assert.Contains("PrepareForEscResume()", seq);
        var integ = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml.cs");
        Assert.Contains("PrepareForEscResume()", integ);
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────
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
