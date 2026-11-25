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
    /// フレームの切れ目は「アンカーページから前方向・後方向にそれぞれ決まる」ものとして扱う。
    /// 双方向のフレーム生成で同じ分割結果になるようにするため、この表が唯一の情報源となる。
    /// 計算は必要になったページまで順次行う(遅延評価)。
    /// ページサイズ、ページ構成、並び方向、ページ間隔の変更で無効化する。
    /// キャンバスサイズの変更は分割に即時反映せず、次のフレーム移動時にアンカーを決めて反映する(ApplyPendingLayout)。
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
        private Size? _layoutCanvasSize;
        private bool _isRegroupPending;
        private int _anchorIndex;
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
                InvalidateAllLocked();
            }
        }

        /// <summary>
        /// 保留しているキャンバスサイズ変更を分割へ反映する
        /// </summary>
        /// <param name="anchorIndex">新しい分割のアンカーページ。通常は移動先ページ</param>
        /// <remarks>
        /// ウィンドウサイズ変更だけでは分割を変更せず、ページ移動時にこのメソッドで反映する。
        /// 全分割を破棄し、anchorIndex より後ろは前方向、anchorIndex より前は後方向に
        /// 現在のキャンバスサイズで計算し直す。
        /// </remarks>
        public void ApplyPendingLayout(int anchorIndex)
        {
            lock (_lock)
            {
                if (_disposedValue) return;
                if (!_isRegroupPending) return;

                // キャンバスが無効なとき(最小化など)は保留したままにする
                if (!IsValidCanvasSize(_context.CanvasSize)) return;

                var pageCount = _bookContext.Pages.Count;
                if (pageCount <= 0)
                {
                    InvalidateAllLocked();
                    return;
                }

                InvalidateAllLocked();

                var anchor = anchorIndex;
                if (anchor < 0 || anchor > pageCount)
                {
                    anchor = _bookContext.NormalizeIndex(anchor);
                }
                _anchorIndex = Math.Clamp(anchor, 0, pageCount);
            }
        }

        /// <summary>
        /// 指定ページ以降の分割を破棄する
        /// </summary>
        public void InvalidateFrom(int index)
        {
            lock (_lock)
            {
                InvalidateFromLocked(index);
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


        private void InvalidateAllLocked()
        {
            _ranges.Clear();
            _measures.Clear();
            _layoutCanvasSize = null;
            _isRegroupPending = false;
            _anchorIndex = 0;
        }

        /// <summary>
        /// 指定ページ以降の分割を破棄する
        /// </summary>
        private void InvalidateFromLocked(int index)
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

        /// <summary>
        /// index を含むフレームが決まるまで分割を進める
        /// </summary>
        private void EnsureCovered(int index)
        {
            var pageCount = _bookContext.Pages.Count;
            if (pageCount <= 0) return;

            while (true)
            {
                if (_ranges.Count == 0)
                {
                    var added = index >= _anchorIndex
                        ? AddForwardRange(_anchorIndex, pageCount)
                        : AddBackwardRange(_anchorIndex - 1, pageCount);
                    if (!added) return;
                    continue;
                }

                if (index < _ranges[0].Start)
                {
                    if (!AddBackwardRange(_ranges[0].Start - 1, pageCount)) return;
                    continue;
                }

                if (index > _ranges[^1].End)
                {
                    if (!AddForwardRange(_ranges[^1].End + 1, pageCount)) return;
                    continue;
                }

                return;
            }
        }

        /// <summary>
        /// 前方分割を追加する
        /// </summary>
        /// <returns>追加した場合 true。範囲外の場合は false</returns>
        private bool AddForwardRange(int start, int pageCount)
        {
            if (start < 0 || start >= pageCount) return false;

            var canvasSize = GetLayoutCanvasSize();
            var count = AdaptivePagePacker.GetFramePageCount(
                GetMeasure,
                pageCount,
                canvasSize.Width,
                canvasSize.Height,
                Math.Max(0.0, _context.ContentsSpace),
                _context.StackOrientation,
                start,
                PageFrameContext.AdaptiveMaxPageCount);

            var end = Math.Min(start + count - 1, pageCount - 1);
            _ranges.Add(new FrameRange(start, end));
            return true;
        }

        /// <summary>
        /// 後方分割を追加する
        /// </summary>
        /// <returns>追加した場合 true。範囲外の場合は false</returns>
        private bool AddBackwardRange(int end, int pageCount)
        {
            if (end < 0 || end >= pageCount) return false;

            // 逆順の測定値で前方分割と同じ計算を行う
            var canvasSize = GetLayoutCanvasSize();
            var count = AdaptivePagePacker.GetFramePageCount(
                i => GetMeasure(end - i),
                end + 1,
                canvasSize.Width,
                canvasSize.Height,
                Math.Max(0.0, _context.ContentsSpace),
                _context.StackOrientation,
                0,
                PageFrameContext.AdaptiveMaxPageCount);

            var start = Math.Max(0, end - count + 1);
            _ranges.Insert(0, new FrameRange(start, end));
            return true;
        }

        /// <summary>
        /// 分割に使用しているキャンバスサイズ
        /// </summary>
        /// <remarks>
        /// サイズ変更はフレーム移動まで反映しないため、分割開始時のサイズを保持する。
        /// </remarks>
        private Size GetLayoutCanvasSize()
        {
            _layoutCanvasSize ??= _context.CanvasSize;
            return _layoutCanvasSize.Value;
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
                    OnCanvasSizeChanged();
                    break;

                case nameof(PageFrameContext.ContentsSpace):
                case nameof(PageFrameContext.StackOrientation):
                    InvalidateAll();
                    break;
            }
        }

        /// <summary>
        /// キャンバスサイズ変更
        /// </summary>
        /// <remarks>
        /// 表示中の分割は維持し、次のフレーム移動時に新しいサイズを反映する。
        /// 初期化前の暫定サイズで分割されている場合のみ即時破棄する。
        /// </remarks>
        private void OnCanvasSizeChanged()
        {
            lock (_lock)
            {
                if (_disposedValue) return;
                if (_ranges.Count == 0) return;

                var layoutCanvasSize = _layoutCanvasSize;
                if (layoutCanvasSize is null || !IsValidCanvasSize(layoutCanvasSize.Value))
                {
                    // 初期化前の暫定サイズ。即時反映する
                    InvalidateAllLocked();
                    return;
                }

                // 元のサイズに戻った場合は再分割不要
                _isRegroupPending = layoutCanvasSize.Value != _context.CanvasSize;
            }
        }

        /// <summary>
        /// 分割に使用できるキャンバスサイズか？
        /// </summary>
        private static bool IsValidCanvasSize(Size size)
        {
            return size.Width > PageFrameProfile.MinWidth && size.Height > PageFrameProfile.MinHeight;
        }


        private readonly record struct FrameRange(int Start, int End);
    }
}
