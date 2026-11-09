using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 【v2.12.0 追加開発 / No.28=BUG-20260923-02 取消し】
/// クライアント要求(2026-11-05)により、Esc は「現在の Effect を先頭から再開（再実行）」する v1.4.8 相当の
/// 動作へ戻す。No.28 で Esc を『停止ボタンと同じ停止のみ』に揃えた修正（選択即実行の抑止）を破棄する。
///   ・Esc → 停止（StopExecution）後、現在の演出を先頭から再開する（ReExecuteCurrentStepForEsc）。
///     単発 Effect/Color/Rainbow 行は現在行を再実行、OL/Chase ブロック行はブロックを先頭から回し直す。
///   ・停止ボタンは従来どおり StopExecutionCommand 直結で『停止のみ』を維持する（Esc と結果が一致しなくてよい）。
///
/// テスト方針は既存 SpecTests に倣い、プロダクトソースへ対策の要点が存在することを走査で保証する
/// （Esc 押下の実体験・実際の再開見た目は実機確認項目）。
/// </summary>
public class EscStopBehaviorSpecTests
{
    [Fact]
    public void VM_ReExecuteCurrentStepForEscが単発は再実行しループは先頭から回し直す()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // Esc 再開用の公開メソッドが存在する。
        Assert.Matches(new Regex(@"public\s+void\s+ReExecuteCurrentStepForEsc\s*\(\s*\)"), vm);

        // メソッド本体（次メソッド SyncRainbowPanelFromStep の手前まで）を取り出す。
        var body = Between(vm, "public void ReExecuteCurrentStepForEsc", "private void SyncRainbowPanelFromStep");

        // 単発 Effect／Color／Rainbow 行は選択即実行と同じ ExecuteStepWithoutAdvanceAsync で現在行を再実行する。
        Assert.Contains("ExecuteStepWithoutAdvanceAsync()", body);
        // OL/Chase ブロック行は単発ではなくブロックを先頭から回し直す（TryAutoStartLoop でループ再起動）。
        Assert.Contains("IsAutoStartableLoopBlockRow", body);
        Assert.Contains("TryAutoStartLoop(", body);
        // Emergency／進行ロック中は再開しない（既存ガードと整合）。
        Assert.Contains("IsEmergencyActive", body);
        Assert.Contains("IsProgressLocked", body);
    }

    [Fact]
    public void IntegratedWindow_Escは停止後に現在行を再開する()
    {
        var win = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml.cs");

        // Esc → 全停止ヘルパー。
        Assert.Contains("StopAllAndRestoreGrid();", win);
        Assert.Contains("StopExecutionCommand.Execute(null)", win);

        // 停止後の復帰処理の中で、現在行を再開（再実行）する。
        var restore = Between(win, "private void RestoreGridEditableAfterStop", "catch");
        Assert.Contains("ReExecuteCurrentStepForEsc()", restore);
        // グリッド選択の張り替え自体は自動実行を抑止して行う（二重実行防止）。
        Assert.Contains("RunWithoutAutoExecute(", restore);
    }

    [Fact]
    public void SequenceEditorWindow_Escは停止後に現在行を再開する()
    {
        var win = ReadSource("Lib.Ui.Screens", "Views", "SequenceEditorWindow.xaml.cs");

        // Esc 経路で停止したうえで再開メソッドを呼ぶ。
        Assert.Contains("StopExecutionCommand.Execute(null)", win);
        Assert.Contains("ReExecuteCurrentStepForEsc()", win);
    }

    [Fact]
    public void 停止ボタンは停止のみで再開しない()
    {
        // 停止ボタンは StopExecutionCommand に直結（XAML）。
        var xaml = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml");
        Assert.Contains("StopExecutionCommand", xaml);

        // VM 内では Esc 再開メソッドは『定義 1 箇所のみ』で、内部（停止コマンド等）からは呼ばれない。
        // ＝停止ボタン経路で再開（再実行）は起きない。呼び出しは View 側の Esc ハンドラからのみ。
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");
        int occurrences = Regex.Matches(vm, "ReExecuteCurrentStepForEsc").Count;
        Assert.Equal(1, occurrences);
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
