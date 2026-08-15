namespace Lib.Application.Models
{
    /// <summary>
    /// 煽りボタンに割り当てる色 1 件分
    /// 概要：シーケンス再生中などにオペレータが即興で押下すると一時的に LED を点灯させる。
    ///       2 つまでローカル保存できる。右クリックで色編集可能。
    /// </summary>
    public class AggressiveColorEntry
    {
        /// <summary>ボタン表示名（例：煽り 1）</summary>
        public string Name { get; set; } = "煽り";

        /// <summary>R 値（0〜255）</summary>
        public byte R { get; set; } = 255;

        /// <summary>G 値（0〜255）</summary>
        public byte G { get; set; } = 255;

        /// <summary>B 値（0〜255）</summary>
        public byte B { get; set; } = 255;
    }
}
