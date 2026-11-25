using System;
using System.Collections.Generic;
using System.Windows;
using NeeView.PageFrames;

namespace NeeView
{
    /// <summary>
    /// ページ固定の参照表示位置
    /// </summary>
    public enum PageLockSide
    {
        Left,
        Right,
    }

    /// <summary>
    /// ページ固定（参照表示）の計算
    /// </summary>
    public static class PageLockTools
    {
        /// <summary>
        /// 参照表示の幅の最大割合
        /// </summary>
        public const double MaxPaneWidthRate = 0.5;

        /// <summary>
        /// 同時に固定できるページ数の上限
        /// </summary>
        public const int MaxLockCount = 8;


        /// <summary>
        /// 回転後のピクセルサイズを求める
        /// </summary>
        /// <remarks>
        /// 90度単位の回転のみを対象とする。
        /// </remarks>
        public static Size GetRotatedPixelSize(Size pixelSize, double angle)
        {
            if (pixelSize.IsEmpty || pixelSize.Width <= 0.0 || pixelSize.Height <= 0.0)
            {
                return Size.Empty;
            }

            return IsQuarterTurn(angle) ? new Size(pixelSize.Height, pixelSize.Width) : pixelSize;
        }

        /// <summary>
        /// 参照表示の幅を求める
        /// </summary>
        /// <param name="viewSize">参照表示を含む表示領域のサイズ</param>
        /// <param name="imageSize">固定するページのサイズ（回転適用済）</param>
        /// <param name="maxRate">幅の最大割合</param>
        public static double GetPaneWidth(Size viewSize, Size imageSize, double maxRate = MaxPaneWidthRate)
        {
            if (viewSize.IsEmpty || viewSize.Width <= 0.0 || viewSize.Height <= 0.0) return 0.0;
            if (imageSize.IsEmpty || imageSize.Width <= 0.0 || imageSize.Height <= 0.0) return 0.0;

            var aspect = imageSize.Width / imageSize.Height;
            var width = viewSize.Height * aspect;
            var maxWidth = viewSize.Width * maxRate;

            return Math.Max(0.0, Math.Min(width, maxWidth));
        }

        /// <summary>
        /// ページの表示位置から参照表示の側を求める
        /// </summary>
        /// <param name="pageRect">ページの表示矩形</param>
        /// <param name="viewSize">表示領域のサイズ</param>
        public static PageLockSide GetSide(Rect pageRect, Size viewSize)
        {
            if (viewSize.IsEmpty || viewSize.Width <= 0.0) return PageLockSide.Right;

            var center = pageRect.X + pageRect.Width * 0.5;
            return center < viewSize.Width * 0.5 ? PageLockSide.Left : PageLockSide.Right;
        }

        /// <summary>
        /// 90度・270度回転か？
        /// </summary>
        public static bool IsQuarterTurn(double angle)
        {
            var normalized = Math.Abs(angle) % 360.0;
            return Math.Abs(normalized - 90.0) < 1.0 || Math.Abs(normalized - 270.0) < 1.0;
        }


        #region 行頭参照区（多ページ固定）

        /// <summary>
        /// 参照格の長さを求める
        /// </summary>
        /// <param name="viewSize">表示領域のサイズ</param>
        /// <param name="imageSize">固定するページのサイズ（回転適用済）</param>
        /// <param name="orientation">並べる方向。横並びなら幅、縦並びなら高さを返す</param>
        public static double GetCellLength(Size viewSize, Size imageSize, PageFrameOrientation orientation)
        {
            if (viewSize.IsEmpty || viewSize.Width <= 0.0 || viewSize.Height <= 0.0) return 0.0;
            if (imageSize.IsEmpty || imageSize.Width <= 0.0 || imageSize.Height <= 0.0) return 0.0;

            var canvasLength = GetCanvasLength(viewSize, orientation);

            if (orientation == PageFrameOrientation.Vertical)
            {
                // 縦並び: 幅いっぱいに合わせた高さ
                var length = viewSize.Width * (imageSize.Height / imageSize.Width);
                return Math.Max(0.0, Math.Min(length, canvasLength));
            }
            else
            {
                // 横並び: 高さいっぱいに合わせた幅
                var length = viewSize.Height * (imageSize.Width / imageSize.Height);
                return Math.Max(0.0, Math.Min(length, canvasLength));
            }
        }

        /// <summary>
        /// 主ビューに最低限必要な長さ(未固定ページ1ページ分)
        /// </summary>
        public static double GetLiveMinLength(Size viewSize, Size imageSize, PageFrameOrientation orientation)
        {
            return GetCellLength(viewSize, imageSize, orientation);
        }

