namespace Lib.Ui.Screens.ViewModels
{
    /// <summary>
    /// Rainbow 入力の形式検証（純ロジック＝実機不要）。時間系は 0〜5000ms（下限補正なし・上限超過は不可）、
    /// 色数は 2〜7、点灯比率は 0〜9。範囲外は丸めずエラーとする（設計書 第3/5章・v2.12.0 入力制限緩和）。
    ///
    /// 追加開発 No.41(B-20261120-01): 以前は DlgRainbowRowEditor.ValidateRow（行ダイアログ）だけが
    /// この検証を持ち、コマンドパネルの「この行に適用」/「Rainbow Start」経路は無検証で値を適用・送信して
    /// いた。検証ロジックをここへ集約し、ダイアログ・パネル両経路から共有して範囲外入力を拒否する。
    /// </summary>
    public static class RainbowInputValidator
    {
        /// <summary>時間系(速度/点滅周期/FI/FO)の上限 [ms]。</summary>
        public const int TimeMaxMs = 5000;

        /// <summary>
        /// Rainbow パラメータを検証する。範囲外なら false を返し、error に理由を格納する。
        /// </summary>
        /// <param name="colorCount">パレット色数</param>
        /// <param name="cycleMs">速度(色切替間隔) [ms]</param>
        /// <param name="blinkPeriodMs">点滅周期 [ms]</param>
        /// <param name="fadeInMs">フェードイン時間(FI) [ms]</param>
        /// <param name="fadeOutMs">フェードアウト時間(FO) [ms]</param>
        /// <param name="dutyRatio">点灯比率 (0〜9)</param>
        /// <param name="error">範囲外時のエラーメッセージ</param>
        public static bool Validate(
            int colorCount,
            int cycleMs,
            int blinkPeriodMs,
            int fadeInMs,
            int fadeOutMs,
            int dutyRatio,
            out string error)
        {
            if (colorCount < 2 || colorCount > 7)
            {
                error = $"レインボーカラーは 2〜7 色で設定してください（現在 {colorCount} 色）。";
                return false;
            }

            var times = new (string Name, int Value)[]
            {
                ("速度(色切替間隔)", cycleMs),
                ("点滅周期", blinkPeriodMs),
                ("フェードイン時間(FI)", fadeInMs),
                ("フェードアウト時間(FO)", fadeOutMs),
            };
            foreach (var t in times)
            {
                if (t.Value < 0 || t.Value > TimeMaxMs)
                {
                    error = $"{t.Name} は 0〜{TimeMaxMs}ms の範囲で入力してください（現在 {t.Value}ms）。\n"
                          + $"※ 仕様下限未満の値は端末側で補正されます。{TimeMaxMs}ms を超える値は設定できません。";
                    return false;
                }
            }

            if (dutyRatio < 0 || dutyRatio > 9)
            {
                error = $"点灯比率は 0〜9 で入力してください（現在 {dutyRatio}）。";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
