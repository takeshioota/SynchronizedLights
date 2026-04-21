namespace Lib.Application.Models
{
    /// <summary>
    /// ユーザー操作状態の永続化オブジェクト
    /// 概要：速度・色・対象・命令名・ポート設定などを保存し、次回起動時に復元する。
    /// </summary>
    public class UserState
    {
        /// <summary>最後に使用した速度（ミリ秒）</summary>
        public int SpeedValueMs { get; set; } = 1000;

        /// <summary>最後に選択した色（R）</summary>
        public byte ColorR { get; set; } = 255;

        /// <summary>最後に選択した色（G）</summary>
        public byte ColorG { get; set; } = 255;

        /// <summary>最後に選択した色（B）</summary>
        public byte ColorB { get; set; } = 255;

        /// <summary>
        /// 最後に選択した対象（"All" / "Group01" 〜 "Group08"）
        /// 概要：Enumではなく文字列で保存。Enumの値変更に強い。
        /// </summary>
        public string SelectedTarget { get; set; } = "All";

        /// <summary>最後に使用した命令名（ログ用ラベル）</summary>
        public string CommandName { get; set; } = "";

        /// <summary>最後に選択した送信先ポート（"ALL" / "PortA" / "PortB"）</summary>
        public string SelectedPort { get; set; } = "ALL";

        /// <summary>最後に使用した送信機チャネル（FA、1〜4）</summary>
        public byte TransmitterChannel { get; set; } = 1;

        /// <summary>最後に使用した送信機電力（FB、0〜3）</summary>
        public byte TransmitterPower { get; set; } = 3;

        /// <summary>
        /// 保存タイムスタンプ（デバッグ用）
        /// </summary>
        public string SavedAt { get; set; } = "";
    }
}