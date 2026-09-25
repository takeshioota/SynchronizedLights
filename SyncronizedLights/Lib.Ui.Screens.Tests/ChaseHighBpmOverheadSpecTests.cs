using System.IO;
using System.Text.RegularExpressions;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// BUG-20260923-03: 高 BPM（Chase は ~400BPM〜）で色が飛ぶ。
///
/// 原因：Chase は各ステップで ExecuteStepWithoutAdvanceAsync を呼ぶが、色送出前に毎回
///       StopEffect＋StopRainbow（各 HTTP 往復）＋固定 Task.Delay(50) を実行しており、
///       1 ステップの固定オーバーヘッド（≈50ms＋HTTP2往復）が高 BPM の周期を食い潰し、
///       各色の実表示窓が消えて「色飛び」になっていた。
/// 対策：色→色（Color/Off）で、かつ API 側にエフェクト/レインボーが走っていない（IsEffectRunning=false）
///       ときは事前停止プリアンブルを省く。走っている場合・非色コマンドは従来どおり停止する。
///
/// テスト方針は既存 SpecTests に倣い、対策の要点がプロダクトソースに存在することを走査で保証する
/// （実際に何 BPM まで飛ばないかは端末描画限界に依存するため実機確認項目）。
/// </summary>
public class ChaseHighBpmOverheadSpecTests
{
    [Fact]
    public void 事前停止は色から色かつ非稼働のとき省かれる()
    {
        var vm = ReadSource("Lib.Ui.Screens", "ViewModels", "SequenceEditorViewModel.cs");
        var method = Between(vm, "private async Task ExecuteStepWithoutAdvanceAsync", "switch (step.CommandType)");

        // 事前停止の要否フラグ：Color/Off かつ IsEffectRunning=false のとき省く。
        Assert.Matches(new Regex(@"needPreStop\s*=\s*\(\s*step\.CommandType\s+is\s+not\s+""Color""\s+and\s+not\s+""Off""\s*\)\s*\|\|\s*_lighting\.IsEffectRunning"), method);

        // 停止プリアンブル（StopEffect/StopRainbow/固定50ms待機）は needPreStop ガードの内側にある。
        int guard = method.IndexOf("if (needPreStop)", System.StringComparison.Ordinal);
        Assert.True(guard >= 0, "needPreStop ガードが無い");
        Assert.True(guard < method.IndexOf("StopEffectAsync", System.StringComparison.Ordinal), "StopEffect はガード内であること");
        Assert.True(guard < method.IndexOf("StopRainbowAsync", System.StringComparison.Ordinal), "StopRainbow はガード内であること");
        Assert.True(guard < method.IndexOf("Task.Delay(50)", System.StringComparison.Ordinal), "固定50ms待機はガード内であること");
    }

    [Fact]
    public void ILightingFacadeはIsEffectRunningを公開する()
    {
        var iface = ReadSource("Lib.Application", "Interfaces", "ILightingFacade.cs");
        Assert.Matches(new Regex(@"bool\s+IsEffectRunning\s*\{\s*get;\s*\}"), iface);

        var api = ReadSource("Lib.Application", "Facades", "ApiLightingFacade.cs");
        Assert.Contains("IsEffectRunning => _apiEffectRunning", api);
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
