using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// BUG-20260923-04: BPM の数値を編集すると桁が 1 つ消える（例: 200 の先頭 2 を 1 に変えると 100 でなく 10）。
///
/// 原因：BPM 列（DataGridTextColumn）が int プロパティに対し UpdateSourceTrigger=PropertyChanged で
///       バインドされていたため、1 打鍵ごとに text→int→text の往復が起き、中間テキストが正規化
///       （"00"→"0" 等）されてキャレットもリセットされ、編集途中の桁が失われていた。
/// 対策：隣の Time 列と同じ UpdateSourceTrigger=LostFocus に統一し、確定（セル離脱）時にだけ 1 回だけ
///       パースする（BPM はループ実行時に読むだけでライブ反映は不要）。両画面（SequenceEditorWindow /
///       IntegratedWindow）に適用する。
///
/// テストはプロダクト XAML を走査し、BPM バインドが LostFocus であり PropertyChanged でないことを保証する。
/// （実際のキー入力挙動は実機/手動確認項目。）
/// </summary>
public class BpmEditBindingSpecTests
{
    [Fact]
    public void SequenceEditorWindowのBPM列はLostFocusで編集する()
    {
        var xaml = ReadSource("Lib.Ui.Screens", "Views", "SequenceEditorWindow.xaml");
        AssertBpmUsesLostFocus(xaml);
    }

    [Fact]
    public void IntegratedWindowのBPM列はLostFocusで編集する()
    {
        var xaml = ReadSource("SynchronizedLights.UI", "IntegratedWindow.xaml");
        AssertBpmUsesLostFocus(xaml);
    }

    private static void AssertBpmUsesLostFocus(string xaml)
    {
        // "Binding Bpm ... UpdateSourceTrigger=..." を抜き出して判定する。
        var m = Regex.Match(xaml, @"Binding\s+Bpm[^}]*UpdateSourceTrigger=(\w+)");
        Assert.True(m.Success, "BPM バインドが見つかりません");
        Assert.Equal("LostFocus", m.Groups[1].Value);
        Assert.DoesNotContain("Binding Bpm, UpdateSourceTrigger=PropertyChanged", xaml);
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
