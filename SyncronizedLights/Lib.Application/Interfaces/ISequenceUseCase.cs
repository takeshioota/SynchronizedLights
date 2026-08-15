using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib.Domain.Enums;

namespace Lib.Application.Interfaces
{
    /// <summary>
    /// シーケンス実行機能
    /// 概要：事前定義されたシーケンス演出を実行する機能。
    /// </summary>
    public interface ISequenceUseCase
    {
        /// <summary>
        /// シーケンス再生
        /// 概要：指定されたシーケンスIDの演出を開始する。
        /// </summary>
        Task ExecuteAsync(Target target, int sequenceId);

        /// <summary>
        /// シーケンス停止
        /// 概要：指定されたターゲットのシーケンス再生を停止する。
        /// </summary>
        Task StopAsync(Target target);
    }
}
