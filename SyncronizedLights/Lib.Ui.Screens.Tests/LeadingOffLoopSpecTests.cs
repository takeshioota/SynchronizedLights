using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.12.0 追加開発 / BUG-20261109-01】Chase/OL ブロックが「off 行から始まる」ときの挙動。
///
/// 症状：先頭が off(消灯/SignalOff) の Chase/OL ブロックが、起動直後に即停止して走り出さない
///       （他の Effect 始まりのブロックは正常に動く）。
/// 原因：BUG-20260729-06/09 で入れた「ブロック内に off があればループを止める終端処理」が、
///       ブロックの先頭(i=0)にも無条件で効いていたため、先頭 off で即 StopLoopExecution していた。
/// 対応：先頭の連続 off（leadingOff）は「終端」ではなく黒フレーム扱いにして止めず、
///       黒→効果→…→黒 と脈動ループさせる。内容（非 off 行）より後ろに来る off は従来どおり終端。
///       全行 off（退化）は無限空ループを避けるため従来どおり先頭 off で停止。
///
/// テスト方針は既存 ChaseOverlapOnceModeSpecTests に倣う：
///   (A) 判定仕様 … leadingOff の算出規則をテスト側に複製して固定する。
///   (B) 回帰ガード … プロダクトソースに leadingOff ゲートの要点が存在することを走査で保証する。
/// 実際の点灯・脈動の体感は実機確認項目。
/// </summary>
public class LeadingOffLoopSpecTests
{
    // ── (A) 判定仕様：LeadingOffCount と同じ規則の複製 ──────────────────────────
    //   true=off 行。先頭の連続 off 数を返す。全 off は 0（＝従来どおり先頭 off で停止）。
    private static int LeadingOffCount(bool[] isOff)
    {
        int n = 0;
        while (n < isOff.Length && isOff[n]) n++;
        return n >= isOff.Length ? 0 : n;
    }

    /// <summary>index i の off 行が「終端(停止)」か＝i が leadingOff 以上のとき true。</summary>
    private static bool TerminatesAt(bool[] isOff, int i) => isOff[i] && i >= LeadingOffCount(isOff);

    [Fact]
    public void 先頭Offは黒フレーム_終端にならない()
    {
        // [Off, Eff, Eff]（Dragon Night2 先頭ブロック相当）
        var block = new[] { true, false, false };
        Assert.Equal(1, LeadingOffCount(block));
        Assert.False(TerminatesAt(block, 0));   // 先頭 off は止めない（黒フレーム）
    }

    [Fact]
    public void 連続する先頭Offは全て黒フレーム()
    {
        // [Off, Off, Eff, Eff]
        var block = new[] { true, true, false, false };
        Assert.Equal(2, LeadingOffCount(block));
        Assert.False(TerminatesAt(block, 0));
        Assert.False(TerminatesAt(block, 1));
    }

    [Fact]
    public void 内容の後ろのOffは従来どおり終端する()
    {
        // [Eff, Eff, Off]（off を終端マーカーに使う既存ケースを維持）
        var block = new[] { false, false, true };
        Assert.Equal(0, LeadingOffCount(block));
        Assert.True(TerminatesAt(block, 2));    // 末尾 off は停止
    }

    [Fact]
    public void 中間のOffも終端する()
    {
        // [Eff, Off, Eff]
        var block = new[] { false, true, false };
        Assert.True(TerminatesAt(block, 1));
    }

    [Fact]
    public void 先頭Offと中間Offが混在_先頭は黒_後ろは終端()
    {
        // [Off, Eff, Off, Eff]
        var block = new[] { true, false, true, false };
        Assert.Equal(1, LeadingOffCount(block));
        Assert.False(TerminatesAt(block, 0));   // 先頭 off は黒フレーム
        Assert.True(TerminatesAt(block, 2));    // 内容の後ろの off は終端
    }

    [Fact]
    public void 全行Offは退化ケース_従来どおり先頭で停止()
    {
        var block = new[] { true, true };
        Assert.Equal(0, LeadingOffCount(block));   // 無限空ループを避け先頭で停止
        Assert.True(TerminatesAt(block, 0));
    }

    [Fact]
    public void Off無しブロックは影響を受けない()
    {
        var block = new[] { false, false, false };
        Assert.Equal(0, LeadingOffCount(block));
        for (int i = 0; i < block.Length; i++) Assert.False(TerminatesAt(block, i));
    }

    // ── (B) 回帰ガード（ソース走査）──────────────────────────────────────────
    [Fact]
    public void 回帰ガード_LeadingOffCountヘルパーが存在する()
    {
        var text = ReadViewModelSource();
        Assert.Matches(new Regex(@"private\s+static\s+int\s+LeadingOffCount\s*\(\s*IList<SequenceStepWrapper>\s+steps\s*\)"), text);
    }

    [Fact]
    public void 回帰ガード_ChaseとOLが先頭Offを終端から除外する()
    {
        var text = ReadViewModelSource();

        // 両ループが leadingOff を算出している（Chase/OL の steps 構築直後）。
        int leadingOffDecls = Regex.Matches(text, @"int\s+leadingOff\s*=\s*LeadingOffCount\(steps\)").Count;
        Assert.True(leadingOffDecls >= 2, $"leadingOff 算出が Chase/OL の2箇所に無い（検出 {leadingOffDecls}）。");

        // Chase 側：off 終端を leadingOff でゲート。
        Assert.Matches(new Regex(@"IsOffStep\(step\)\s*&&\s*i\s*>=\s*leadingOff"), text);
        // OL 側：off 終端を leadingOff でゲート。
        Assert.Matches(new Regex(@"IsOffStep\(current\)\s*&&\s*i\s*>=\s*leadingOff"), text);
    }

    // テスト実行ディレクトリから上方向に探索してソースを読む（ChaseOverlapOnceModeSpecTests と同じ方式）。
    private static string ReadViewModelSource()
    {
        var file = FindRepoFile("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");
        Assert.True(File.Exists(file), $"ソースが見つかりません: {file}");
        return File.ReadAllText(file);
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(relativeParts);
    }
}
