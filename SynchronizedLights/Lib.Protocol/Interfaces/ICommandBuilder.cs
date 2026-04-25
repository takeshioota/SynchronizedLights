using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Lib.Protocol.Packets;

namespace Lib.Protocol.Interfaces
{
    /// <summary>
    /// 制御コマンド生成インターフェース
    /// 概要：UIやUseCaseから渡された操作意図を、
    /// シンクロライト制御用の32バイト通信パケットへ変換するためのインターフェース。
    /// Protocol層でコマンド生成ルールを定義する。
    /// </summary>
    public interface ICommandBuilder
    {
        #region メソッド
        /// <summary>
        /// 色変更コマンドを生成する
        /// 概要：指定されたターゲットとRGB色情報をもとに、
        /// シンクロライトの色変更を行う通信パケットを生成する。
        /// </summary>
        Packet32 BuildSetColor(Target target, Rgb color);

        /// <summary>
        /// 点灯コマンドを生成する
        /// 概要：指定されたターゲットに対して、
        /// 指定色でライトを点灯させる通信パケットを生成する。
        /// </summary>
        Packet32 BuildTurnOn(Target target, Rgb color);

        /// <summary>
        /// 消灯コマンドを生成する
        /// 概要：指定されたターゲットに対して、
        /// ライトを消灯させる通信パケットを生成する。
        /// </summary>
        Packet32 BuildTurnOff(Target target);

        /// <summary>
        /// 点滅演出コマンドを生成する
        /// 概要：指定されたターゲットに対して、
        /// 指定色と速度で点滅演出を行う通信パケットを生成する。
        /// </summary>
        Packet32 BuildFlash(Target target, Rgb color, int speedMs);

        /// <summary>
        /// フェードイン演出コマンドを生成する
        /// 概要：指定されたターゲットに対して、
        /// 指定時間で徐々に点灯するフェードイン演出の通信パケットを生成する。
        /// </summary>
        Packet32 BuildFadeIn(Target target, Rgb color, int speedMs);

        /// <summary>
        /// フェードアウト演出コマンドを生成する
        /// 概要：指定されたターゲットに対して、
        /// 指定時間で徐々に消灯するフェードアウト演出の通信パケットを生成する。
        /// </summary>
        Packet32 BuildFadeOut(Target target, Rgb color, int speedMs);

        /// <summary>
        /// ブレス演出コマンドを生成する
        /// 概要：指定されたターゲットに対して、
        /// 指定色と速度で明滅を繰り返すブレス演出の通信パケットを生成する。
        /// </summary>
        Packet32 BuildBreathe(Target target, Rgb color, int speedMs);

        /// <summary>
        /// シーケンス再生コマンドを生成する
        /// 概要：指定されたシーケンスIDをもとに、
        /// 事前定義された演出シーケンスを再生する通信パケットを生成する。
        /// </summary>
        Packet32 BuildSequence(Target target, int sequenceId);
        #endregion メソッド
    }
}