        /// <summary>
        /// 参照区の総長を求める
        /// </summary>
        public static double GetStripLength(IReadOnlyList<double> cellLengths)
        {
            if (cellLengths is null) return 0.0;

            var length = 0.0;
            foreach (var cellLength in cellLengths)
            {
                if (double.IsNaN(cellLength) || double.IsInfinity(cellLength) || cellLength <= 0.0) continue;
                length += cellLength;
            }
            return length;
        }

        /// <summary>
        /// 主ビューの自然長を求める
        /// </summary>
        /// <remarks>
        /// 参照区を除いた領域と最低1ページ分の長さの大きい方を返す。
        /// これにより表示行の長さが常に「参照区 + 最低1ページ」以上になる。
        /// </remarks>
        public static double GetLiveLength(Size viewSize, double stripLength, double minLiveLength, PageFrameOrientation orientation)
        {
            var minimum = Math.Max(0.0, minLiveLength);
            if (viewSize.IsEmpty) return minimum;

            var canvasLength = GetCanvasLength(viewSize, orientation);
            if (canvasLength <= 0.0) return minimum;

            var available = canvasLength - Math.Max(0.0, stripLength);
            return Math.Max(available, minimum);
        }

        /// <summary>
        /// 表示行の自然長(参照区 + 主ビュー)
        /// </summary>
        public static double GetRowLength(double stripLength, double liveLength)
        {
            return Math.Max(0.0, stripLength) + Math.Max(0.0, liveLength);
        }

        /// <summary>
        /// 表示行の統一スケールを求める
        /// </summary>
        /// <returns>1.0以下。表示行が収まらない場合のみ1.0未満になる</returns>
        public static double GetRowScale(Size viewSize, double rowLength, PageFrameOrientation orientation)
        {
            if (viewSize.IsEmpty || rowLength <= 0.0) return 1.0;

            var canvasLength = GetCanvasLength(viewSize, orientation);
            if (canvasLength <= 0.0) return 1.0;

            return Math.Min(1.0, canvasLength / rowLength);
        }

        /// <summary>
        /// 参照区のぶんだけ主ビューをずらす余白
        /// </summary>
        public static Thickness GetViewMargin(double stripLength, PageFrameOrientation orientation)
        {
            var length = Math.Max(0.0, stripLength);
            return orientation == PageFrameOrientation.Vertical
                ? new Thickness(0.0, length, 0.0, 0.0)
                : new Thickness(length, 0.0, 0.0, 0.0);
        }

        /// <summary>
        /// ページ固定が可能か? (R1: 表示中に最低1ページの未固定ページを残す)
        /// </summary>
        /// <param name="unlockedPageCountInCurrentView">現在の画面で未固定のページ数</param>
        public static bool CanLockPage(int unlockedPageCountInCurrentView)
        {
            return unlockedPageCountInCurrentView >= 2;
        }

        /// <summary>
        /// 表示ページ数の下限を保証する (R2: 最低1ページは表示する)
        /// </summary>
        public static int EnsureVisiblePageCount(int computedCount)
        {
            return Math.Max(1, computedCount);
        }

        /// <summary>
        /// 指定方向で次の未固定ページのインデックスを求める
        /// </summary>
        /// <param name="startIndex">検索開始インデックス</param>
        /// <param name="direction">検索方向 (+1: 次, -1: 前)</param>
        /// <param name="pageCount">ページ数</param>
        /// <param name="isLoop">ループ表示か</param>
        /// <param name="isPageLocked">ページが固定されているか判定する関数</param>
        /// <returns>未固定ページのインデックス。見つからない場合は -1</returns>
        public static int FindNextUnlockedPageIndex(int startIndex, int direction, int pageCount, bool isLoop, Func<int, bool> isPageLocked)
        {
            if (pageCount <= 0 || isPageLocked is null) return -1;

            var step = direction >= 0 ? 1 : -1;
            var index = startIndex;

            for (var i = 0; i < pageCount + 1; i++)
            {
                if (isLoop)
                {
                    index = NormalizeIndex(index, pageCount);
                }
                else if (index < 0 || index >= pageCount)
                {
                    return -1;
                }

                if (!isPageLocked(index))
                {
                    return index;
                }

                index += step;
            }

            return -1;
        }

        private static int NormalizeIndex(int index, int pageCount)
        {
            return ((index % pageCount) + pageCount) % pageCount;
        }

        private static double GetCanvasLength(Size viewSize, PageFrameOrientation orientation)
        {
            return orientation == PageFrameOrientation.Vertical ? viewSize.Height : viewSize.Width;
        }

        #endregion 行頭参照区（多ページ固定）
    }
}
