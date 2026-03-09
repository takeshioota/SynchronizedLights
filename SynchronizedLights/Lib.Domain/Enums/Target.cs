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
        All,
        Group01,
        Group02,
        Group03,
        Group04,
        Group05,
        Group06
    }
}
