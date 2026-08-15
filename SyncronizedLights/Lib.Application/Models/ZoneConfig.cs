namespace Lib.Application.Models
{
    /// <summary>
    /// ゾーン構成情報
    /// 概要：Zone1〜4 を特定のポート（COMx）に割り当て、
    ///       ゾーンごとに異なる受信チャネル／電力を運用するための設定。
    ///       UserState と一緒に永続化される。
    /// </summary>
    public class ZoneConfig
    {
        /// <summary>ゾーン名（"Zone1"〜"Zone4"）</summary>
        public string ZoneName { get; set; } = "";

        /// <summary>
        /// 割り当てポート名
        /// 概要：未割当時は "-" または空文字。COM ポート名（"COM3" 等）を格納。
        /// </summary>
        public string AssignedPort { get; set; } = "-";

        /// <summary>受信チャネル（1〜4）</summary>
        public byte Channel { get; set; } = 1;

        /// <summary>送信電力（0〜3）</summary>
        public byte Power { get; set; } = 3;
    }
}