using System;
using System.Collections.Generic;

namespace NeeView.PageFrames
{
    /// <summary>
    /// アダプティブ表示のフレーム分割計算 (純粋関数)
    /// </summary>
    /// <remarks>
    /// 「行」としてページを詰めていく。
    /// 測定値は「ページをキャンバス高さいっぱいに拡大したときの幅」を高さ1で正規化した値なので、
    /// 判定はページのアスペクト比のみで行う(実ピクセル値には依存しない)。
    /// 横方向 : 測定値 = w/h、予算 = W/H
    /// 縦方向 : 測定値 = h/w、予算 = H/W
    /// 予算を超えるページは取り込まない。これにより、フレームは常にキャンバスの高さ(縦方向は幅)を
    /// 埋める大きさになり、同時に表示できる最大ページ数になる。
    /// </remarks>
    public static class AdaptivePagePacker
    {
        /// <summary>
        /// フレームに含めるページ数を求める
        /// </summary>
        /// <param name="measure">ページインデックスから測定値を得る関数</param>
        /// <param name="pageCount">ブックのページ数</param>
        /// <param name="canvasWidth">キャンバス幅</param>
        /// <param name="canvasHeight">キャンバス高</param>
        /// <param name="span">ページ間の距離。0以下は無視する</param>
        /// <param name="orientation">並べる方向</param>
        /// <param name="start">フレーム先頭のページインデックス</param>
        /// <param name="maxCount">フレームの最大ページ数</param>
        /// <returns>ページ数 (1以上)</returns>
        public static int GetFramePageCount(Func<int, double> measure, int pageCount, double canvasWidth, double canvasHeight, double span, PageFrameOrientation orientation, int start, int maxCount)
        {
            if (measure is null) throw new ArgumentNullException(nameof(measure));
            if (start < 0 || start >= pageCount || maxCount < 1)
            {
                return 1;
            }

            span = Math.Max(0.0, span);
            var firstBudget = GetBudget(canvasWidth, canvasHeight, orientation, span, 1);
            if (!IsValidBudget(firstBudget))
            {
                return 1;
            }

            var count = 0;
            var sum = 0.0;

            for (var i = start; i < pageCount && count < maxCount; i++)
            {
                var value = NormalizeMeasure(measure(i));

                if (count > 0)
                {
                    // 追加すると予算を超える場合は取り込まない
                    var budget = GetBudget(canvasWidth, canvasHeight, orientation, span, count + 1);
                    if (!IsValidBudget(budget) || sum + value > budget)
                    {
                        break;
                    }
                }

                sum += value;
                count++;
            }

            return Math.Max(count, 1);
        }

        /// <summary>
        /// フレームに含めるページ数を求める (配列版。テスト用)
        /// </summary>
        public static int GetFramePageCount(IReadOnlyList<double> measures, double canvasWidth, double canvasHeight, double span, PageFrameOrientation orientation, int start, int maxCount)
        {
            if (measures is null) throw new ArgumentNullException(nameof(measures));
            return GetFramePageCount(i => measures[i], measures.Count, canvasWidth, canvasHeight, span, orientation, start, maxCount);
        }


        /// <summary>
        /// ページサイズから測定値を求める
        /// </summary>
        public static double GetMeasure(double width, double height, PageFrameOrientation orientation)
        {
            if (width <= 0.0 || height <= 0.0 || double.IsNaN(width) || double.IsNaN(height))
            {
                return 1.0;
            }

            return orientation == PageFrameOrientation.Vertical ? height / width : width / height;
        }

        /// <summary>
        /// 画面サイズとページ数から予算を求める
        /// </summary>
        /// <param name="pageCount">フレームに含めるページ数</param>
        public static double GetBudget(double canvasWidth, double canvasHeight, PageFrameOrientation orientation, double span, int pageCount)
        {
            if (canvasWidth <= 0.0 || canvasHeight <= 0.0)
            {
                return 0.0;
            }

            var gap = Math.Max(0, pageCount - 1) * Math.Max(0.0, span);
            return orientation == PageFrameOrientation.Vertical
                ? (canvasHeight - gap) / canvasWidth
                : (canvasWidth - gap) / canvasHeight;
        }


        private static bool IsValidBudget(double budget)
        {
            return !double.IsNaN(budget) && !double.IsInfinity(budget) && budget > 0.0;
        }

        private static double NormalizeMeasure(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 ? 1.0 : value;
        }
    }
}
