using Lib.Application.Facades;
using Lib.Application.Models;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// DummyLightingFacade（実機不要のオフライン擬似実装）の単体・耐久テスト。
///  ・接続状態／自動再接続（SimulateDisconnect→TryReconnect）
///  ・未接続時の SetColor 例外
///  ・キュー満杯ドロップ（DropNewest ポリシー）
///  ・シーケンス登録／再生状態
///  ・大量送信でも例外なく安定（Wait ポリシー）
/// を検証する。IsEffectRunning はオフライン擬似のため常に false。
/// </summary>
public class DummyLightingFacadeTests
{
    // ───────────────────────── 接続 ─────────────────────────

    [Fact]
    public void 初期状態は未接続かつIsEffectRunningは常に偽()
    {
        var sut = new DummyLightingFacade();
        Assert.False(sut.IsConnected);
        Assert.False(sut.IsEffectRunning);
    }

    [Fact]
    public async Task Connectで接続状態になりIntendedPortsが保存される()
    {
        var sut = new DummyLightingFacade();
        await sut.ConnectAsync(new[] { "COM1", "COM2" });

        Assert.True(sut.IsConnected);
        Assert.Equal(new[] { "COM1", "COM2" }, sut.ConnectedPorts);
        Assert.Equal(new[] { "COM1", "COM2" }, sut.IntendedPorts);
    }

    [Fact]
    public async Task Disconnectで全ポートが閉じられintendedもクリアされる()
    {
        var sut = new DummyLightingFacade();
        await sut.ConnectAsync(new[] { "COM1" });
        await sut.DisconnectAsync();

        Assert.False(sut.IsConnected);
        Assert.Empty(sut.IntendedPorts);
    }

    [Fact]
    public async Task 未接続でのSetColorはInvalidOperationExceptionを投げる()
    {
        var sut = new DummyLightingFacade();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SetColorAsync(Target.All, Rgb.Red));
    }

    [Fact]
    public async Task 接続後のSetColorは例外なく完了する()
    {
        var sut = new DummyLightingFacade(sendDelayMs: 0);
        await sut.ConnectAsync(new[] { "COM1" });
        await sut.SetColorAsync(Target.All, new Rgb(1, 2, 3)); // 例外が出ないこと
        Assert.Null(sut.LastError);
    }

    // ───────────────────────── 自動再接続 ─────────────────────────

    [Fact]
    public async Task SimulateDisconnect後にTryReconnectで復旧しReconnectCountが増える()
    {
        var sut = new DummyLightingFacade();
        await sut.ConnectAsync(new[] { "COM1", "COM2" });

        sut.SimulateDisconnect();
        Assert.False(sut.IsConnected);
        Assert.Equal(new[] { "COM1", "COM2" }, sut.IntendedPorts); // intended は保持

        int reconnected = await sut.TryReconnectMissingPortsAsync();
        Assert.Equal(2, reconnected);
        Assert.True(sut.IsConnected);
        Assert.Equal(2, sut.ReconnectCount);
    }

    [Fact]
    public async Task 欠落ポートが無ければTryReconnectは0を返す()
    {
        var sut = new DummyLightingFacade();
        await sut.ConnectAsync(new[] { "COM1" });
        Assert.Equal(0, await sut.TryReconnectMissingPortsAsync());
    }

    // ───────────────────────── シーケンス再生状態 ─────────────────────────

    [Fact]
    public async Task シーケンス再生状態はPlay_Stopで遷移する()
    {
        var sut = new DummyLightingFacade();
        var (playing0, name0) = await sut.GetSequencePlayStatusAsync();
        Assert.False(playing0);
        Assert.Null(name0);

        await sut.PlaySequenceAsync("オープニング");
        var (playing1, name1) = await sut.GetSequencePlayStatusAsync();
        Assert.True(playing1);
        Assert.Equal("オープニング", name1);

        await sut.StopSequenceAsync();
        var (playing2, _) = await sut.GetSequencePlayStatusAsync();
        Assert.False(playing2);
    }

    [Fact]
    public async Task Upsertで登録されListに現れ_Deleteで消える()
    {
        var sut = new DummyLightingFacade();
        await sut.UpsertSequenceAsync("seqA", new List<SequenceApiStep>());
        await sut.UpsertSequenceAsync("seqB", new List<SequenceApiStep>());

        var names = await sut.ListSequenceNamesAsync();
        Assert.Contains("seqA", names);
        Assert.Contains("seqB", names);

        await sut.DeleteSequenceFromApiAsync("seqA");
        var names2 = await sut.ListSequenceNamesAsync();
        Assert.DoesNotContain("seqA", names2);
        Assert.Contains("seqB", names2);
    }

    // ───────────────────────── キュー満杯ドロップ ─────────────────────────

    [Fact]
    public async Task DropNewestポリシーはキュー満杯でコマンドをドロップする()
    {
        // sendDelayMs を極端に長くしてキュー滞留を作り、閾値超過でドロップさせる（決定的）。
        var sut = new DummyLightingFacade(
            sendDelayMs: 60_000, queueCapacity: 10, queuePolicy: "DropNewest", dropThreshold: 0.95);
        await sut.ConnectAsync(new[] { "COM1" });

        for (int i = 0; i < 20; i++)
            await sut.SetColorAsync(Target.All, new Rgb((byte)i, 0, 0));

        Assert.True(sut.DroppedCount >= 1, $"満杯でドロップが発生する（実際 {sut.DroppedCount} 件）");
        Assert.NotNull(sut.LastError);
        Assert.Contains("満杯", sut.LastError!);
    }

    [Fact]
    public async Task Waitポリシーでは大量送信してもドロップしない_耐久()
    {
        var sut = new DummyLightingFacade(sendDelayMs: 0, queueCapacity: 16, queuePolicy: "Wait");
        await sut.ConnectAsync(new[] { "COM1" });

        for (int i = 0; i < 3000; i++)
            await sut.SetColorAsync(Target.All, new Rgb((byte)(i & 0xFF), 0, 0));

        Assert.Equal(0, sut.DroppedCount);
    }
}
