using System.IO;
using Lib.Application.Models;
using Lib.Application.Services;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// 時間ベースシーケンス永続化(TimeBasedSequenceStore)の単体・耐久テスト。
/// 一時ディレクトリを baseDir に注入し、実ファイル I/O で JSON 保存／読込／削除を検証する
/// （実機不要・ネットワーク不要）。壊れたファイルのスキップ・名前重複判定・大量保存の耐久も確認。
/// </summary>
public class TimeBasedSequenceStoreTests : IDisposable
{
    private readonly string _baseDir;
    private readonly TimeBasedSequenceStore _store;

    public TimeBasedSequenceStoreTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "sl_store_test_" + Guid.NewGuid().ToString("N"));
        _store = new TimeBasedSequenceStore(_baseDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true); }
        catch { /* テスト後始末失敗は無視 */ }
    }

    private static TimeBasedSequence Seq(string name, params SequenceStep[] steps)
        => new() { Name = name, Steps = new List<SequenceStep>(steps) };

    // ───────────────────────── 基本 CRUD ─────────────────────────

    [Fact]
    public void Save_Id未設定なら新規GUIDを採番する()
    {
        var seq = Seq("A");
        seq.Id = "";
        Assert.True(_store.Save(seq));
        Assert.False(string.IsNullOrWhiteSpace(seq.Id));
    }

    [Fact]
    public void Save_Loadでラウンドトリップする()
    {
        var seq = Seq("オープニング", new SequenceStep { TimeMs = 1000, CommandType = "Color" });
        _store.Save(seq);

        var loaded = _store.Load(seq.Id);
        Assert.NotNull(loaded);
        Assert.Equal("オープニング", loaded!.Name);
        Assert.Single(loaded.Steps);
        Assert.Equal(1000, loaded.Steps[0].TimeMs);
    }

    [Fact]
    public void Load_存在しないIdはnull()
    {
        Assert.Null(_store.Load("does-not-exist"));
        Assert.Null(_store.Load(""));
    }

    [Fact]
    public void Delete_存在すれば真_存在しなければ偽()
    {
        var seq = Seq("消す");
        _store.Save(seq);
        Assert.True(_store.Delete(seq.Id));
        Assert.Null(_store.Load(seq.Id));
        Assert.False(_store.Delete(seq.Id)); // 二度目は無い
    }

    // ───────────────────────── 名前判定 ─────────────────────────

    [Fact]
    public void ExistsByName_大文字小文字を区別しない()
    {
        var seq = Seq("Encore");
        _store.Save(seq);
        Assert.True(_store.ExistsByName("encore"));
        Assert.True(_store.ExistsByName("ENCORE"));
        Assert.False(_store.ExistsByName("別名"));
    }

    [Fact]
    public void ExistsByName_excludeIdで自分自身を除外できる()
    {
        var seq = Seq("Solo");
        _store.Save(seq);
        Assert.False(_store.ExistsByName("Solo", excludeId: seq.Id)); // 自分を除けば重複なし
        Assert.True(_store.ExistsByName("Solo"));                     // 除外なしなら存在
    }

    [Fact]
    public void LoadByName_名前で1件取得できる()
    {
        _store.Save(Seq("ラスト"));
        Assert.NotNull(_store.LoadByName("ラスト"));
        Assert.Null(_store.LoadByName("無い名前"));
    }

    // ───────────────────────── LoadAll ─────────────────────────

    [Fact]
    public void LoadAll_名前順にソートされ壊れたファイルはスキップされる()
    {
        _store.Save(Seq("Charlie"));
        _store.Save(Seq("alpha"));
        _store.Save(Seq("Bravo"));

        // 壊れた JSON を投入 → 読込でスキップされ、例外を投げない
        File.WriteAllText(Path.Combine(_store.GetStoreDirectoryPath(), "broken.json"), "{ これは壊れた JSON ");

        var all = _store.LoadAll();
        Assert.Equal(3, all.Count);
        var names = all.Select(s => s.Name).ToArray();
        Assert.Equal(new[] { "alpha", "Bravo", "Charlie" }, names); // OrdinalIgnoreCase 昇順
    }

    // ───────────────────────── ラウンドトリップの型保持 ─────────────────────────

    [Fact]
    public void ラウンドトリップでStepNumberの小数とRainbowパレットが保持される()
    {
        var step = new SequenceStep
        {
            TimeMs = 2500,
            StepNumber = 12.3,
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.FadeInOut,
            RainbowColors = new List<Rgb> { new(10, 20, 30), new(40, 50, 60), new(70, 80, 90) },
            RainbowFadeInMs = 1234,
            RainbowFadeOutMs = 5000,
        };
        var seq = Seq("虹", step);
        _store.Save(seq);

        var loaded = _store.Load(seq.Id)!;
        var s = loaded.Steps[0];
        Assert.Equal(12.3, s.StepNumber!.Value, precision: 6);
        Assert.Equal(RainbowMode.FadeInOut, s.RainbowMode);
        Assert.Equal(3, s.RainbowColors!.Count);
        Assert.Equal(new Rgb(40, 50, 60), s.RainbowColors[1]);
        Assert.Equal(1234, s.RainbowFadeInMs);
        Assert.Equal(5000, s.RainbowFadeOutMs);
    }

    // ───────────────────────── 耐久 ─────────────────────────

    [Fact]
    public void 大量保存_200件を保存し全件読み込める_耐久()
    {
        for (int i = 0; i < 200; i++)
            _store.Save(Seq($"seq_{i:D3}", new SequenceStep { TimeMs = i * 10, CommandType = "Color" }));

        var all = _store.LoadAll();
        Assert.Equal(200, all.Count);
        Assert.All(all, s => Assert.NotNull(_store.Load(s.Id)));
    }
}
