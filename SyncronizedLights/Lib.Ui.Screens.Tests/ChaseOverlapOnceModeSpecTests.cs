using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// Chase2 / OL2（1回のみ・最終行保持）の仕様テスト＋回帰ガード。
///
/// 仕様：
///   ・Chase2 … 選択した連続行を上→下へ「1回だけ」順送りし、末尾で先頭へ戻らず最終行を点灯保持する。
///   ・OL2   … 同様に色クロスフェードで1回だけ送り、末尾で先頭へ戻らず最終行の色を保持する。
///   ・保持中もループトークンは生存（IsLoopRunning=true）のままで、停止／↑↓離脱／別行クリック等の
///     「何らかの操作」があるまで最終行を維持する。
///   ・反復版（Chase / OL）は従来どおり末尾→先頭へ戻って繰り返す。
///
/// テスト方針は既存 BpmMonotonicSpecTests に倣う：
///   (A) 巡回仕様 … 実行順序・フェード区間・末尾の戻り先を固定（本式は実行メソッドにインライン）。
///   (B) 回帰ガード … プロダクトソースに once（1回のみ）実装の要点が存在することを走査で保証。
/// 実際の点灯・保持の体感は実機確認項目。
/// </summary>
public class ChaseOverlapOnceModeSpecTests
{
    // ── 実行メソッドに埋め込まれた巡回ロジックの仕様複製 ──────────────────────

    /// <summary>Chase の1パスで実行される行インデックス列（反復/1回とも 0..N-1）。</summary>
    private static int[] ChaseExecutionOrder(int n) =>
        Enumerable.Range(0, n).ToArray();

    /// <summary>Chase の最終行の「次」＝反復は先頭(0)へ戻る／1回は最終行(N-1)を保持する。</summary>
    private static int ChaseNextAfterLast(int n, bool once) => once ? n - 1 : 0;

    /// <summary>OL の1パスのフェード区間 (from,to)。反復は末尾→先頭を含む／1回は含まない。</summary>
    private static (int from, int to)[] OverlapSegments(int n, bool once)
    {
        var segs = new List<(int, int)>();
        int count = once ? n - 1 : n;                 // 1回のみは末尾→先頭の区間を作らない
        for (int i = 0; i < count; i++)
            segs.Add((i, once ? i + 1 : (i + 1) % n));
        return segs.ToArray();
    }

    // ───────────────────────── (A) 巡回仕様 ─────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void Chase2は上から下へ1回_順序は0からN_1(int n)
        => Assert.Equal(Enumerable.Range(0, n).ToArray(), ChaseExecutionOrder(n));

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Chase2は最終行を保持し先頭へ戻らない(int n)
    {
        Assert.Equal(n - 1, ChaseNextAfterLast(n, once: true));   // 最終行を保持
        Assert.Equal(0, ChaseNextAfterLast(n, once: false));      // 反復版は先頭へ戻る
    }

    [Fact]
    public void OL2は末尾から先頭への区間を含まない()
    {
        // N=3：1回のみ＝(0→1),(1→2)／反復＝(0→1),(1→2),(2→0)
        Assert.Equal(new[] { (0, 1), (1, 2) }, OverlapSegments(3, once: true));
        Assert.Equal(new[] { (0, 1), (1, 2), (2, 0) }, OverlapSegments(3, once: false));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void OL2のフェード区間数はN_1_最後は最終行に着地する(int n)
    {
        var segs = OverlapSegments(n, once: true);
        Assert.Equal(n - 1, segs.Length);
        Assert.Equal(n - 1, segs[^1].to);            // 最後のフェードは最終行の色へ着地
    }

    // ───────────────────────── (B) 回帰ガード（ソース走査）─────────────────────────

    [Fact]
    public void 回帰ガード_実行メソッドがonce引数と最終行保持を実装している()
    {
        var text = ReadViewModelSource();

        // 実行メソッドが bool once 引数を受け付ける（反復と1回のみを共通化）。
        Assert.Matches(new Regex(@"StartChaseExecutionAsync\([^)]*bool\s+once"), text);
        Assert.Matches(new Regex(@"StartOverlapExecutionAsync\([^)]*bool\s+once"), text);

        // 最終行到達で待機せず抜けるガード（Chase2/OL2 共通の形）。
        Assert.Matches(new Regex(@"once\s*&&\s*i\s*==\s*steps\.Count\s*-\s*1"), text);

        // 「最終行保持」＝トークン生存のまま無期限待機（何らかの操作＝キャンセルで解除）。
        Assert.Matches(new Regex(@"Task\.Delay\(\s*Timeout\.Infinite\s*,\s*token\s*\)"), text);

        // OL2 は末尾で先頭へ戻さない（once のとき modulo 折返しを使わない）。
        Assert.Matches(new Regex(@"once\s*\?\s*steps\[i\s*\+\s*1\]\s*:\s*steps\[\(i\s*\+\s*1\)\s*%\s*steps\.Count\]"), text);
    }

    [Fact]
    public void 回帰ガード_Chase2とOL2のコマンドとマーカーが存在する()
    {
        var text = ReadViewModelSource();

        // 専用コマンド（RelayCommand）。
        Assert.Contains("StartChase2Async", text);
        Assert.Contains("StartOverlap2Async", text);

        // once 起動時に Trig 列へ "Chase2" / "OL2" を刻む。
        Assert.Matches(new Regex("once\\s*\\?\\s*\"Chase2\"\\s*:\\s*\"Chase\""), text);
        Assert.Matches(new Regex("once\\s*\\?\\s*\"OL2\"\\s*:\\s*\"OL\""), text);

        // 1回のみ判定ヘルパー（"Chase2"/"OL2"）。
        Assert.Matches(new Regex("IsOnceTrig[^\\n]*\"Chase2\"[^\\n]*\"OL2\""), text);
    }

    // テスト実行ディレクトリから上方向に探索してソースを読む（BpmMonotonicSpecTests と同じ方式）。
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
