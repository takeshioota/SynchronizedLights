namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// チャンネル選択肢（ComboBox 用）。
    /// 概要：CH1(2405MHz) は端末側ファーム未実装のため <see cref="Enabled"/>=false とし、
    ///       画面には表示するが選択できないようにする（選択すると端末が復帰不能になるため）。
    ///       使用可能は CH2/CH3/CH4（2434/2451/2475 MHz）。端末の工場既定は CH4(2475MHz)。
    /// </summary>
    public sealed record ChannelOption(byte Value, string Label, bool Enabled)
    {
        /// <summary>全チャンネル選択肢（CH1 のみ選択不可）。</summary>
        public static IReadOnlyList<ChannelOption> All { get; } = new[]
        {
            new ChannelOption(1, "CH1 2405MHz（使用不可）", false),
            new ChannelOption(2, "CH2 2434MHz", true),
            new ChannelOption(3, "CH3 2451MHz", true),
            new ChannelOption(4, "CH4 2475MHz（既定）", true),
        };
    }
}
