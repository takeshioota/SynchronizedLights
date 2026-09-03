using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.0.1】Chase/OL の BPM→待機時間(ms) 変換が単調であることの仕様テスト＋回帰ガード。
///
/// 背景（不具合）：旧仕様は BPM の既定値 120 を「未設定」に化かす特別扱い
///   （<c>bpm &gt; 0 &amp;&amp; bpm != 120</c>）を Chase/OL/Preset の 3 箇所で行っており、
///   BPM=120 のとき Time(1000ms) 相当に化けていた。結果 50→1200ms / 80→750ms / 120→1000ms となり、
///   「80(750ms) が 120(本来500ms) より速い」非単調挙動が発生していた。
///   v2.0.1 で <c>!= 120</c> を撤去し、BPM は一律 <c>Math.Max(20, 60000 / bpm)</c> に統一。
///
/// テスト方針：
///   (A) 仕様値テスト … 期待マッピングと単調性を固定（本式は Chase/OL/Preset にインライン実装）。
///   (B) 回帰ガード   … プロダクトソースに <c>!= 120</c> の特別扱いが復活していないことを走査で保証。
/// 実際のテンポの体感は実機確認項目。
/// </summary>
public class BpmMonotonicSpecTests
{
    // v2.0.1 で 3 箇所（Chase/OL/Preset）に統一実装された式。
    private static int BpmToWaitMs(int bpm) => Math.Max(20, 60000 / bpm);

    // ───────────────────────── (A) 仕様値・単調性 ─────────────────────────

    [Theory]
    [InlineData(50, 1200)]
    [InlineData(80, 750)]
    [InlineData(120, 500)]   // 旧仕様では 1000ms 相当に化けていた
    [InlineData(240, 250)]
    [InlineData(6000, 20)]   // 下限 20ms
    public void BPMから待機msへの期待マッピング(int bpm, int expectedMs)
        => Assert.Equal(expectedMs, BpmToWaitMs(bpm));

    [Fact]
    public void BPM増加に対し待機msは単調非増加_120で反転しない()
    {
        int prev = int.MaxValue;
        for (int bpm = 1; bpm <= 3000; bpm++)
        {
            int ms = BpmToWaitMs(bpm);
            Assert.True(ms <= prev, $"BPM={bpm} で単調性が崩れた: {prev} -> {ms}");
            prev = ms;
        }
    }

    [Fact]
    public void BPM80は120より遅い_旧不具合の逆転が解消()
    {
        Assert.True(BpmToWaitMs(80) > BpmToWaitMs(120),
            "80 の待機(750ms) は 120 の待機(500ms) より長い＝80 の方が遅い、が正しい。");
    }

    // ───────────────────────── (B) 回帰ガード（ソース走査）─────────────────────────

    [Fact]
    public void 回帰ガード_ソースにBPM120の特別扱いが復活していない()
    {
        var files = new[]
        {
            FindRepoFile("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs"),
            FindRepoFile("Lib.Ui.Screens", "ViewModels", "TimeSequenceViewModel.cs"),
        };

        // "!= 120"（空白任意）が BPM 特別扱いとして復活していないこと。
        var pattern = new Regex(@"!=\s*120\b");
        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"ソースが見つかりません: {file}");
            var text = File.ReadAllText(file);
            Assert.False(pattern.IsMatch(text),
                $"{Path.GetFileName(file)} に BPM=120 の特別扱い（!= 120）が復活しています。");
        }
    }

    // テスト実行ディレクトリから上方向に探索してリポジトリ内ソースを見つける。
    private static string FindRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // 見つからない場合はテスト側で File.Exists により失敗させる（相対の目安を返す）
        return Path.Combine(relativeParts);
    }
}
