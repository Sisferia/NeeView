using System;
using System.Diagnostics;

namespace NeeView
{
    // ページ表示モード
    public enum PageMode
    {
        [AliasName]
        SinglePage,

        [AliasName]
        WidePage,

        [AliasName]
        Adaptive,
    }


    public static class PageModeExtension
    {
        public static PageMode GetToggle(this PageMode mode, int direction, bool isLoop)
        {
            Debug.Assert(direction == -1 || direction == +1);
            var length = Enum.GetNames(typeof(PageMode)).Length;
            if (isLoop)
            {
                return (PageMode)(((int)mode + length + direction) % length);
            }
            else
            {
                return (PageMode)(Math.Clamp((int)mode + direction, 0, length - 1));
            }
        }

        public static PageMode Validate(this PageMode mode)
        {
            if (mode < PageMode.SinglePage) return PageMode.SinglePage;
            if (mode > PageMode.Adaptive) return PageMode.Adaptive;
            return mode;
        }

        /// <summary>
        /// 単ページ表示
        /// </summary>
        public static bool IsSinglePage(this PageMode mode)
        {
            return mode == PageMode.SinglePage;
        }

        /// <summary>
        /// 見開き表示
        /// </summary>
        public static bool IsWidePage(this PageMode mode)
        {
            return mode == PageMode.WidePage;
        }

        /// <summary>
        /// アダプティブ表示
        /// </summary>
        public static bool IsAdaptive(this PageMode mode)
        {
            return mode == PageMode.Adaptive;
        }

        /// <summary>
        /// フレーム内に複数ページを並べるモード？
        /// </summary>
        public static bool IsMultiPageFrame(this PageMode mode)
        {
            return mode != PageMode.SinglePage;
        }
    }
}
