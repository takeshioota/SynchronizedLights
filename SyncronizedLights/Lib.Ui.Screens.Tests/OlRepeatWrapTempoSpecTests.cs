using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.20.0 / 項目2】OL 繰り返し時に「色変化が急に進む」事象の対策。
///
/// 事象：OL(繰り返し)のループ境界で色変化のテンポが飛ぶ（OL2 では発生しない）。
/// 原因：OL のフェード時間は「遷移先(next)の行の Time/BPM」で決まる。OL 繰り返しだけが
///       巻戻し区間(最終行→先頭行)を持ち、そこで先頭行の Time/BPM を使う。先頭行は OL2 では
///       遷移先にならず使われないため未設定(0)のまま放置されがちで、既定値(DefaultLoopStepMs=1000)に
///       化けて境界だけテンポが変わる＝「急」に見える。OL2 は巻戻しが無いので出ない。
/// 対応：巻戻し区間で先頭行が未設定(BPM<=0 かつ Time<=0)なら、直前区間のフェード時間を引き継いで
///       境目のテンポを維持する。先頭行に明示値があればそれを尊重。OL2 は影響なし。
///
/// テスト方針は既存 ChaseOverlapOnceModeSpecTests に倣う：
///   (A) 算出仕様 … フェード時間の決め方（巻戻し引き継ぎ込み）をテスト側に複製して固定する。
///   (B) 回帰ガード … プロダクトソースに巻戻し引き継ぎの要点が存在することを走査で保証する。
/// </summary>
public class OlRepeatWrapTempoSpecTests
{
    private const int DefaultLoopStepMs = 1000;   // SequenceEditorViewModel と一致

    /// <summary>
    /// 1 セグメントのフェード時間。next=(bpm,timeMs)。巻戻し区間(isWrap)かつ next 未設定なら prevFade を引き継ぐ。
    /// SequenceEditorViewModel.StartOverlapExecutionAsync の算出と同じ規則。
    /// </summary>
    private static int SegmentFadeMs(int nextBpm, int nextTimeMs, int prevFadeMs, bool isWrap)
    {
        int f;
        if (nextBpm > 0) f = System.Math.Max(20, 60000 / nextBpm);
        else f = nextTimeMs > 0 ? nextTimeMs : DefaultLoopStepMs;

        if (isWrap && nextBpm <= 0 && nextTimeMs <= 0 && prevFadeMs > 0) f = prevFadeMs;
        return f;
    }

    /// <summary>OL 繰り返し1周分の各セグメントのフェード時間を算出する。rows=(bpm,timeMs)。</summary>
    private static int[] OverlapRepeatFades((int bpm, int time)[] rows)
    {
        int n = rows.Length;
        var fades = new int[n];
        int prev = 0;
        for (int i = 0; i < n; i++)
        {
            var next = rows[(i + 1) % n];
            bool isWrap = i == n - 1;             // next == rows[0]
            int f = SegmentFadeMs(next.bpm, next.time, prev, isWrap);
            fades[i] = f;
            prev = f;
        }
        return fades;
    }

    /// <summary>OL2(1回のみ)の各セグメント。末尾→先頭の巻戻しを含まない（先頭行は遷移先にならない）。</summary>
    private static int[] Overlap2Fades((int bpm, int time)[] rows)
    {
        int n = rows.Length;
        var fades = new int[n - 1];
        int prev = 0;
        for (int i = 0; i < n - 1; i++)
        {
            var next = rows[i + 1];
            int f = SegmentFadeMs(next.bpm, next.time, prev, isWrap: false);
            fades[i] = f;
            prev = f;
        }
        return fades;
    }

    // ───────────────────────── (A) 算出仕様 ─────────────────────────

    [Fact]
    public void 先頭行未設定_OL繰り返しの巻戻しは直前テンポを引き継ぐ()
    {
        // [A(未設定), B(500ms), C(500ms)]
        var rows = new[] { (bpm: 0, time: 0), (bpm: 0, time: 500), (bpm: 0, time: 500) };
        var fades = OverlapRepeatFades(rows);

        Assert.Equal(new[] { 500, 500, 500 }, fades);   // 巻戻し(2)も 500 を引き継ぎ（既定1000に飛ばない）
    }

    [Fact]
    public void 先頭行未設定でも対策無しなら巻戻しが既定値で急変する_回帰の証拠()
    {
        // 対策を外した（isWrap を常に false 扱い）場合、巻戻しは先頭行=未設定→既定1000ms になる。
        var rows = new[] { (bpm: 0, time: 0), (bpm: 0, time: 500), (bpm: 0, time: 500) };
        int prev = 0; var bad = new int[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var next = rows[(i + 1) % rows.Length];
            bad[i] = SegmentFadeMs(next.bpm, next.time, prev, isWrap: false);
            prev = bad[i];
        }
        Assert.Equal(new[] { 500, 500, 1000 }, bad);    // 巻戻しだけ 1000＝テンポが飛ぶ（これが「急」）
    }

    [Fact]
    public void OL2は巻戻しが無く先頭行の未設定時間を使わない()
    {
        var rows = new[] { (bpm: 0, time: 0), (bpm: 0, time: 500), (bpm: 0, time: 500) };
        var fades = Overlap2Fades(rows);
        Assert.Equal(new[] { 500, 500 }, fades);        // 先頭行(未設定)は遷移先にならない＝1000 は現れない
    }

    [Fact]
    public void 先頭行に明示値があれば巻戻しはその値を尊重する()
    {
        // [A(800ms), B(500ms), C(500ms)] 先頭に明示値→引き継ぎせずそのまま使う
        var rows = new[] { (bpm: 0, time: 800), (bpm: 0, time: 500), (bpm: 0, time: 500) };
        var fades = OverlapRepeatFades(rows);
        Assert.Equal(new[] { 500, 500, 800 }, fades);   // 巻戻しは先頭行の 800 を使用
    }

    [Fact]
    public void 全行均一なら従来どおり全区間同じ_影響なし()
    {
        // BPM116 均一（Dragon 第2 OL 相当）→ 517ms 均一、巻戻し引き継ぎは発動しない
        var rows = new[] { (bpm: 116, time: 0), (bpm: 116, time: 0), (bpm: 116, time: 0), (bpm: 116, time: 0), (bpm: 116, time: 0) };
        var fades = OverlapRepeatFades(rows);
        foreach (var f in fades) Assert.Equal(60000 / 116, f);
    }

    // ───────────────────────── (B) 回帰ガード（ソース走査）─────────────────────────

    [Fact]
    public void 回帰ガード_OLが巻戻し区間で直前フェード時間を引き継ぐ()
    {
        var text = ReadViewModelSource();

        // 巻戻し区間判定（最終行＝next が先頭行）。
        Assert.Matches(new Regex(@"bool\s+isWrapSegment\s*=\s*!once\s*&&\s*i\s*==\s*steps\.Count\s*-\s*1"), text);
        // 先頭行が未設定なら直前区間のフェード時間を引き継ぐ。
        Assert.Matches(new Regex(@"isWrapSegment\s*&&\s*next\.Bpm\s*<=\s*0\s*&&\s*next\.TimeMs\s*<=\s*0\s*&&\s*prevFadeMs\s*>\s*0"), text);
        Assert.Contains("prevFadeMs = fadeMs", text);
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
