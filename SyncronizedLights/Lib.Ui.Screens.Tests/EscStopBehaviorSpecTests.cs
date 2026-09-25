using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// BUG-20260923-02: Esc は停止ボタンと同じ挙動でなければならない（エフェクトを停止する。リセットしない）。
///
/// 原因：IntegratedWindow の Esc 経路（StopAllAndRestoreGrid → RestoreGridEditableAfterStop）は
///       停止後にグリッド行を選択し直すが、SequenceEditorViewModel.OnSelectedStepChanged は選択行を
///       「選択即実行」するため、停止直後に同じ行が再実行され、エフェクトがリセットされて見えた。
/// 対策：選択復元を _suppressAutoExecute を立てた状態（RunWithoutAutoExecute）で行い、再実行を抑止する。
///
/// テスト方針は既存 SpecTests に倣い、プロダクトソースへ対策の要点が存在することを走査で保証する
/// （Esc 押下の実体験は実機確認項目）。
/// </summary>
public class EscStopBehaviorSpecTests
{
    [Fact]
    public void VM_RunWithoutAutoExecuteは自動実行を抑止する()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");

        // 抑止ヘルパーが存在し、_suppressAutoExecute を立てて action を実行する。
        Assert.Matches(new Regex(@"public\s+void\s+RunWithoutAutoExecute\s*\(\s*Action"), vm);
        var body = Between(vm, "public void RunWithoutAutoExecute", "}");
        Assert.Contains("_suppressAutoExecute = true", body);

        // OnSelectedStepChanged は _suppressAutoExecute のとき選択即実行しない（早期 return）。
        Assert.Matches(new Regex(@"if\s*\(\s*_suppressAutoExecute\s*\)\s*return\s*;"), vm);
    }

    [Fact]
    public void IntegratedWindow_停止後の選択復元は自動実行を抑止して行う()
    {
        var win = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml.cs");

        // 選択復元メソッドの本体で、グリッド選択の張り替えを RunWithoutAutoExecute で包む。
        var restore = Between(win, "private void RestoreGridEditableAfterStop", "catch");
        Assert.Contains("RunWithoutAutoExecute(", restore);
        Assert.Contains("StepDataGrid.SelectedItem", restore);
        // 抑止で包む前にグリッド行を選択していないこと（包み込み順の担保）。
        Assert.True(
            restore.IndexOf("RunWithoutAutoExecute(", System.StringComparison.Ordinal)
                < restore.IndexOf("StepDataGrid.SelectedItems.Clear();", System.StringComparison.Ordinal),
            "選択復元は RunWithoutAutoExecute で包まれていなければならない");
    }

    [Fact]
    public void IntegratedWindow_Escも停止ボタンも同じStopExecutionCommandへ至る()
    {
        var win = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml.cs");
        var xaml = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml");

        // Esc → 全停止ヘルパー、ヘルパー内で StopExecutionCommand を実行。
        Assert.Contains("StopAllAndRestoreGrid();", win);
        Assert.Contains("StopExecutionCommand.Execute(null)", win);
        // 停止ボタンも同じ StopExecutionCommand にバインド。
        Assert.Contains("StopExecutionCommand", xaml);
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
