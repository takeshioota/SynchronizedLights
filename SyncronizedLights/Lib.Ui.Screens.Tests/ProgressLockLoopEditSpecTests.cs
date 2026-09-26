using System.IO;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// BUG-20260926-02: OL/Chase 実行中に「進行ロック」を掛けた状態で他行をクリックすると、演出（ループ）が
/// 止まってしまう。進行ロックは「ON 中は選択行の実行が継続し、別行選択/編集しても点灯は変わらない」
/// （本番中に先のキューを安全に修正するための機能）が仕様のため、ロック中のクリックでループが止まるのは不具合。
///
/// 原因：
///  (1) 両画面の行クリックハンドラ StepDataGrid_PreviewMouseLeftButtonDown が、ループ実行中は無条件で
///      RequestLoopExit()（ループ停止）を呼んでいた（進行ロックを考慮していない）。
///  (2) Chase/OL ループは各ステップで SelectedStep を自分のステップへ書き換えており、進行ロック中でも
///      ユーザーのクリック選択を奪い返すため、他行の編集が成立しなかった。
///
/// 対策：
///  (1) IsProgressLocked のときは RequestLoopExit() を呼ばない（選択のみ確定＝編集可能／ループ継続）。
///  (2) IsProgressLocked のときはループが SelectedStep/CurrentStepIndex を書き換えない。実行対象は
///      ExecuteStepWithoutAdvanceAsync(step) の明示指定で継続する（OL は元々ローカル変数で送出）。
///
/// テスト方針は既存 SpecTests に倣い、プロダクトソースへ対策の要点が存在することを走査で保証する
/// （実際のロック中クリック編集は実機/手動確認項目）。
/// </summary>
public class ProgressLockLoopEditSpecTests
{
    [Fact]
    public void 両画面のクリックハンドラは進行ロック中にループを離脱しない()
    {
        // 統合画面
        var integrated = ReadRepoFile("SynchronizedLights.UI", "IntegratedWindow.xaml.cs");
        AssertGuardedRequestLoopExit(integrated, "_sequenceVm");

        // シーケンス編集画面
        var editor = ReadRepoFile("Lib.Ui.Screens", "Views", "SequenceEditorWindow.xaml.cs");
        AssertGuardedRequestLoopExit(editor, "vm");
    }

    [Fact]
    public void 実行本体は実行対象を明示指定できる()
    {
        var vm = ReadRepoFile("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // ExecuteStepWithoutAdvanceAsync に overrideStep 引数がある。
        Assert.Contains("ExecuteStepWithoutAdvanceAsync(SequenceStepWrapper? overrideStep = null)", vm);
        // 実行対象は overrideStep 優先。
        Assert.Contains("var wrapper = overrideStep ?? SelectedStep;", vm);
    }

    [Fact]
    public void ループは進行ロック中に選択ハイライトを奪わない()
    {
        var vm = ReadRepoFile("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // Chase/OL いずれも、進行ロック中は SelectedStep 書き換えをスキップするガードがある。
        // "if (!IsProgressLocked)" の直後に SelectedStep 代入が現れる箇所が 2 つ（Chase / OL）以上あること。
        int count = CountGuardedSelectionAssignments(vm);
        Assert.True(count >= 2, $"進行ロックガード付き SelectedStep 代入が不足しています（検出 {count} 箇所）");
    }

    [Fact]
    public void Chaseループは実行対象をステップ明示指定で継続する()
    {
        var vm = ReadRepoFile("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // Chase ループは SelectedStep ではなく step を明示指定して実行する。
        Assert.Contains("await ExecuteStepWithoutAdvanceAsync(step);", vm);
        // OL の off 行離脱も current を明示指定する。
        Assert.Contains("await ExecuteStepWithoutAdvanceAsync(current);", vm);
    }

    [Fact]
    public void 矢印とEnterも進行ロック中はループを止めず選択移動のみにする()
    {
        var vm = ReadRepoFile("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // 3 つのナビゲーション（Enter/Space=ExecuteCurrentStep, 前=PreviousStep, 次=NextStep）が
        // 進行ロック中は MoveSelectionDuringProgressLock を呼んで早期 return する。
        AssertProgressLockNavGuard(vm, "private void ExecuteCurrentStep()", "+1");
        AssertProgressLockNavGuard(vm, "private void PreviousStep()", "-1");
        AssertProgressLockNavGuard(vm, "private void NextStep()", "+1");
    }

    [Fact]
    public void ロック中移動ヘルパーはループ停止も実行もしない()
    {
        var vm = ReadRepoFile("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        int start = vm.IndexOf("private void MoveSelectionDuringProgressLock(int delta)", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "MoveSelectionDuringProgressLock が見つかりません");
        // 次のメソッド定義までを本体とみなす。
        int end = vm.IndexOf("private bool MoveSelectionForLoopExit", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "MoveSelectionDuringProgressLock の本体範囲が特定できません");
        var body = vm.Substring(start, end - start);

        // 選択のみ移動する（点灯は変えない）＝停止も実行も呼ばない。
        Assert.DoesNotContain("StopLoopExecution()", body);
        Assert.DoesNotContain("ExecuteStepWithoutAdvanceAsync", body);
        Assert.Contains("SelectedStep = EditingSteps[newIndex];", body);
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────
    private static void AssertProgressLockNavGuard(string source, string methodSig, string delta)
    {
        int m = source.IndexOf(methodSig, System.StringComparison.Ordinal);
        Assert.True(m >= 0, $"メソッドが見つかりません: {methodSig}");
        // メソッド先頭付近（日本語コメントを含む字数を考慮して広めに取る）に進行ロックの早期分岐がある。
        var head = source.Substring(m, System.Math.Min(700, source.Length - m));
        Assert.Contains($"if (IsProgressLocked) {{ MoveSelectionDuringProgressLock({delta}); return; }}", head);
    }

    private static void AssertGuardedRequestLoopExit(string source, string vmRef)
    {
        // "if (!<vmRef>.IsProgressLocked)" ブロック内で RequestLoopExit を呼ぶ形になっていること。
        int guard = source.IndexOf($"if (!{vmRef}.IsProgressLocked)", System.StringComparison.Ordinal);
        Assert.True(guard >= 0, $"進行ロックガードが見つかりません（{vmRef}）");
        int call = source.IndexOf($"{vmRef}.RequestLoopExit();", guard, System.StringComparison.Ordinal);
        Assert.True(call > guard, $"RequestLoopExit がガードの内側にありません（{vmRef}）");
        // ガードとの距離が近い（同一ブロック内）ことを確認。
        Assert.True(call - guard < 120, $"RequestLoopExit がガードから離れすぎています（{vmRef}）");
    }

    private static int CountGuardedSelectionAssignments(string source)
    {
        int count = 0;
        int idx = 0;
        while (true)
        {
            int g = source.IndexOf("if (!IsProgressLocked)", idx, System.StringComparison.Ordinal);
            if (g < 0) break;
            // このガード直後の一定範囲に SelectedStep 代入があればカウント（深いネストの字下げを考慮して広めに取る）。
            int windowEnd = System.Math.Min(source.Length, g + 320);
            var window = source.Substring(g, windowEnd - g);
            if (window.Contains("SelectedStep = ")) count++;
            idx = g + "if (!IsProgressLocked)".Length;
        }
        return count;
    }

    private static string ReadRepoFile(params string[] relativeParts)
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
