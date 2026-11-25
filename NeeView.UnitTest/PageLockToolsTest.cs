using System.Windows;

namespace NeeView.UnitTest
{
    /// <summary>
    /// ページ固定の参照表示計算のテスト
    /// </summary>
    public class PageLockToolsTest
    {
        [Fact]
        public void GetPaneWidth_Default()
        {
            // 縦長ページは高さいっぱいに合わせた幅
            var viewSize = new Size(1600, 1000);
            var imageSize = new Size(800, 1200);

            var width = PageLockTools.GetPaneWidth(viewSize, imageSize);

            Assert.Equal(1000.0 * 800.0 / 1200.0, width, 3);
        }

        [Fact]
        public void GetPaneWidth_MaxRate()
        {
            // 横長ページは幅の上限で制限される
            var viewSize = new Size(1000, 800);
            var imageSize = new Size(3000, 1000);

            var width = PageLockTools.GetPaneWidth(viewSize, imageSize);

            Assert.Equal(500.0, width, 3);
        }

        [Fact]
        public void GetPaneWidth_CustomMaxRate()
        {
            var viewSize = new Size(1000, 800);
            var imageSize = new Size(3000, 1000);

            var width = PageLockTools.GetPaneWidth(viewSize, imageSize, 0.25);

            Assert.Equal(250.0, width, 3);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(180.0)]
        [InlineData(360.0)]
        public void GetRotatedPixelSize_NotQuarterTurn(double angle)
        {
            var size = PageLockTools.GetRotatedPixelSize(new Size(800, 1200), angle);

            Assert.Equal(new Size(800, 1200), size);
        }

        [Theory]
        [InlineData(90.0)]
        [InlineData(-90.0)]
        [InlineData(270.0)]
        [InlineData(-270.0)]
        public void GetRotatedPixelSize_QuarterTurn(double angle)
        {
            var size = PageLockTools.GetRotatedPixelSize(new Size(800, 1200), angle);

            Assert.Equal(new Size(1200, 800), size);
        }

        [Fact]
        public void GetRotatedPixelSize_Empty()
        {
            var size = PageLockTools.GetRotatedPixelSize(Size.Empty, 90.0);

            Assert.Equal(Size.Empty, size);
        }

        [Fact]
        public void GetSide_Left()
        {
            var pageRect = new Rect(0.0, 0.0, 400.0, 800.0);

            var side = PageLockTools.GetSide(pageRect, new Size(1200, 800));

            Assert.Equal(PageLockSide.Left, side);
        }

        [Fact]
        public void GetSide_Right()
        {
            var pageRect = new Rect(700.0, 0.0, 500.0, 800.0);

            var side = PageLockTools.GetSide(pageRect, new Size(1200, 800));

            Assert.Equal(PageLockSide.Right, side);
        }

        [Fact]
        public void GetSide_EmptyView()
        {
            var pageRect = new Rect(0.0, 0.0, 100.0, 100.0);

            var side = PageLockTools.GetSide(pageRect, Size.Empty);

            Assert.Equal(PageLockSide.Right, side);
        }

        [Fact]
        public void GetPaneWidth_EmptyView()
        {
            var width = PageLockTools.GetPaneWidth(Size.Empty, new Size(800, 1200));

            Assert.Equal(0.0, width, 3);
        }
    }
}
