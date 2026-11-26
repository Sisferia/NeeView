using NeeView.PageFrames;
using System.Windows;

namespace NeeView.UnitTest
{
    /// <summary>
    /// ページ固定（行頭参照区・多ページ固定）の計算テスト
    /// </summary>
    public class PageLockLayoutTest
    {
        [Fact]
        public void GetCellLength_PortraitHorizontal()
        {
            // 横並び: 高さいっぱいに合わせた幅
            var length = PageLockTools.GetCellLength(new Size(1600, 1000), new Size(800, 1200), PageFrameOrientation.Horizontal);

            Assert.Equal(1000.0 * 800.0 / 1200.0, length, 3);
        }

        [Fact]
        public void GetCellLength_LandscapeCappedByCanvas()
        {
            // 横長すぎるページはキャンバス幅で制限する
            var length = PageLockTools.GetCellLength(new Size(1000, 800), new Size(3000, 1000), PageFrameOrientation.Horizontal);

            Assert.Equal(1000.0, length, 3);
        }

        [Fact]
        public void GetCellLength_VerticalCappedByCanvas()
        {
            // 縦並び: 幅いっぱいに合わせた高さ
            var length = PageLockTools.GetCellLength(new Size(1600, 1000), new Size(800, 1200), PageFrameOrientation.Vertical);

            Assert.Equal(1000.0, length, 3);
        }

        [Fact]
        public void GetCellLength_Empty()
        {
            Assert.Equal(0.0, PageLockTools.GetCellLength(Size.Empty, new Size(800, 1200), PageFrameOrientation.Horizontal), 3);
            Assert.Equal(0.0, PageLockTools.GetCellLength(new Size(1600, 1000), Size.Empty, PageFrameOrientation.Horizontal), 3);
        }

        [Fact]
        public void GetStripLength_SumsAndSkipsInvalid()
        {
            var length = PageLockTools.GetStripLength(new[] { 100.0, double.NaN, -5.0, 200.0 });

            Assert.Equal(300.0, length, 3);
        }

        [Fact]
        public void GetStripLength_Null_Empty()
        {
            Assert.Equal(0.0, PageLockTools.GetStripLength(null!), 3);
        }

        [Fact]
        public void GetLiveLength_EnoughSpace()
        {
            // 参照区を除いた領域が最低1ページ分より広い
            var liveLength = PageLockTools.GetLiveLength(new Size(1400, 800), 467.0, 467.0, PageFrameOrientation.Horizontal);

            Assert.Equal(933.0, liveLength, 3);
        }

        [Fact]
        public void GetLiveLength_NotEnoughSpace_KeepsMinimum()
        {
            // 幅が足りなくても最低1ページ分を確保する (R2)
            var liveLength = PageLockTools.GetLiveLength(new Size(1000, 800), 900.0, 400.0, PageFrameOrientation.Horizontal);

            Assert.Equal(400.0, liveLength, 3);
        }

        [Fact]
        public void GetRowLength_Sums()
        {
            Assert.Equal(1400.0, PageLockTools.GetRowLength(467.0, 933.0), 3);
        }

        [Fact]
        public void GetRowScale_EnoughSpace()
        {
            var scale = PageLockTools.GetRowScale(new Size(1400, 800), 1400.0, PageFrameOrientation.Horizontal);

            Assert.Equal(1.0, scale, 3);
        }

        [Fact]
        public void GetRowScale_Overflow()
        {
            // 表示行が長すぎる場合は1.0未満の統一スケールになる
            var scale = PageLockTools.GetRowScale(new Size(1400, 800), 2335.0, PageFrameOrientation.Horizontal);

            Assert.Equal(1400.0 / 2335.0, scale, 3);
        }

        [Fact]
        public void GetRowScale_Vertical()
        {
            var scale = PageLockTools.GetRowScale(new Size(1400, 800), 1600.0, PageFrameOrientation.Vertical);

            Assert.Equal(0.5, scale, 3);
        }

        [Fact]
        public void GetViewMargin_Horizontal()
        {
            var margin = PageLockTools.GetViewMargin(467.0, PageFrameOrientation.Horizontal);

            Assert.Equal(new Thickness(467.0, 0.0, 0.0, 0.0), margin);
        }

        [Fact]
        public void GetViewMargin_Vertical()
        {
            var margin = PageLockTools.GetViewMargin(300.0, PageFrameOrientation.Vertical);

            Assert.Equal(new Thickness(0.0, 300.0, 0.0, 0.0), margin);
        }

        [Fact]
        public void HasUnlockedPageExcept_AnotherUnlocked()
        {
            // 対象以外に未固定ページがある
            var locked = new[] { false, false, true };
            Assert.True(PageLockTools.HasUnlockedPageExcept(0, locked.Length, i => locked[i]));
        }

