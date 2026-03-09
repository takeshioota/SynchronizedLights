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
    /// Preset制御機能インターフェース
    /// 概要：シンクロライトの基本操作（色変更、点灯、消灯、点滅、フェードなど）を
    /// 実行するUseCaseのインターフェースを定義する。
    /// UI層は本インターフェースを通じてPreset関連の処理を呼び出す。
    /// </summary>
    public interface IPresetUseCase
    {
        #region メソッド
        /// <summary>
        /// 指定したターゲットのライト色を変更する。
        /// 概要：指定された色情報をもとに色変更処理を実行する。
        /// </summary>
        Task SetColorAsync(Target target, Rgb color);

        /// <summary>
        /// 指定ターゲットのライトを点灯させる。
        /// 概要：指定されたターゲットと色をもとに点灯処理を実行する。
        /// </summary>
        Task TurnOnAsync(Target target, Rgb color);

        /// <summary>
        /// 指定ターゲットのライトを消灯する。
        /// 概要：指定されたターゲットをもとに消灯処理を実行する。
        /// </summary>
        Task TurnOffAsync(Target target);

        /// <summary>
        /// 指定ターゲットに点滅演出を実行する。
        /// 概要：指定されたターゲット、色、速度をもとに点滅演出を実行する。
        /// </summary>
        Task ExecuteFlashAsync(Target target, Rgb color, int speedMs);

        /// <summary>
        /// 指定ターゲットにフェードイン演出を実行する。
        /// 概要：指定されたターゲット、色、速度をもとにフェードイン演出を実行する。
        /// </summary>
        Task ExecuteFadeInAsync(Target target, Rgb color, int speedMs);

        /// <summary>
        /// 指定ターゲットにフェードアウト演出を実行する。
        /// 概要：指定されたターゲット、色、速度をもとにフェードアウト演出を実行する。
        /// </summary>
        Task ExecuteFadeOutAsync(Target target, Rgb color, int speedMs);
        #endregion メソッド
    }
}