using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Domain.Enums
{
    /// <summary>
    /// UIカテゴリ種別
    /// 概要：メイン画面で切り替える機能カテゴリを表す列挙型。
    /// Preset、Mode、Animation、Sequence、Setting などの画面状態管理に使用する。
    /// </summary>
    public enum UiCategory
    {
        Preset = 0,
        Mode = 1,
        Animation = 2,
        Sequence = 3,
        Setting = 4
    }
}
