using NeeView.PageFrames;
using System.Collections.Generic;

namespace NeeView.UnitTest
{
    /// <summary>
    /// アダプティブ表示のフレーム分割計算のテスト
    /// </summary>
    public class AdaptivePagePackerTest
    {
        // ページサイズ (幅, 高さ)
        private static readonly (double Width, double Height)[] _sizes =
        {
            (600, 800),
            (200, 800),
            (100, 800),
            (500, 800),
        };

        private static List<double> CreateMeasures(PageFrameOrientation orientation)
        {
            var measures = new List<double>();
            foreach (var (width, height) in _sizes)
            {
                measures.Add(AdaptivePagePacker.GetMeasure(width, height, orientation));
            }
            return measures;
        }

        [Fact]
        public void Packer_UserExample()
        {
            // 1200x800 の画面に 600,200,100,500 幅のページ (すべて高さ800)
            // キャンバス高さいっぱいに拡大した幅で合計すると 600+200+100=900 <= 1200。
            // 4枚目を加えると 1400 > 1200 になるので取り込まない。
            var measures = CreateMeasures(PageFrameOrientation.Horizontal);
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);
            Assert.Equal(3, count);
        }

        [Fact]
        public void Packer_ExactBudgetIsIncluded()
        {
            // 予算とちょうどの値は取り込む
            var measures = new List<double> { 0.5, 0.5, 0.5, 0.5 };
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);
            Assert.Equal(3, count);
        }

        [Fact]
        public void Packer_NeverExceedsBudget()
        {
            // 取り込んだページの合計が予算を超えないこと
            var measures = new List<double> { 0.4, 0.3, 0.9, 0.2, 0.7, 0.1, 0.55 };
            var budget = 1200.0 / 800.0;
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);

            Assert.True(count >= 1);
            var sum = 0.0;
            for (var i = 0; i < count; i++)
            {
                sum += measures[i];
            }
            Assert.True(sum <= budget + 1e-9, $"sum={sum} budget={budget}");
        }

        [Fact]
        public void Packer_FirstPageIsAlone()
        {
            // 1枚だけで予算を超えるページ
            var measures = new List<double> { 3.0 };
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);
            Assert.Equal(1, count);
        }

        [Fact]
        public void Packer_MaxCount()
        {
            var measures = new List<double> { 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1 };
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 3);
            Assert.Equal(3, count);
        }

        [Fact]
        public void Packer_EmptyList()
        {
            var measures = new List<double>();
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);
            Assert.Equal(1, count);
        }

        [Fact]
        public void Packer_OutOfRangeStart()
        {
            var measures = new List<double> { 0.5, 0.5 };
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 10, 8);
            Assert.Equal(1, count);
        }

        [Fact]
        public void Packer_InvalidSizeIsTreatedAsOne()
        {
            var measures = new List<double> { 0.0, double.NaN, 0.5 };
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);
            Assert.True(count >= 1);
        }

        [Fact]
        public void Packer_SpanReducesPageCount()
        {
            var measures = new List<double> { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };

            var withoutSpan = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);
            var withSpan = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 100.0, PageFrameOrientation.Horizontal, 0, 8);

            Assert.True(withSpan <= withoutSpan);
            Assert.True(withSpan >= 1);
        }

        [Fact]
        public void Packer_VerticalOrientation()
        {
            // 縦方向は高さで判定する
            var measures = new List<double>
            {
                AdaptivePagePacker.GetMeasure(800, 600, PageFrameOrientation.Vertical),
                AdaptivePagePacker.GetMeasure(800, 600, PageFrameOrientation.Vertical),
            };

            var horizontalBudget = AdaptivePagePacker.GetBudget(600, 1200, PageFrameOrientation.Horizontal, 0.0, 1);
            var verticalBudget = AdaptivePagePacker.GetBudget(600, 1200, PageFrameOrientation.Vertical, 0.0, 1);

            Assert.Equal(0.5, horizontalBudget, 6);
            Assert.Equal(2.0, verticalBudget, 6);

            var count = AdaptivePagePacker.GetFramePageCount(measures, 600, 1200, 0.0, PageFrameOrientation.Vertical, 0, 8);
            Assert.Equal(2, count);
        }

        [Fact]
        public void Packer_ContinueFromMiddlePage()
        {
            var measures = new List<double> { 0.75, 0.75, 0.75, 0.75 };
            var count = AdaptivePagePacker.GetFramePageCount(measures, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 2, 8);
            Assert.Equal(2, count);
        }

        [Fact]
        public void Packer_BackwardFrame()
        {
            // 後方フレームは測定値を逆順にして同じ計算を行う
            // 予算 1200/800 = 1.5 に対して 0.625 + 0.125 + 0.25 = 1.0 まで取り込み、+0.75 で超過
            var measures = new List<double> { 0.75, 0.25, 0.125, 0.625 };
            var end = 3;

            var count = AdaptivePagePacker.GetFramePageCount(i => measures[end - i], end + 1, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);

            Assert.Equal(3, count);
            Assert.Equal(1, end - count + 1);
        }

        [Fact]
        public void Packer_BackwardFrame_CannotExceedBudget()
        {
            // 後方フレームでも合計が予算を超えないこと
            var measures = new List<double> { 0.4, 0.3, 0.9, 0.2, 0.7, 0.1 };
            var end = 5;
            var budget = 1200.0 / 800.0;

            var count = AdaptivePagePacker.GetFramePageCount(i => measures[end - i], end + 1, 1200, 800, 0.0, PageFrameOrientation.Horizontal, 0, 8);

            Assert.True(count >= 1);
            var sum = 0.0;
            for (var i = 0; i < count; i++)
            {
                sum += measures[end - i];
            }
            Assert.True(sum <= budget + 1e-9, $"sum={sum} budget={budget}");
        }

        [Fact]
        public void Measure_InvalidSize()
        {
            Assert.Equal(1.0, AdaptivePagePacker.GetMeasure(0, 0, PageFrameOrientation.Horizontal), 6);
            Assert.Equal(1.0, AdaptivePagePacker.GetMeasure(double.NaN, 100, PageFrameOrientation.Vertical), 6);
            Assert.Equal(2.0, AdaptivePagePacker.GetMeasure(400, 200, PageFrameOrientation.Horizontal), 6);
            Assert.Equal(2.0, AdaptivePagePacker.GetMeasure(200, 400, PageFrameOrientation.Vertical), 6);
        }
    }
}
