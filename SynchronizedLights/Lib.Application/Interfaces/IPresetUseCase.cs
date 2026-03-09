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
    /// Preset操作機能
    /// 概要：シンクロライトの基本状態（色変更、ON/OFFなど）を即時制御する機能。
    /// UIからの操作要求を受け取り、Protocolを用いてコマンドを生成し、Transportへ送信する。
    /// </summary>
    public interface IPresetUseCase
    {
        /// <summary>
        /// 色変更処理
        /// 概要：指定されたターゲットのライト色を変更する。
        /// </summary>
        Task SetColorAsync(Target target, Rgb color);

        /// <summary>
        /// 点灯処理
        /// 概要：指定されたターゲットを指定色で点灯させる。
        /// </summary>
        Task TurnOnAsync(Target target, Rgb color);

        /// <summary>
        /// 消灯処理
        /// 概要：指定されたターゲットのライトを消灯する。
        /// </summary>
        Task TurnOffAsync(Target target);
    }
}
