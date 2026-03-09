using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.Application.Interfaces;
using Lib.Domain.Enums;
using Lib.Domain.ValueObjects;
using Lib.Protocol.Interfaces;
using Lib.Transport.Interfaces;
using Lib.Transport.Models;

namespace Lib.Application.UseCases
{
    /// <summary>
    /// Preset制御機能
    /// 概要：シンクロライトの基本操作（色変更、点灯、消灯など）を実行するUseCase。
    /// UIからの操作要求を受け取り、Protocolで制御コマンドを生成し、
    /// Transportを通じてシンクロライトへ送信する。
    /// </summary>
    public class PresetUseCase : IPresetUseCase
    {
        /// <summary>
        /// コマンド生成処理
        /// 概要：UI操作内容をシンクロライト制御用の通信コマンド（Packet32）へ変換するためのビルダ。
        /// </summary>
        private readonly ICommandBuilder _commandBuilder;

        /// <summary>
        /// 送信処理
        /// 概要：生成された通信パケットを送信キューへ登録し、実際の送信処理を管理するTransport。
        /// </summary>
        private readonly ITransport _transport;

        #region コンストラクタ
        /// <summary>
        /// PresetUseCaseのインスタンスを生成する。
        /// 概要：コマンド生成（Protocol）と送信処理（Transport）の依存を受け取り、
        /// Preset操作の実行に利用する。
        /// </summary>
        public PresetUseCase(ICommandBuilder commandBuilder, ITransport transport)
        {
            _commandBuilder = commandBuilder;
            _transport = transport;
        }
        #endregion コンストラクタ

        #region メソッド
        /// <summary>
        /// 指定したターゲットのライト色を変更する。
        /// 概要：指定された色情報をもとにProtocolで制御コマンドを生成し、
        /// Transportへ送信キューとして登録する。
        /// </summary>
        public async Task SetColorAsync(Target target, Rgb color)
        {
            var packet = _commandBuilder.BuildSetColor(target, color);
            await _transport.EnqueueAsync(packet, new SendOptions());
        }

        /// <summary>
        /// 指定ターゲットのライトを点灯させる。
        /// </summary>
        public async Task TurnOnAsync(Target target, Rgb color)
        {
            var packet = _commandBuilder.BuildTurnOn(target, color);
            await _transport.EnqueueAsync(packet, new SendOptions());
        }

        /// <summary>
        /// 指定ターゲットのライトを消灯する。
        /// </summary>
        public async Task TurnOffAsync(Target target)
        {
            var packet = _commandBuilder.BuildTurnOff(target);
            await _transport.EnqueueAsync(packet, new SendOptions());
        }
        #endregion メソッド
    }
}
