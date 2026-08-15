namespace Lib.Application.Models
{
    /// <summary>
    /// SNO端末の内蔵プログラム（A1コマンド）1 件分
    /// 概要：内蔵アニメーション（Rainbow、満点星等）のフレーム番号とボタン表示名を保持する。
    ///       CommandPanelView の A1 プリセットボタンとして動的に表示され、右クリックで編集可能。
    /// </summary>
    public class InternalProgramEntry
    {
        /// <summary>ボタン表示名（例：Rainbow）</summary>
        public string Name { get; set; } = "Prog";

        /// <summary>A1 コマンドに渡すフレーム番号</summary>
        public uint FrameNo { get; set; }

        /// <summary>ボタンを表示するかどうか</summary>
        public bool Enabled { get; set; } = true;
    }
}
