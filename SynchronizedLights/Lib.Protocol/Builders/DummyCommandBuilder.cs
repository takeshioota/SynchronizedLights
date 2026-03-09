using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Lib.Protocol.Interfaces;
using Lib.Protocol.Packets;

namespace Lib.Protocol.Builders
{
    // <summary>
    /// ダミーコマンド生成クラス
    /// 概要：ICommandBuilderのテスト実装。
    /// 実際の通信仕様が確定する前の段階で、
    /// 仮の32バイトパケットを生成するために使用する。
    /// </summary>
    public class DummyCommandBuilder : ICommandBuilder
    {
        #region メソッド
        /// <summary>
        /// 色変更コマンドを生成する（ダミー実装）
        /// 概要：RGB色情報をパケットに設定し、
        /// 仮の制御コマンドとしてPacket32を生成する。
        /// </summary>
        public Packet32 BuildSetColor(Target target, Rgb color)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA2;
            bytes[1] = (byte)target;
            bytes[2] = color.R;
            bytes[3] = color.G;
            bytes[4] = color.B;
            return new Packet32(bytes);
        }

        /// <summary>
        /// 点灯コマンド生成（ダミー実装）
        /// 概要：指定されたターゲットを指定色で点灯させるための
        /// 仮の32バイト通信パケットを生成する。
        /// コマンド種別(A0)と対象、RGB色情報をパケットへ設定する。
        /// </summary>
        public Packet32 BuildTurnOn(Target target, Rgb color)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA0;
            bytes[1] = (byte)target;
            bytes[2] = color.R;
            bytes[3] = color.G;
            bytes[4] = color.B;
            bytes[5] = 0x01;
            return new Packet32(bytes);
        }

        /// <summary>
        /// 点灯コマンド生成（ダミー実装）
        /// 概要：指定されたターゲットを指定色で点灯させるための
        /// 仮の32バイト通信パケットを生成する。
        /// コマンド種別(A0)と対象、RGB色情報をパケットへ設定する。
        /// </summary>
        public Packet32 BuildTurnOff(Target target)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA0;
            bytes[1] = (byte)target;
            bytes[5] = 0x00;
            return new Packet32(bytes);
        }

        /// <summary>
        /// 点滅演出コマンド生成（ダミー実装）
        /// 概要：指定されたターゲットに対して、指定色と速度で
        /// 点滅演出を行う仮の32バイト通信パケットを生成する。
        /// 速度はミリ秒値を簡易的にスケーリングしてパケットに格納する。
        /// </summary>
        public Packet32 BuildFlash(Target target, Rgb color, int speedMs)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA1;
            bytes[1] = (byte)target;
            bytes[2] = color.R;
            bytes[3] = color.G;
            bytes[4] = color.B;
            bytes[6] = (byte)Math.Min(speedMs / 10, 255);
            return new Packet32(bytes);
        }

        /// <summary>
        /// フェードイン演出コマンド生成（ダミー実装）
        /// 概要：指定されたターゲットに対して、指定色で徐々に点灯する
        /// フェードイン演出の仮の32バイト通信パケットを生成する。
        /// </summary>
        public Packet32 BuildFadeIn(Target target, Rgb color, int speedMs)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA3;
            bytes[1] = (byte)target;
            bytes[2] = color.R;
            bytes[3] = color.G;
            bytes[4] = color.B;
            bytes[6] = (byte)Math.Min(speedMs / 10, 255);
            return new Packet32(bytes);
        }

        /// <summary>
        /// フェードアウト演出コマンド生成（ダミー実装）
        /// 概要：指定されたターゲットに対して、指定色から徐々に消灯する
        /// フェードアウト演出の仮の32バイト通信パケットを生成する。
        /// </summary>
        public Packet32 BuildFadeOut(Target target, Rgb color, int speedMs)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA4;
            bytes[1] = (byte)target;
            bytes[2] = color.R;
            bytes[3] = color.G;
            bytes[4] = color.B;
            bytes[6] = (byte)Math.Min(speedMs / 10, 255);
            return new Packet32(bytes);
        }

        /// <summary>
        /// ブレス演出コマンド生成（ダミー実装）
        /// 概要：指定されたターゲットに対して、指定色で明暗を繰り返す
        /// ブレス演出の仮の32バイト通信パケットを生成する。
        /// </summary>
        public Packet32 BuildBreathe(Target target, Rgb color, int speedMs)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA5;
            bytes[1] = (byte)target;
            bytes[2] = color.R;
            bytes[3] = color.G;
            bytes[4] = color.B;
            bytes[6] = (byte)Math.Min(speedMs / 10, 255);
            return new Packet32(bytes);
        }
        /// <summary>
        /// シーケンス再生コマンド生成（ダミー実装）
        /// 概要：指定されたシーケンスIDを実行するための
        /// 仮の32バイト通信パケットを生成する。
        /// </summary>

        public Packet32 BuildSequence(Target target, int sequenceId)
        {
            var bytes = new byte[Packet32.Length];
            bytes[0] = 0xA6;
            bytes[1] = (byte)target;
            bytes[2] = (byte)sequenceId;
            return new Packet32(bytes);
        }
        #endregion メソッド
    }
}