        [Fact]
        public void HasUnlockedPageExcept_AllOthersLocked()
        {
            // 対象以外がすべて固定済み = 全ページ固定になる
            var locked = new[] { false, true, true };
            Assert.False(PageLockTools.HasUnlockedPageExcept(0, locked.Length, i => locked[i]));
        }

        [Fact]
        public void HasUnlockedPageExcept_TargetAlreadyLocked()
        {
            // 対象の固定状態には依存しない
            var locked = new[] { true, false };
            Assert.True(PageLockTools.HasUnlockedPageExcept(0, locked.Length, i => locked[i]));
        }

        [Fact]
        public void HasUnlockedPageExcept_SinglePage()
        {
            var locked = new[] { false };
            Assert.False(PageLockTools.HasUnlockedPageExcept(0, locked.Length, i => locked[i]));
        }

        [Fact]
        public void HasUnlockedPageExcept_EmptyBook()
        {
            Assert.False(PageLockTools.HasUnlockedPageExcept(0, 0, _ => false));
        }

        [Theory]
        [InlineData(-3, 1)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(3, 3)]
        public void EnsureVisiblePageCount_AtLeastOne(int computedCount, int expected)
        {
            // R2: 表示ページ数は最低1ページ
            Assert.Equal(expected, PageLockTools.EnsureVisiblePageCount(computedCount));
        }

        [Fact]
        public void MaxLockCount_IsEight()
        {
            Assert.Equal(8, PageLockTools.MaxLockCount);
        }

        // === 固定ページの読み飛ばし (次/前ページ移動) ===

        [Fact]
        public void FindNextUnlockedPageIndex_SkipsLockedForward()
        {
            // 開始位置(2)から前進し、固定済みの 2,3 を飛ばして 4 を返す
            var locked = new[] { false, false, true, true, false };
            var index = PageLockTools.FindNextUnlockedPageIndex(2, +1, locked.Length, false, i => locked[i]);

            Assert.Equal(4, index);
        }

        [Fact]
        public void FindNextUnlockedPageIndex_StartUnlocked()
        {
            var locked = new[] { false, false, true };
            var index = PageLockTools.FindNextUnlockedPageIndex(1, +1, locked.Length, false, i => locked[i]);

            Assert.Equal(1, index);
        }

        [Fact]
        public void FindNextUnlockedPageIndex_SkipsLockedBackward()
        {
            var locked = new[] { false, true, true, false };
            var index = PageLockTools.FindNextUnlockedPageIndex(2, -1, locked.Length, false, i => locked[i]);

            Assert.Equal(0, index);
        }

        [Fact]
        public void FindNextUnlockedPageIndex_NoUnlockedAhead_ReturnsMinusOne()
        {
            // 最後の2ページが固定済み -> 前進先なし (ページ終端)
            var locked = new[] { false, true, true };
            var index = PageLockTools.FindNextUnlockedPageIndex(1, +1, locked.Length, false, i => locked[i]);

            Assert.Equal(-1, index);
        }

        [Fact]
        public void FindNextUnlockedPageIndex_NoUnlockedBackward_ReturnsMinusOne()
        {
            // 開始位置(1)から後退した先に未固定ページなし
            var locked = new[] { true, true };
            var index = PageLockTools.FindNextUnlockedPageIndex(1, -1, locked.Length, false, i => locked[i]);

            Assert.Equal(-1, index);
        }

        [Fact]
        public void FindNextUnlockedPageIndex_OutOfRange_ReturnsMinusOne()
        {
            var locked = new[] { false, false };
            Assert.Equal(-1, PageLockTools.FindNextUnlockedPageIndex(2, +1, locked.Length, false, i => locked[i]));
            Assert.Equal(-1, PageLockTools.FindNextUnlockedPageIndex(-1, -1, locked.Length, false, i => locked[i]));
        }

        [Fact]
        public void FindNextUnlockedPageIndex_LoopWraps()
        {
            // ループ表示では末尾から先頭へ回り込む
            var locked = new[] { false, true, true };
            var index = PageLockTools.FindNextUnlockedPageIndex(1, +1, locked.Length, true, i => locked[i]);

            Assert.Equal(0, index);
        }

        [Fact]
        public void FindNextUnlockedPageIndex_AllLocked_ReturnsMinusOne()
        {
            var locked = new[] { true, true, true };
            Assert.Equal(-1, PageLockTools.FindNextUnlockedPageIndex(0, +1, locked.Length, false, i => locked[i]));
            Assert.Equal(-1, PageLockTools.FindNextUnlockedPageIndex(0, +1, locked.Length, true, i => locked[i]));
        }

        [Fact]
        public void FindNextUnlockedPageIndex_EmptyBook_ReturnsMinusOne()
        {
            Assert.Equal(-1, PageLockTools.FindNextUnlockedPageIndex(0, +1, 0, false, _ => false));
        }
    }
}
