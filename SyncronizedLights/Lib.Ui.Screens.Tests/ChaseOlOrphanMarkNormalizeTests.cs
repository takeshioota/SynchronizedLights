using System.IO;
using System.Text.RegularExpressions;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// Q-20260922-03: Chase/OL の付与は「連続2〜15行」必須だが、解除（ClearLoopMark）は1行ずつ可能。
/// そのため 3 行 Chase の 2 行だけ解除して「1 行だけ Chase マークが残る」状態が作れてしまう。
/// 再生時はループ起動せず単一 Color として振る舞う（実害なし）が、マーク表示だけが残り違和感になる。
///
/// 対策：孤児（連続ブロックが 2 行未満）のループマーカーを検出する純ロジック
/// SequenceEditorViewModel.FindOrphanLoopMarkIndices を追加し、ClearLoopMark 末尾の
/// NormalizeLoopMarks から呼んで自動で掃除する（UI 表示のみ・API 非依存）。
///
/// 純ロジック部は実プロダクトコードを直接検証し、UI 結線はソース走査でガードする。
/// </summary>
public class ChaseOlOrphanMarkNormalizeTests
{
    // ───────────────────────── 純ロジック（実データ検証） ─────────────────────────

    [Fact]
    public void 正常な連続ブロックは孤児と見なさない()
    {
        // 2 行以上の同種連続ブロックは維持（解除対象なし）。
        Assert.Empty(Find("Chase", "Chase", "Chase"));
        Assert.Empty(Find("OL", "OL"));
        // 3 行 Chase を 2 行へ減らした後（有効なまま）＝孤児なし。
        Assert.Empty(Find("Chase", "Chase", ""));
    }

    [Fact]
    public void 一行だけ残ったマークは孤児として検出する()
    {
        // 3 行 Chase の後ろ 2 行を解除 → 先頭 1 行だけ残る。
        Assert.Equal(new[] { 0 }, Find("Chase", "", ""));
        // 先頭 2 行を解除 → 末尾 1 行だけ残る。
        Assert.Equal(new[] { 2 }, Find("", "", "Chase"));
    }

    [Fact]
    public void 複数の孤児をすべて検出する()
    {
        // 削除で分断され両端が 1 行ずつ残った（間が空）ケース。
        Assert.Equal(new[] { 0, 2 }, Find("Chase", "", "Chase"));
        // 別モードが隣接＝それぞれ別ブロック（各 1 行）で両方孤児。
        Assert.Equal(new[] { 0, 1 }, Find("Chase", "OL"));
        // Chase と Chase2 は別 Trig 文字列＝別ブロック（各 1 行）で両方孤児。
        Assert.Equal(new[] { 0, 1 }, Find("Chase", "Chase2"));
    }

    [Fact]
    public void 有効ブロックと孤児が混在しても有効ブロックは残す()
    {
        // [0,1]=有効な Chase ブロック / [3]=単独 OL（孤児）。
        Assert.Equal(new[] { 3 }, Find("Chase", "Chase", "", "OL"));
        // 5 行が中央で 2+2 に分断＝両方有効（孤児なし）。
        Assert.Empty(Find("Chase", "Chase", "", "Chase", "Chase"));
    }

    [Fact]
    public void ループマーカー以外は対象外()
    {
        Assert.Empty(Find("", "", ""));
        Assert.Empty(Find("Color", "Off", "Flash"));
        Assert.Empty(Find((string?)null, null));
    }

    [Fact]
    public void 上限15行の連続ブロックは孤児にならない()
    {
        var trigs = Enumerable.Repeat("Chase", 15).ToArray();
        Assert.Empty(SequenceEditorViewModel.FindOrphanLoopMarkIndices(trigs));
    }

    [Fact]
    public void null入力は空を返す()
    {
        Assert.Empty(SequenceEditorViewModel.FindOrphanLoopMarkIndices(null!));
    }

    // ───────────────────────── UI 結線ガード（ソース走査） ─────────────────────────

    [Fact]
    public void ClearLoopMarkは解除後にNormalizeLoopMarksを呼ぶ()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        var clearBody = Between(vm, "private void ClearLoopMark", "private int NormalizeLoopMarks");
        // 選択行の解除（Trig="")の「後」に正規化を呼ぶ。
        Assert.Contains("w.Trig = \"\"", clearBody);
        Assert.Contains("NormalizeLoopMarks()", clearBody);
        Assert.True(
            clearBody.IndexOf("w.Trig = \"\"", System.StringComparison.Ordinal)
                < clearBody.IndexOf("NormalizeLoopMarks()", System.StringComparison.Ordinal),
            "正規化は選択行の解除より後で呼ばれなければならない");

        // NormalizeLoopMarks は純ロジック FindOrphanLoopMarkIndices を用いて孤児を解除する。
        var normBody = Between(vm, "private int NormalizeLoopMarks", "\n        }");
        Assert.Contains("FindOrphanLoopMarkIndices", normBody);
        Assert.Contains(".Trig = \"\"", normBody);
    }

    [Fact]
    public void 付与は連続2から15行必須の不変条件を保つ()
    {
        // 本対策は「付与は 2〜15 行」という前提に依存する（1 行では付与できない）。
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");
        Assert.Matches(
            new Regex(@"selectedSteps\.Count\s*<\s*2\s*\|\|\s*selectedSteps\.Count\s*>\s*15"),
            vm);
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────
    private static int[] Find(params string?[] trigs)
        => SequenceEditorViewModel.FindOrphanLoopMarkIndices(trigs).ToArray();

    private static string Between(string text, string startMarker, string endMarker)
    {
        int s = text.IndexOf(startMarker, System.StringComparison.Ordinal);
        Assert.True(s >= 0, $"開始マーカーが見つかりません: {startMarker}");
        int e = text.IndexOf(endMarker, s + startMarker.Length, System.StringComparison.Ordinal);
        Assert.True(e >= 0, $"終了マーカーが見つかりません: {endMarker}");
        return text.Substring(s, e - s);
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
