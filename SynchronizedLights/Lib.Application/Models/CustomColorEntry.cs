namespace Lib.Application.Models
{
    /// <summary>
    /// オペレータが現場で調整するカスタム色 1 件分
    /// 概要：シンクロライト発色の中間色調整のため、4 つまでローカル保存できる。
    ///       シーケンス編集ウィンドウの色プリセットに並んで表示され、右クリックで編集可能。
    /// </summary>
    public class CustomColorEntry
    {
        /// <summary>ボタン表示名（例：Custom 1）</summary>
        public string Name { get; set; } = "Custom";

        /// <summary>R 値（0〜255）</summary>
        public byte R { get; set; } = 255;

        /// <summary>G 値（0〜255）</summary>
        public byte G { get; set; } = 255;

        /// <summary>B 値（0〜255）</summary>
        public byte B { get; set; } = 255;
    }
}
