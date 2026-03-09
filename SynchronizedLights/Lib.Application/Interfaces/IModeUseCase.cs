using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
namespace Lib.Application.Interfaces
{
    /// <summary>
    /// Mode演出制御機能
    /// 概要：点滅（Flash）、フェード（Fade）、ブレス（Breathe）などの
    /// 演出モードをシンクロライトへ適用する機能。
    /// UI操作から演出パラメータを受け取り、Protocolを通じて制御コマンドを生成する。
    /// </summary>
    public interface IModeUseCase
    {
        /// <summary>
        /// 点滅演出実行
        /// 概要：指定されたターゲットを点滅させる。
        /// </summary>
        Task FlashAsync(Target target, Rgb color, int speedMs);

        /// <summary>
        /// フェードイン演出実行
        /// 概要：指定されたターゲットを徐々に点灯させる。
        /// </summary>
        Task FadeInAsync(Target target, Rgb color, int speedMs);

        /// <summary>
        /// フェードアウト演出実行
        /// 概要：指定されたターゲットを徐々に消灯させる。
        /// </summary>
        Task FadeOutAsync(Target target, Rgb color, int speedMs);

        /// <summary>
        /// ブレス演出実行
        /// 概要：指定されたターゲットを明暗を繰り返す演出で制御する。
        /// </summary>
        Task BreatheAsync(Target target, Rgb color, int speedMs);
    }
}
