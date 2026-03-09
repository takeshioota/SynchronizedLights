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
        All = 0,
        Group1 = 1,
        Group2 = 2,
        Group3 = 3,
        Group4 = 4
    }
}
