using Lib.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lib.Application.Interfaces
{
    /// <summary>
    /// Animation演出機能
    /// 概要：あらかじめ定義されたアニメーションパターンを再生する機能。
    /// UIから選択されたアニメーションIDをもとにコマンドを生成し、Transport経由で送信する。
    /// </summary>
    public interface IAnimationUseCase
    {
        /// <summary>
        /// アニメーション再生
        /// 概要：指定されたアニメーションIDの演出を開始する。
        /// </summary>
        Task PlayAsync(Target target, int animationId, int speedMs);

        /// <summary>
        /// アニメーション停止
        /// 概要：指定されたターゲットのアニメーションを停止する。
        /// </summary>
        Task StopAsync(Target target);
    }
}
