using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Domain.ValueObjects
{
    /// <summary>
    /// RGB色情報
    /// 概要：シンクロライトの発光色を表す値オブジェクト。
    /// 赤・緑・青の各成分を保持し、色変更や演出制御のパラメータとして使用する。
    /// </summary>
    public record Rgb(byte R, byte G, byte B)
    {
        #region 静的メンバ
        /// <summary>
        /// 黒色
        /// </summary>
        public static readonly Rgb Black = new(0, 0, 0);

        /// <summary>
        /// 白色
        /// </summary>
        public static readonly Rgb White = new(255, 255, 255);

        /// <summary>
        /// 赤色
        /// </summary>
        public static readonly Rgb Red = new(255, 0, 0);

        /// <summary>
        /// 緑色
        /// </summary>
        public static readonly Rgb Green = new(0, 255, 0);

        /// <summary>
        /// 青色
        /// </summary>
        public static readonly Rgb Blue = new(0, 0, 255);
        #endregion 静的メンバ
    }
}
