using Lib.Application.Models;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// シーケンス行モデル(SequenceStepWrapper)の単体テスト。実プロダクトコードを直接検証。
///  【BUG-20250922-01】恒久 WYSIWYG：Rainbow 行はパレット未設定（2色未満）なら既定7色を実データ
///                      として実体化し、表示・保存・実機再生を行自身のパレットで一致させる。
///  【v2.0.1】BPM 既定値・クランプ・null マッピング（120 特別扱いの撤廃）。
///  【v2.0.0】No 列 RowNumber の型(double?)・既定・null マッピング（小数保持）。
/// 純ロジック（実機不要）。
/// </summary>
public class SequenceStepWrapperTests
{
    // ───────────────────────── v2.0.1: BPM ─────────────────────────

    [Fact]
    public void Bpm既定値は0_旧既定120からの変更()
    {
        // 旧仕様は既定 120（=「未設定」に化けて非単調の原因）。v2.0.1 で既定 0（未設定）へ。
        Assert.Equal(0, new SequenceStepWrapper().Bpm);
    }

    [Theory]
    [InlineData(-1, 0)]       // 下限クランプ
    [InlineData(0, 0)]
    [InlineData(120, 120)]    // 120 は特別扱いせずそのまま保持
    [InlineData(6000, 6000)]
    [InlineData(7000, 6000)]  // 上限クランプ
    public void Bpmは0から6000にクランプ(int input, int expected)
    {
        var w = new SequenceStepWrapper { Bpm = input };
        Assert.Equal(expected, w.Bpm);
    }

    [Fact]
    public void 復元時_Bpmのnullは0へ_120化しない()
    {
        var w = new SequenceStepWrapper(new SequenceStep { Bpm = null });
        Assert.Equal(0, w.Bpm); // 旧仕様は null を 120 扱いにしていた
    }

    [Fact]
    public void 復元時_Bpm120はそのまま保持()
    {
        var w = new SequenceStepWrapper(new SequenceStep { Bpm = 120 });
        Assert.Equal(120, w.Bpm);
    }

    // ───────────────────────── v2.0.0: No 列（RowNumber）─────────────────────────

    [Fact]
    public void RowNumber既定は0_0_double_nullable型()
    {
        // RowNumber は double?（空欄不可＝null は 00.0 として扱う仕様）。既定 0.0。
        double? rn = new SequenceStepWrapper().RowNumber;   // 代入が通ること＝double? 型であることの静的保証
        Assert.Equal(0.0, rn);
    }

    [Fact]
    public void 復元時_StepNumberのnullは0_0へ()
    {
        var w = new SequenceStepWrapper(new SequenceStep { StepNumber = null });
        Assert.Equal(0.0, w.RowNumber);
    }

    [Fact]
    public void 復元時_StepNumberの小数は丸めず保持()
    {
        var w = new SequenceStepWrapper(new SequenceStep { StepNumber = 12.3 });
        Assert.Equal(12.3, w.RowNumber!.Value, precision: 6);
    }

    // ───────────────────────── 付随: Time（参考）─────────────────────────

    [Fact]
    public void TimeMsは負値を0にクランプ()
    {
        var w = new SequenceStepWrapper { TimeMs = -100 };
        Assert.Equal(0, w.TimeMs);
    }

    [Fact]
    public void TimeSecは0msでnull_未設定表示()
    {
        var w = new SequenceStepWrapper { TimeMs = 0 };
        Assert.Null(w.TimeSec);
    }

    // ─────────── BUG-20250922-01: Rainbow 恒久 WYSIWYG（既定7色の実体化）───────────

    [Fact]
    public void Rainbow行_Cmd選択で既定7色を実データとして実体化する()
    {
        // Cmd 列で直接 Rainbow を選んだだけの行（mode 既定 Solid）。データそのものに既定7色が入り、
        // 表示・保存・実機再生を行自身のパレットで一致させる（WYSIWYG）。
        var w = new SequenceStepWrapper { CommandType = "Rainbow" };
        Assert.Equal(7, w.RainbowColors.Count);          // データが実体化されている
        Assert.Equal("7色 (常時)", w.RainbowSummary);
        Assert.Equal(7, w.RainbowDisplayColors.Count());
        // 実機再生も行の7色を採用する（ToModel が7色を永続化＝パネル非依存）。
        Assert.Equal(7, w.ToModel().RainbowColors!.Count);
    }

    [Fact]
    public void 旧データ_パレット無しRainbow行は読込時に既定7色へ実体化する()
    {
        // 保存済み（旧版）の RainbowColors=null な Rainbow 行を読込むと既定7色で実体化される。
        var w = new SequenceStepWrapper(new SequenceStep { CommandType = "Rainbow", RainbowColors = null });
        Assert.Equal(7, w.RainbowColors.Count);
        Assert.Equal("7色 (常時)", w.RainbowSummary);
    }

    [Fact]
    public void Rainbow行_2色以上のパレットは実体化で壊さない()
    {
        // 既に色があれば上書きしない（ユーザ設定を保持）。読込時も実数を表示。
        var w = new SequenceStepWrapper(new SequenceStep
        {
            CommandType = "Rainbow",
            RainbowMode = RainbowMode.FadeInOut,
            RainbowColors = new() { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255) },
        });
        Assert.Equal(3, w.RainbowColors.Count);
        Assert.Equal("3色 (FI/FO)", w.RainbowSummary);
        Assert.Equal(3, w.RainbowDisplayColors.Count());
    }

    [Fact]
    public void Rainbow行_独自色にしてCmd往復しても既定7色へ戻さない()
    {
        var w = new SequenceStepWrapper { CommandType = "Rainbow" }; // 実体化で7色
        w.RainbowColors.Clear();
        w.RainbowColors.Add(new RgbColorItem(1, 2, 3));
        w.RainbowColors.Add(new RgbColorItem(4, 5, 6)); // 独自2色
        w.CommandType = "Color";                        // 退避（色は保持）
        w.CommandType = "Rainbow";                      // 2色以上なので実体化しない
        Assert.Equal(2, w.RainbowColors.Count);
        Assert.Equal("2色 (常時)", w.RainbowSummary);
    }

    [Fact]
    public void Rainbow行_Randomは端末仕様どおり常に7色ランダム()
    {
        // Random(3.21) は A9 02 パレットを無視し内蔵7色で光る＝色数に関わらず "7色 (ランダム)"。
        var w = new SequenceStepWrapper { CommandType = "Rainbow", RainbowMode = RainbowMode.Random };
        Assert.Equal("7色 (ランダム)", w.RainbowSummary);
        Assert.Equal(7, w.RainbowDisplayColors.Count());
    }

    [Fact]
    public void 非Rainbow行はサマリ空_かつ実体化しない()
    {
        var w = new SequenceStepWrapper { CommandType = "Color" };
        Assert.Equal("", w.RainbowSummary);
        Assert.Empty(w.RainbowColors);
    }
}
