using NeeView.PageFrames;
using System.Windows;
using System.Windows.Media;

namespace NeeView.UnitTest
{
    /// <summary>
    /// アダプティブ表示のフレームサイズ計算のテスト
    /// </summary>
    /// <remarks>
    /// 「予算内に収まっているフレームは、キャンバスの高さ(縦方向は幅)をちょうど埋める」
    /// という性質を確認する。
    /// </remarks>
    public class AdaptiveFrameStretchTest
    {
        private sealed class TestProfile : IContentSizeCalculatorProfile
        {
            public double ContentsSpace => 0.0;
            public PageStretchMode StretchMode => PageStretchMode.Uniform;
            public AutoRotateType AutoRotate => AutoRotateType.None;
            public AutoRotatePolicy AutoRotatePolicy => AutoRotatePolicy.FitToViewArea;
            public bool AllowFileContentAutoRotate => false;
            public bool AllowEnlarge => true;
            public bool AllowReduce => true;
            public Size ReferenceSize => CanvasSize;
            public Size CanvasSize { get; init; } = new Size(1200, 800);
            public DpiScale DpiScale => new DpiScale(1.0, 1.0);
            public WidePageStretch WidePageStretch => WidePageStretch.UniformHeight;
        }

        [Fact]
        public void Horizontal_FitsCanvasHeight()
        {
            // 900x800 のフレーム (Σアスペクト 1.125 <= 1.5) は 1200x800 のキャンバスで等倍
            var calculator = new ContentSizeCalculator(new TestProfile());
            var scale = calculator.CalcFrameStretchScale(new Size(900, 800), 0.0, new RotateTransform(0), new Size(1200, 800), PageFrameOrientation.Horizontal);
            Assert.Equal(1.0, scale, 6);
        }

        [Fact]
        public void Horizontal_OverBudgetFitsWidth()
        {
            // 1400x800 (Σアスペクト 1.75 > 1.5) は幅に合わせて縮小される
            var calculator = new ContentSizeCalculator(new TestProfile());
            var scale = calculator.CalcFrameStretchScale(new Size(1400, 800), 0.0, new RotateTransform(0), new Size(1200, 800), PageFrameOrientation.Horizontal);
            Assert.Equal(1200.0 / 1400.0, scale, 6);
        }

        [Fact]
        public void Vertical_FitsCanvasWidth()
        {
            // 縦方向: 100x120 のフレーム (Σアスペクト 1.2 <= 1.5) は 800x1200 のキャンバスで幅を埋める
            var calculator = new ContentSizeCalculator(new TestProfile());
            var scale = calculator.CalcFrameStretchScale(new Size(100, 120), 0.0, new RotateTransform(0), new Size(800, 1200), PageFrameOrientation.Vertical);
            Assert.Equal(8.0, scale, 6);
            Assert.Equal(800.0, 100.0 * scale, 6); // 幅がキャンバス幅に一致
        }

        [Fact]
        public void Horizontal_AllowEnlargeFalseLimitsScale()
        {
            // 拡大禁止設定では等倍が上限
            var calculator = new ContentSizeCalculator(new NoEnlargeProfile());
            var scale = calculator.CalcFrameStretchScale(new Size(600, 400), 0.0, new RotateTransform(0), new Size(1200, 800), PageFrameOrientation.Horizontal);
            Assert.Equal(1.0, scale, 6);
        }

        private sealed class NoEnlargeProfile : IContentSizeCalculatorProfile
        {
            public double ContentsSpace => 0.0;
            public PageStretchMode StretchMode => PageStretchMode.Uniform;
            public AutoRotateType AutoRotate => AutoRotateType.None;
            public AutoRotatePolicy AutoRotatePolicy => AutoRotatePolicy.FitToViewArea;
            public bool AllowFileContentAutoRotate => false;
            public bool AllowEnlarge => false;
            public bool AllowReduce => true;
            public Size ReferenceSize => CanvasSize;
            public Size CanvasSize => new Size(1200, 800);
            public DpiScale DpiScale => new DpiScale(1.0, 1.0);
            public WidePageStretch WidePageStretch => WidePageStretch.UniformHeight;
        }
    }
}
