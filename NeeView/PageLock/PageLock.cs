using CommunityToolkit.Mvvm.ComponentModel;
using NeeLaboratory.ComponentModel;
using NeeView.ComponentModel;
using NeeView.PageFrames;
using NeeView.Properties;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NeeView
{
    /// <summary>
    /// ページ固定（参照表示）管理
    /// </summary>
    /// <remarks>
    /// 固定したページは主ビューの行頭に参照区として表示する。
    /// 表示内容は固定時のスナップショットなので、ブックを切り替えても表示できる。
    /// </remarks>
    public class PageLock : ObservableObject, IDisposable
    {
        static PageLock() => Current = new PageLock();
        public static PageLock Current { get; }

        /// <summary>
        /// 主ビューの最低長が取得できないときの比率
        /// </summary>
        private const double FallbackLiveLengthRate = 0.25;

        private readonly DisposableCollection _disposables = new();
        private readonly ObservableCollection<PageLockItem> _items = new();
        private volatile Page[] _lockedPages = Array.Empty<Page>();
        private Size _viewSize;
        private PageFrameOrientation _orientation = PageFrameOrientation.Horizontal;
        private double _stripLength;
        private double _liveLength;
        private double _rowScale = 1.0;
        private bool _isAvailable;
        private bool _isFull;
        private bool _disposedValue;


        private PageLock()
        {
            // BookSetting はブック読み込み時にワーカースレッドで更新されることがある。
            // レイアウト更新は UI スレッドで行う必要があるため、ディスパッチャーに委譲する。
            _disposables.Add(Config.Current.BookSetting.SubscribePropertyChanged(nameof(BookSettingConfig.PageMode), (s, e) => AppDispatcher.BeginInvoke(OnPageModeChanged)));
            _disposables.Add(Config.Current.Book.SubscribePropertyChanged(nameof(BookConfig.IsPanorama), (s, e) => OnBookSettingChanged()));
            _disposables.Add(BookOperation.Current.SubscribeBookChanged((s, e) => OnBookChanged(e.Book)));

            IsAvailable = CheckAvailable();
        }


        /// <summary>
        /// 参照区のレイアウト更新要求
        /// </summary>
        public event EventHandler? LayoutChanged;

        /// <summary>
        /// 固定ページの増減通知
        /// </summary>
        public event EventHandler? LockChanged;

        /// <summary>
        /// 固定ページ一覧。固定順 = 行頭からの表示順
        /// </summary>
        public ObservableCollection<PageLockItem> Items => _items;

        /// <summary>
        /// 固定ページ数
        /// </summary>
        public int LockedCount => _items.Count;

        /// <summary>
        /// ページ固定中？
        /// </summary>
        public bool IsLocked => _items.Count > 0;

        /// <summary>
        /// ページ固定可能？
        /// </summary>
        public bool IsAvailable
        {
            get => _isAvailable;
            private set
            {
                if (_isAvailable != value)
                {
                    _isAvailable = value;
                    OnPropertyChanged();
                    LayoutChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// 固定上限に達している？
        /// </summary>
        public bool IsFull
        {
            get => _isFull;
            private set
            {
                if (_isFull != value)
                {
                    _isFull = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 参照区の長さ
        /// </summary>
        public double StripLength => _stripLength;

        /// <summary>
        /// 主ビューの自然長
        /// </summary>
        public double LiveLength => _liveLength;

        /// <summary>
        /// 表示行の統一スケール
        /// </summary>
        public double RowScale => _rowScale;

        /// <summary>
        /// 表示行の自然幅
        /// </summary>
        public double RowWidth => _orientation == PageFrameOrientation.Vertical ? _viewSize.Width : _stripLength + _liveLength;

        /// <summary>
        /// 表示行の自然高
        /// </summary>
        public double RowHeight => _orientation == PageFrameOrientation.Vertical ? _stripLength + _liveLength : _viewSize.Height;

        /// <summary>
        /// 表示行の並び方向
        /// </summary>
        public Orientation RowOrientation => _orientation == PageFrameOrientation.Vertical ? Orientation.Vertical : Orientation.Horizontal;

        /// <summary>
        /// 参照区のドック位置
        /// </summary>
        public Dock PinnedDock => _orientation == PageFrameOrientation.Vertical ? Dock.Top : Dock.Left;


        /// <summary>
        /// 主ビューのキャンバスサイズを通知
        /// </summary>
        public void SetViewSize(Size viewSize)
        {
            if (_disposedValue) return;
            if (_viewSize == viewSize) return;

            _viewSize = viewSize;
            UpdateLayout();
        }

        /// <summary>
        /// 固定対象ページを解決する
        /// </summary>
        /// <remarks>
        /// マウス位置のページ、無ければ現在ページ
        /// </remarks>
        public ViewContent? ResolveTargetViewContent()
        {
            var mainView = MainViewComponent.Current.MainView;

            var viewContent = mainView.GetViewContentAtMousePosition();
            if (viewContent is not null) return viewContent;

            var page = BookOperation.Current.Book?.CurrentPage;
            if (page is null) return null;

            var frameContent = MainViewComponent.Current.PageFrameBoxPresenter.View?.GetSelectedPageFrameContent();
            return frameContent?.ViewContents.FirstOrDefault(e => e.Page == page);
        }

        /// <summary>
        /// ページ固定の切り替え
        /// </summary>
        /// <param name="toggleMode">切り替えモード</param>
        /// <param name="target">固定対象。nullなら自動解決</param>
        public void Toggle(ToggleMode toggleMode, ViewContent? target = null)
        {
            if (_disposedValue) return;

            if (!IsAvailable)
            {
                if (toggleMode == ToggleMode.Off && IsLocked)
                {
                    UnlockAll();
                }
                return;
            }

            var viewContent = target ?? ResolveTargetViewContent();

            if (toggleMode == ToggleMode.Off)
            {
                var item = viewContent is not null ? FindItem(viewContent.Page) : null;
                if (item is not null)
                {
                    UnlockItem(item);
                }
                else
                {
                    UnlockAll();
                }
                return;
            }

            if (viewContent is null)
            {
                ShowMessage(TextResources.GetString("PageLock.CannotLock"));
                return;
            }

            var existing = FindItem(viewContent.Page);
            if (existing is not null)
            {
                if (toggleMode == ToggleMode.Toggle)
                {
                    UnlockItem(existing);
                }
                return;
            }

            Lock(viewContent);
        }

        /// <summary>
        /// 指定ページを固定する
        /// </summary>
        public bool Lock(ViewContent viewContent)
        {
            if (_disposedValue) return false;
            if (!IsAvailable) return false;

            var page = viewContent.Page;
            if (FindItem(page) is not null) return false;

            if (IsFull)
            {
                ShowMessage(string.Format(TextResources.GetString("PageLock.Full"), PageLockTools.MaxLockCount));
                return false;
            }

            // 最後の未固定ページを固定すると表示できるページが無くなるため、その場合のみ拒否する
            if (IsLastUnlockedPageInBook(page))
            {
                ShowMessage(TextResources.GetString("PageLock.CannotLockAll"));
                return false;
            }

            var angle = GetPageFrameAngle(viewContent);
            var imageSource = CreateSnapshot(viewContent, angle);
            if (imageSource is null)
            {
                ShowMessage(TextResources.GetString("PageLock.CannotLock"));
                return false;
            }

            var imageSize = GetImageSize(imageSource, viewContent, angle);
            var pageLabel = string.Format(TextResources.GetString("PageLock.PageFormat"), page.IndexPlusOne);
            var book = BookOperation.Current.Book;
            var sourceBookPath = book?.Path ?? page.BookPath;
            var item = new PageLockItem(page, imageSource, imageSize, pageLabel, sourceBookPath, BookTools.PathToBookName(sourceBookPath));
            item.IsSourceBookVisible = IsSourceBookVisible(sourceBookPath);

            _items.Add(item);
            UpdateCurrentPageFlags();
            UpdateLockedPages();
            UpdateLayout();
            LockChanged?.Invoke(this, EventArgs.Empty);

            ShowMessage(string.Format(TextResources.GetString("PageLock.LockedCount"), pageLabel, _items.Count));

            return true;
        }

        /// <summary>
        /// ページ固定を解除する
        /// </summary>
        public bool UnlockItem(PageLockItem item)
        {
            if (_disposedValue) return false;
            if (!_items.Remove(item)) return false;

            UpdateCurrentPageFlags();
            UpdateLockedPages();
            UpdateLayout();
            LockChanged?.Invoke(this, EventArgs.Empty);

            ShowMessage(string.Format(TextResources.GetString("PageLock.UnlockedItem"), item.PageLabel));
            return true;
        }

        /// <summary>
        /// すべてのページ固定を解除する
        /// </summary>
        public void UnlockAll()
        {
            if (_disposedValue) return;
            if (!IsLocked) return;

            var count = _items.Count;
            ClearItems();
            ShowMessage(string.Format(TextResources.GetString("PageLock.UnlockedAll"), count));
        }

        /// <summary>
        /// 固定状態を破棄する (通知なし)
        /// </summary>
        public void Clear()
        {
            if (_disposedValue) return;
            if (!IsLocked) return;

            ClearItems();
        }

        /// <summary>
        /// 指定ページが固定されているか?
        /// </summary>
        public bool IsPageLocked(Page? page)
        {
            if (page is null) return false;

            var lockedPages = _lockedPages;
            foreach (var lockedPage in lockedPages)
            {
                if (lockedPage == page) return true;
            }
            return false;
        }

        /// <summary>
        /// 現在固定しているページの一覧
        /// </summary>
        public IReadOnlyList<Page> GetLockedPages()
        {
            return _lockedPages;
        }

        private void UpdateLockedPages()
        {
            _lockedPages = _items.Select(e => e.Page).ToArray();
        }

        private void ClearItems()
        {
            _items.Clear();
            UpdateCurrentPageFlags();
            UpdateLockedPages();
            UpdateLayout();
            LockChanged?.Invoke(this, EventArgs.Empty);
        }

        private PageLockItem? FindItem(Page? page)
        {
            if (page is null) return null;
            return _items.FirstOrDefault(e => e.IsSamePage(page));
        }

        /// <summary>
        /// 指定ページが現在のブックで最後の未固定ページか?
        /// </summary>
        /// <remarks>
        /// このページを固定すると主ビューに表示できるページが無くなるため、固定を拒否する。
        /// </remarks>
        private bool IsLastUnlockedPageInBook(Page page)
        {
            var book = BookOperation.Current.Book;
            if (book is null) return false;

            var pages = book.Pages;
            if (page.Index < 0 || page.Index >= pages.Count || pages[page.Index] != page) return false;

            return !PageLockTools.HasUnlockedPageExcept(page.Index, pages.Count, i => IsPageLocked(pages[i]));
        }

        /// <summary>
        /// レイアウトを再計算する
        /// </summary>
        private void UpdateLayout()
        {
            if (_disposedValue) return;

            _viewSize = _viewSize.IsEmpty ? (MainViewComponent.Current.MainView?.GetViewCanvasSize() ?? Size.Empty) : _viewSize;
            _orientation = GetOrientation();

            foreach (var item in _items)
            {
                var length = PageLockTools.GetCellLength(_viewSize, item.ImageSize, _orientation);
                item.CellLength = length;
                item.CellWidth = _orientation == PageFrameOrientation.Vertical ? double.NaN : length;
                item.CellHeight = _orientation == PageFrameOrientation.Vertical ? length : double.NaN;
            }

            _stripLength = PageLockTools.GetStripLength(_items.Select(e => e.CellLength).ToList());

            var canvasLength = _orientation == PageFrameOrientation.Vertical ? _viewSize.Height : _viewSize.Width;
            var liveMinLength = GetLiveMinLength(canvasLength);
            _liveLength = PageLockTools.GetLiveLength(_viewSize, _stripLength, liveMinLength, _orientation);

            var rowLength = PageLockTools.GetRowLength(_stripLength, _liveLength);
            _rowScale = PageLockTools.GetRowScale(_viewSize, rowLength, _orientation);

            IsFull = _items.Count >= PageLockTools.MaxLockCount;

            NotifyLayoutChanged();
        }

        private double GetLiveMinLength(double canvasLength)
        {
            var imageSize = GetLivePageImageSize();
            var length = imageSize.IsEmpty ? 0.0 : PageLockTools.GetLiveMinLength(_viewSize, imageSize, _orientation);
            if (length <= 0.0)
            {
                length = Math.Max(0.0, canvasLength * FallbackLiveLengthRate);
            }
            return length;
        }

        /// <summary>
        /// 主ビューに表示するページのイメージサイズ
        /// </summary>
        private Size GetLivePageImageSize()
        {
            var frameContent = MainViewComponent.Current.PageFrameBoxPresenter.View?.GetSelectedPageFrameContent();
            var viewContent = frameContent?.ViewContents.FirstOrDefault(e => !IsPageLocked(e.Page));
            if (viewContent is not null)
            {
                var size = viewContent.Element.Size;
                if (!size.IsEmpty) return size;
            }

            var page = BookOperation.Current.Book?.CurrentPage;
            if (page is not null && !page.Size.IsEmpty)
            {
                return page.Size;
            }

            return Size.Empty;
        }

        private void NotifyLayoutChanged()
        {
            OnPropertyChanged(nameof(LockedCount));
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(IsFull));
            OnPropertyChanged(nameof(StripLength));
            OnPropertyChanged(nameof(LiveLength));
            OnPropertyChanged(nameof(RowScale));
            OnPropertyChanged(nameof(RowWidth));
            OnPropertyChanged(nameof(RowHeight));
            OnPropertyChanged(nameof(RowOrientation));
            OnPropertyChanged(nameof(PinnedDock));

            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateCurrentPageFlags()
        {
            var page = BookOperation.Current.Book?.CurrentPage;
            foreach (var item in _items)
            {
                item.IsCurrentPage = item.IsSamePage(page);
            }
        }

        private void UpdateSourceBookVisibility()
        {
            foreach (var item in _items)
            {
                item.IsSourceBookVisible = IsSourceBookVisible(item.SourceBookPath);
            }
        }

        /// <summary>
        /// 参照区の並び方向
        /// </summary>
        private static PageFrameOrientation GetOrientation()
        {
            return Config.Current.BookSetting.PageMode.IsAdaptive() ? Config.Current.Book.Orientation : PageFrameOrientation.Horizontal;
        }

        private static bool IsSourceBookVisible(string sourceBookPath)
        {
            var book = BookOperation.Current.Book;
            return book is null || !string.Equals(book.Path, sourceBookPath, StringComparison.Ordinal);
        }

        private static Size GetImageSize(ImageSource imageSource, ViewContent viewContent, double angle)
        {
            var size = new Size(imageSource.Width, imageSource.Height);
            if (double.IsNaN(size.Width) || double.IsNaN(size.Height) || double.IsInfinity(size.Width) || double.IsInfinity(size.Height)
                || size.Width <= 0.0 || size.Height <= 0.0)
            {
                size = PageLockTools.GetRotatedPixelSize(viewContent.Element.Size, angle);
            }
            return size;
        }

        private static double GetPageFrameAngle(ViewContent viewContent)
        {
            var box = MainViewComponent.Current.PageFrameBoxPresenter.View;
            return box?.GetPageFrameAngle(viewContent) ?? viewContent.RenderAngle;
        }

        /// <summary>
        /// 固定ページのスナップショットを生成する
        /// </summary>
        /// <remarks>
        /// ViewContent の読み込み済みビットマップを保持する。自動回転はここで適用する。
        /// </remarks>
        private static ImageSource? CreateSnapshot(ViewContent viewContent, double angle)
        {
            var imageSource = viewContent.ImageSource;
            if (imageSource is null) return null;

            if (imageSource is not BitmapSource bitmapSource)
            {
                if (!imageSource.IsFrozen && imageSource.CanFreeze)
                {
                    imageSource.Freeze();
                }
                return imageSource;
            }

            if (!bitmapSource.IsFrozen && bitmapSource.CanFreeze)
            {
                bitmapSource.Freeze();
            }

            if (!PageLockTools.IsQuarterTurn(angle)) return bitmapSource;

            try
            {
                var rotated = new TransformedBitmap(bitmapSource, new RotateTransform(angle));
                if (rotated.CanFreeze) rotated.Freeze();
                return rotated;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                Debug.WriteLine($"PageLock: Snapshot rotation failed. {ex.Message}");
                return bitmapSource;
            }
        }

        private static bool CheckAvailable()
        {
            var book = BookOperation.Current.Book;
            if (book is null || book.IsMedia) return false;
            if (book.Pages.Count <= 0) return false;
            if (Config.Current.Book.IsPanorama) return false;
            return Config.Current.BookSetting.PageMode.IsMultiPageFrame();
        }

        private void OnPageModeChanged()
        {
            if (_disposedValue) return;

            var available = CheckAvailable();
            if (!available && IsLocked && Config.Current.BookSetting.PageMode.IsSinglePage())
            {
                var count = _items.Count;
                ClearItems();
                ShowMessage(string.Format(TextResources.GetString("PageLock.AutoUnlocked"), count));
            }
            IsAvailable = available;
            UpdateLayout();
        }

        private void OnBookSettingChanged()
        {
            if (_disposedValue) return;

            var available = CheckAvailable();
            if (!available && IsLocked)
            {
                ClearItems();
            }
            IsAvailable = available;
            UpdateLayout();
        }

        private void OnBookChanged(Book? book)
        {
            if (_disposedValue) return;

            if (book is null || book.IsMedia)
            {
                if (IsLocked)
                {
                    ClearItems();
                }
                IsAvailable = false;
            }
            else
            {
                IsAvailable = CheckAvailable();
            }

            UpdateSourceBookVisibility();
            UpdateCurrentPageFlags();
            UpdateLayout();
        }

        private void ShowMessage(string? message)
        {
            if (_disposedValue) return;
            if (string.IsNullOrEmpty(message)) return;

            InfoMessage.Current.SetMessage(InfoMessageType.Command, message);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _disposables.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
