namespace Lib.Domain.Enums
{
    /// <summary>
    /// レインボーエフェクトのモード種別（プロトコル V4.5 セクション 3.16-3.21）
    /// 概要：0xA9 0x03 コマンドの mode バイトに対応する。
    /// </summary>
    public enum RainbowMode
    {
        /// <summary>常時点灯（3.16: mode=0x00）— 色を順番に切り替えて常時点灯</summary>
        Solid = 0,

        /// <summary>点滅（3.17: mode=0x01）— 色を順番に切り替えて点滅</summary>
        Blink = 1,

        /// <summary>フェードイン／フェードアウト（3.18: mode=0x02）— FI+FO の繰り返し</summary>
        FadeInOut = 2,

        /// <summary>フェードイン（3.19: mode=0x03）— FI のみ</summary>
        FadeIn = 3,

        /// <summary>フェードアウト（3.20: mode=0x04）— FO のみ</summary>
        FadeOut = 4,

        /// <summary>7色ランダム点滅（3.21: mode=0x05）— ランダム順で色を切り替え</summary>
        Random = 5
    }
}
