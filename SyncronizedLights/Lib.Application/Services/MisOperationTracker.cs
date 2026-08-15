using System;
using System.Threading;

namespace Lib.Application.Services
{
    /// <summary>
    /// 操作ミス計測サービス
    /// 概要：オペレータの誤操作を種類別にカウントし、事後解析のための
    ///       定量データを提供する。どのプロジェクトからも
    ///       MisOperationTracker.Instance で同一インスタンスにアクセスできる。
    ///
    /// 「操作ミス」の分類：
    ///   - ValidationFailure : 範囲外入力のまま実行試行（Speed/Channel/Power/命令名）
    ///   - SendFailure       : 送信時の例外（接続エラー、タイムアウト等）
    ///   - UndefinedAction   : 未定義シーケンス / 未登録アニメの実行試行
    ///   - InvalidCommandName: 32 文字超過の命令名で Execute 試行
    /// </summary>
    public sealed class MisOperationTracker
    {
        #region シングルトン

        /// <summary>
        /// グローバル唯一のトラッカーインスタンス
        /// 概要：Lib.Ui.Screens など上位レイヤー（SynchronizedLights.UI に
        /// 依存できないプロジェクト）からも直接呼び出せるようにする。
        /// </summary>
        public static MisOperationTracker Instance { get; } = new MisOperationTracker();

        #endregion シングルトン

        #region フィールド

        private long _validationFailureCount;
        private long _sendFailureCount;
        private long _undefinedActionCount;
        private long _invalidCommandNameCount;

        #endregion フィールド

        #region プロパティ

        /// <summary>範囲外入力のまま実行した回数（累積）</summary>
        public long ValidationFailureCount => Interlocked.Read(ref _validationFailureCount);

        /// <summary>送信時例外発生回数（累積）</summary>
        public long SendFailureCount => Interlocked.Read(ref _sendFailureCount);

        /// <summary>未定義アクション実行回数（累積）</summary>
        public long UndefinedActionCount => Interlocked.Read(ref _undefinedActionCount);

        /// <summary>命令名不正の実行試行回数（累積）</summary>
        public long InvalidCommandNameCount => Interlocked.Read(ref _invalidCommandNameCount);

        /// <summary>
        /// 操作ミス合計（KPI バー表示用）
        /// </summary>
        public long TotalMisOperationCount
            => ValidationFailureCount
             + SendFailureCount
             + UndefinedActionCount
             + InvalidCommandNameCount;

        #endregion プロパティ

        #region カウントメソッド

        /// <summary>範囲外入力の実行試行をカウント</summary>
        public void RecordValidationFailure()
            => Interlocked.Increment(ref _validationFailureCount);

        /// <summary>送信失敗をカウント</summary>
        public void RecordSendFailure()
            => Interlocked.Increment(ref _sendFailureCount);

        /// <summary>未定義アクションの実行試行をカウント</summary>
        public void RecordUndefinedAction()
            => Interlocked.Increment(ref _undefinedActionCount);

        /// <summary>命令名不正の実行試行をカウント</summary>
        public void RecordInvalidCommandName()
            => Interlocked.Increment(ref _invalidCommandNameCount);

        #endregion カウントメソッド

        #region ユーティリティ

        /// <summary>
        /// 全カウンタをリセット（テスト用途。本番では呼ばない）
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _validationFailureCount, 0);
            Interlocked.Exchange(ref _sendFailureCount, 0);
            Interlocked.Exchange(ref _undefinedActionCount, 0);
            Interlocked.Exchange(ref _invalidCommandNameCount, 0);
        }

        /// <summary>
        /// 現在の集計をログ出力用の文字列で返す
        /// </summary>
        public override string ToString()
            => $"total={TotalMisOperationCount}, "
             + $"validation={ValidationFailureCount}, "
             + $"send={SendFailureCount}, "
             + $"undefined={UndefinedActionCount}, "
             + $"invalidName={InvalidCommandNameCount}";

        #endregion ユーティリティ
    }
}