using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Domain.Enums
{
    /// <summary>
    /// 制御対象種別
    /// 概要：シンクロライト制御における送信対象を表す列挙型。
    /// 全体制御やグループ単位制御など、送信コマンドの対象指定に使用する。
    /// </summary>
    public enum Target
    {
        /// <summary>全体</summary>
        All,
        /// <summary>グループ01</summary>
        Group01,
        /// <summary>グループ02</summary>
        Group02,
        /// <summary>グループ03</summary>
        Group03,
        /// <summary>グループ04</summary>
        Group04,
        /// <summary>グループ05</summary>
        Group05,
        /// <summary>グループ06</summary>
        Group06
    }
}
