using Lib.Application.Models;
using Lib.Ui.Screens.ViewModels;

namespace Lib.Ui.Screens.Tests;

/// <summary>
/// シーケンス行モデル(SequenceStepWrapper)の単体テスト。実プロダクトコードを直接検証。
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
}
