using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using NeeLaboratory.ComponentModel;

namespace NeeView.PageFrames
{
    /// <summary>
    /// アダプティブ表示のフレーム分割表
    /// </summary>
    /// <remarks>
    /// フレームの切れ目は「ブック先頭から前方向に決まる」ものとして扱う。
    /// 双方向のフレーム生成で同じ分割結果になるようにするため、この表が唯一の情報源となる。
    /// 計算は必要になったページまで順次行う(遅延評価)。
    /// 表示に影響する変更(キャンバスサイズ、ページサイズ、ページ構成、並び方向、ページ間隔)で無効化する。
    /// UIスレッドと先読みスレッドの両方から参照されるため、内部は lock で保護する。
    /// </remarks>
    public sealed class AdaptivePageFrameMap : IDisposable
    {
        private readonly object _lock = new();
        private readonly PageFrameContext _context;
        private readonly BookContext _bookContext;
        private readonly List<FrameRange> _ranges = new();
        private readonly Dictionary<int, double> _measures = new();
        private readonly Dictionary<int, IDisposable> _sizeSubscriptions = new();
        private readonly IDisposable _contextSubscription;
        private readonly IDisposable _pagesChangedSubscription;
        private bool _disposedValue;


        public AdaptivePageFrameMap(PageFrameContext context, BookContext bookContext)
        {
            _context = context;
            _bookContext = bookContext;

            _contextSubscription = _context.SubscribePropertyChanged(Context_PropertyChanged);
            _pagesChangedSubscription = _bookContext.SubscribePagesChanged((s, e) => InvalidateAll());
        }


        /// <summary>
        /// 指定ページを含むフレーム範囲
        /// </summary>
        public PageRange GetFrameRange(int index)
        {
            lock (_lock)
            {
                if (_disposedValue) return PageRange.Empty;
                if (!_bookContext.ContainsIndex(index)) return PageRange.Empty;

                EnsureCovered(index);

                var i = FindRange(index);
                if (i < 0) return PageRange.Empty;

                var range = _ranges[i];
                return new PageRange(new PagePosition(range.Start, 0), new PagePosition(range.End, 1));
            }
        }

        /// <summary>
        /// 指定ページを含むフレームの先頭ページインデックス
        /// </summary>
        public int GetFrameStart(int index)
        {
            var range = GetFrameRange(index);
            return range.IsEmpty() ? index : range.Min.Index;
        }

        /// <summary>
        /// 全ページの分割を破棄する
        /// </summary>
        public void InvalidateAll()
        {
            lock (_lock)
            {
                _ranges.Clear();
                _measures.Clear();
            }
        }

        /// <summary>
        /// 指定ページ以降の分割を破棄する
        /// </summary>
        public void InvalidateFrom(int index)
        {
            lock (_lock)
            {
                if (_ranges.Count == 0) return;

                // index を含むフレーム以降を破棄する
                var i = _ranges.FindIndex(e => e.End >= index);
                if (i < 0) return;

                _ranges.RemoveRange(i, _ranges.Count - i);

                var keys = new List<int>();
                foreach (var key in _measures.Keys)
                {
                    if (key >= index) keys.Add(key);
                }
                foreach (var key in keys)
                {
                    _measures.Remove(key);
                }
            }
        }


        private void Dispose(bool disposing)
        {
            if (_disposedValue) return;

            if (disposing)
            {
                lock (_lock)
                {
                    foreach (var subscription in _sizeSubscriptions.Values)
                    {
                        subscription.Dispose();
                    }
                    _sizeSubscriptions.Clear();
                    _ranges.Clear();
                    _measures.Clear();
                }
                _contextSubscription.Dispose();
                _pagesChangedSubscription.Dispose();
            }

            _disposedValue = true;
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }


        /// <summary>
        /// index を含むフレームが決まるまで分割を進める
        /// </summary>
        private void EnsureCovered(int index)
        {
            var pageCount = _bookContext.Pages.Count;
            if (pageCount <= 0) return;

            while (true)
            {
                var last = _ranges.Count - 1;
                if (last >= 0 && index <= _ranges[last].End)
                {
                    return;
                }

                var start = last >= 0 ? _ranges[last].End + 1 : 0;
                if (start >= pageCount) return;

                var count = AdaptivePagePacker.GetFramePageCount(
                    GetMeasure,
                    pageCount,
                    _context.CanvasSize.Width,
                    _context.CanvasSize.Height,
                    Math.Max(0.0, _context.ContentsSpace),
                    _context.StackOrientation,
                    start,
                    PageFrameContext.AdaptiveMaxPageCount);

                var end = Math.Min(start + count - 1, pageCount - 1);
                _ranges.Add(new FrameRange(start, end));
            }
        }

        private int FindRange(int index)
        {
            for (var i = _ranges.Count - 1; i >= 0; i--)
            {
                if (index >= _ranges[i].Start)
                {
                    return index <= _ranges[i].End ? i : -1;
                }
            }
            return -1;
        }

        /// <summary>
        /// ページの測定値 (キャッシュ付き)
        /// </summary>
        private double GetMeasure(int index)
        {
            if (_measures.TryGetValue(index, out var measure))
            {
                return measure;
            }

            var page = _bookContext.GetPage(index);
            if (page is null)
            {
                return 1.0;
            }

            SubscribeSizeChanged(index, page);

            var range = PageRange.CreatePageRangeForOnePage(new PagePosition(index, 0), 1);
            Size size;
            try
            {
                size = new PageViewSizeCalculator(_context, page.Content.PageDataSource, range, 1).GetViewSize();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AdaptivePageFrameMap: GetMeasure failed: {ex.Message}");
                size = new Size(0.0, 0.0);
            }

            measure = AdaptivePagePacker.GetMeasure(size.Width, size.Height, _context.StackOrientation);
            _measures[index] = measure;
            return measure;
        }

        private void SubscribeSizeChanged(int index, Page page)
        {
            if (_sizeSubscriptions.ContainsKey(index)) return;

            _sizeSubscriptions[index] = page.SubscribeSizeChanged((s, e) =>
            {
                InvalidateFrom(index);
            });
        }

        private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PageFrameContext.CanvasSize):
                case nameof(PageFrameContext.ContentsSpace):
                case nameof(PageFrameContext.StackOrientation):
                    InvalidateAll();
                    break;
            }
        }


        private readonly record struct FrameRange(int Start, int End);
    }
}
