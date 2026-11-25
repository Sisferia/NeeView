using System;
using System.Windows;

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
    }
}
