using System;
using System.Threading;

namespace Lib.Application.Services
{
    /// <summary>
    /// コマンド送信遅延計測クラス
    /// 概要：直近 N 件（既定 1000）の遅延サンプルを循環バッファで保持し、
    ///       P50 / P95 / P99 / Min / Max をスナップショット形式で返す。
    ///       スレッドセーフ。lock で保護、件数・最大値は Interlocked で更新。
    /// </summary>
    public sealed class LatencyTracker
    {
        private readonly object _lock = new();
        private readonly long[] _samples;
        private int _index;
        private int _count;

        private long _totalCount;
        private long _maxMs;

        /// <summary>
        /// LatencyTracker を生成する
        /// </summary>
        /// <param name="windowSize">循環バッファサイズ（既定 1000 件）</param>
        public LatencyTracker(int windowSize = 1000)
        {
            if (windowSize < 1) windowSize = 1;
            _samples = new long[windowSize];
        }

        /// <summary>循環バッファのサイズ</summary>
        public int WindowSize => _samples.Length;

        /// <summary>記録された総件数（バッファを超えた分も数える）</summary>
        public long TotalCount => Interlocked.Read(ref _totalCount);

        /// <summary>これまでに観測した最大遅延（ms）</summary>
        public long MaxEverMs => Interlocked.Read(ref _maxMs);

        /// <summary>
        /// 遅延サンプルを 1 件記録する
        /// </summary>
        /// <param name="ms">遅延値（ミリ秒）。負値は 0 にクランプ</param>
        public void Record(long ms)
        {
            if (ms < 0) ms = 0;

            lock (_lock)
            {
                _samples[_index] = ms;
                _index = (_index + 1) % _samples.Length;
                if (_count < _samples.Length) _count++;
            }

            Interlocked.Increment(ref _totalCount);
            InterlockedMax(ref _maxMs, ms);
        }

        /// <summary>
        /// 現在の統計スナップショットを取得する（読み取り専用）
        /// </summary>
        public LatencyStats Snapshot()
        {
            long[] copy;
            int count;
            lock (_lock)
            {
                count = _count;
                if (count == 0) return LatencyStats.Empty;
                copy = new long[count];
                Array.Copy(_samples, copy, count);
            }

            Array.Sort(copy);
            return new LatencyStats(
                Count: count,
                Min: copy[0],
                Max: copy[count - 1],
                P50: Percentile(copy, 0.50),
                P95: Percentile(copy, 0.95),
                P99: Percentile(copy, 0.99));
        }

        /// <summary>
        /// 循環バッファをクリアする（総件数・最大値は保持しない＝リセット）
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                Array.Clear(_samples, 0, _samples.Length);
                _index = 0;
                _count = 0;
            }
            Interlocked.Exchange(ref _totalCount, 0);
            Interlocked.Exchange(ref _maxMs, 0);
        }

        #region 内部ユーティリティ

        private static long Percentile(long[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        private static void InterlockedMax(ref long target, long value)
        {
            long cur;
            do
            {
                cur = Interlocked.Read(ref target);
                if (value <= cur) return;
            }
            while (Interlocked.CompareExchange(ref target, value, cur) != cur);
        }

        #endregion 内部ユーティリティ
    }

    /// <summary>
    /// 遅延統計スナップショット（不変）
    /// </summary>
    public readonly record struct LatencyStats(
        int Count,
        long Min,
        long Max,
        long P50,
        long P95,
        long P99)
    {
        /// <summary>空の統計（サンプル 0 件）</summary>
        public static LatencyStats Empty { get; } = new LatencyStats(0, 0, 0, 0, 0, 0);

        /// <summary>ログ 1 行表示用</summary>
        public override string ToString()
            => $"n={Count}, P50={P50}ms, P95={P95}ms, P99={P99}ms, Min={Min}ms, Max={Max}ms";
    }
}