using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// Q-20260920-04: Effect 実行 → 停止 → Emergency Black ON → OFF で、解除時に Effect が再生されてしまう。
/// 停止状態で Emergency に入ったなら、解除後は「停止」に戻る（進入時に表示していた色＝停止時の色を
/// 再表示する）べき、というのが正しい期待（ユーザー選択＝A案「停止時の色を再表示」）。
///
/// 原因：ToggleEmergencyAsync の解除側は、退避していたのが _wasLoopRunningBeforeEmergency のみで、
///       単発 Effect/Color の「再生中／停止済み」を区別せず、非ループ経路では選択行を無条件で
///       ExecuteStepWithoutAdvanceAsync 再実行していた（＝停止済みでも再生し直していた）。
/// 対策：進入時に _wasActiveBeforeEmergency（IsPlaying/IsLoopRunning/IsEffectRunning）と、
///       黒送信前の表示色 _colorBeforeEmergency（ILightingFacade.LastSentColor のキャッシュ即読み）を
///       退避。解除時、停止済み（=非アクティブ）なら再実行せず、退避色を SetColorAsync で再表示する。
///
/// テスト方針は既存 SpecTests に倣い、プロダクトソースへ対策の要点が存在することを走査で保証する
/// （実際の Emergency ON/OFF は実機確認項目）。
/// </summary>
public class EmergencyBlackReleaseSpecTests
{
    [Fact]
    public void ファサードは直近送出色をキャッシュ即読みで公開する()
    {
        var iface = ReadSource("Lib.Application", "Interfaces", "ILightingFacade.cs");
        Assert.Matches(new Regex(@"Rgb\?\s+LastSentColor\s*\{\s*get;\s*\}"), iface);

        // Api 実装は保持中の _lastSentColor をそのまま返す（HTTP 往復なし）。
        var api = ReadSource("Lib.Application", "Facades", "ApiLightingFacade.cs");
        Assert.Matches(new Regex(@"LastSentColor\s*=>\s*_lastSentColor\s*;"), api);

        // Dummy 実装（オフライン）は null。
        var dummy = ReadSource("Lib.Application", "Facades", "DummyLightingFacade.cs");
        Assert.Matches(new Regex(@"LastSentColor\s*=>\s*null\s*;"), dummy);
    }

    [Fact]
    public void 進入時にアクティブ状態と表示色を黒送信前に退避する()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // 退避フィールドが存在する。
        Assert.Contains("private bool _wasActiveBeforeEmergency;", vm);
        Assert.Contains("private Rgb? _colorBeforeEmergency;", vm);

        // 進入側で、ループ以外のアクティブ（再生中／エフェクト実行中）と表示色を退避する。
        Assert.Contains("_wasActiveBeforeEmergency = IsPlaying || IsLoopRunning || (_lighting?.IsEffectRunning ?? false);", vm);
        Assert.Contains("_colorBeforeEmergency = _lighting?.LastSentColor;", vm);

        // 退避は「黒(0,0,0)を送る SetColorAsync」より前で行う（黒で色が上書きされる前に読む）。
        // 黒送信のマーカーは他メソッドにも存在するため、退避位置「以降」で最初に現れる送信を進入側の黒送信とみなす。
        int capture = vm.IndexOf("_colorBeforeEmergency = _lighting?.LastSentColor;", System.StringComparison.Ordinal);
        Assert.True(capture >= 0, "色退避コードが見つかりません");
        int blackSend = vm.IndexOf("await _lighting.SetColorAsync(Target.All, color);", capture, System.StringComparison.Ordinal);
        Assert.True(blackSend > capture, "進入時の色退避は黒送信より前でなければならない");
    }

    [Fact]
    public void 解除時_停止済みなら再実行せず停止時の色を再表示する()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // 解除側で退避値を消費する。
        Assert.Contains("var wasActive = _wasActiveBeforeEmergency;", vm);
        Assert.Contains("var colorBefore = _colorBeforeEmergency;", vm);

        // 進入前がアクティブだった場合は従来どおり選択行を再実行（再生復帰）。
        var activeBranch = Between(vm, "else if (wasActive)", "else");
        Assert.Contains("ExecuteStepWithoutAdvanceAsync()", activeBranch);

        // 停止済み（else）分岐では、選択行の再実行ではなく退避色の再表示（SetColorAsync）を行う。
        var stoppedBranch = Between(vm, "進入前は「停止／静止」状態だった", "return;");
        Assert.Contains("_lighting.SetColorAsync(Target.All, colorBefore)", stoppedBranch);
        Assert.DoesNotContain("ExecuteStepWithoutAdvanceAsync()", stoppedBranch);
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────
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
