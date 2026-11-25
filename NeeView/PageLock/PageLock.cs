using CommunityToolkit.Mvvm.ComponentModel;
using NeeLaboratory.ComponentModel;
using NeeView.PageFrames;
using NeeView.Properties;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NeeView
{
    /// <summary>
    /// ページ固定（参照表示）管理
    /// </summary>
    /// <remarks>
    /// 固定したページは主ビューとは独立した参照表示として保持する。
    /// 表示内容は固定時のスナップショットなので、ブックを切り替えても表示できる。
    /// </remarks>
    public class PageLock : ObservableObject, IDisposable
    {
        static PageLock() => Current = new PageLock();
        public static PageLock Current { get; }


        private readonly DisposableCollection _disposables = new();
        private Page? _page;
        private ImageSource? _imageSource;
        private Size _imageSize;
        private string _pageLabel = "";
        private string _sourceBookPath = "";
        private string _sourceBookLabel = "";
        private Size _viewSize;
        private PageLockSide _side = PageLockSide.Right;
        private double _paneWidth;
        private bool _isLocked;
        private bool _isAvailable;
        private bool _disposedValue;


        private PageLock()
        {
            _disposables.Add(Config.Current.BookSetting.SubscribePropertyChanged(nameof(BookSettingConfig.PageMode), (s, e) => OnPageModeChanged()));
            _disposables.Add(Config.Current.Book.SubscribePropertyChanged(nameof(BookConfig.IsPanorama), (s, e) => OnBookSettingChanged()));
            _disposables.Add(BookOperation.Current.SubscribeBookChanged((s, e) => OnBookChanged(e.Book)));

            IsAvailable = CheckAvailable();
        }


        /// <summary>
        /// ページ固定中？
        /// </summary>
        public bool IsLocked => _isLocked;

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
                }
            }
        }

        /// <summary>
        /// 固定ページのスナップショット
        /// </summary>
        public ImageSource? ImageSource => _imageSource;

        /// <summary>
        /// 固定ページの表示名
        /// </summary>
        public string PageLabel => _pageLabel;

        /// <summary>
        /// 固定ページのブック名
        /// </summary>
        public string SourceBookLabel => _sourceBookLabel;

        /// <summary>
        /// 固定ページのブック名を表示するか？
        /// </summary>
        public bool IsSourceBookVisible
        {
            get
            {
                if (!IsLocked || string.IsNullOrEmpty(_sourceBookLabel)) return false;
                var book = BookOperation.Current.Book;
                return book is null || !string.Equals(book.Path, _sourceBookPath, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// 参照表示の表示状態
        /// </summary>
        public Visibility PaneVisibility => IsLocked && _imageSource is not null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 参照表示の位置
        /// </summary>
        public HorizontalAlignment PaneAlignment => _side == PageLockSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        /// <summary>
        /// 参照表示の幅
        /// </summary>
        public double PaneWidth => _paneWidth;

        /// <summary>
        /// 参照表示のために主ビューが空ける余白
        /// </summary>
        public Thickness ViewMargin
        {
            get
            {
                if (PaneVisibility != Visibility.Visible) return default;

                return _side == PageLockSide.Left
                    ? new Thickness(_paneWidth, 0.0, 0.0, 0.0)
                    : new Thickness(0.0, 0.0, _paneWidth, 0.0);
            }
        }


        /// <summary>
        /// 主ビューのキャンバスサイズを通知
        /// </summary>
        public void SetViewSize(Size viewSize)
        {
            if (_disposedValue) return;
            if (_viewSize == viewSize) return;

            _viewSize = viewSize;
            UpdatePaneWidth();
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
                if (toggleMode == ToggleMode.Off)
                {
                    Unlock();
                }
                return;
            }

            if (toggleMode == ToggleMode.Off)
            {
                Unlock();
                return;
            }

            var viewContent = target ?? ResolveTargetViewContent();
            if (viewContent is null)
            {
                ShowMessage(TextResources.GetString("PageLock.CannotLock"));
                return;
            }

            if (IsLocked && IsSamePage(viewContent.Page))
            {
                if (toggleMode != ToggleMode.On)
                {
                    Unlock();
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

            var angle = GetPageFrameAngle(viewContent);
            var imageSource = CreateSnapshot(viewContent, angle);
            if (imageSource is null)
            {
                ShowMessage(TextResources.GetString("PageLock.CannotLock"));
                return false;
            }

            var oldLabel = _isLocked ? _pageLabel : null;
            var book = BookOperation.Current.Book;
            var page = viewContent.Page;

            _page = page;
            _imageSource = imageSource;
            _imageSize = GetImageSize(imageSource, viewContent, angle);
            _pageLabel = string.Format(TextResources.GetString("PageLock.PageFormat"), page.IndexPlusOne);
            _sourceBookPath = book?.Path ?? page.BookPath;
            _sourceBookLabel = BookTools.PathToBookName(_sourceBookPath);
            _side = GetLockSide(viewContent);
            _viewSize = GetViewSize();
            _isLocked = true;

            UpdatePaneWidth();
            NotifyStateChanged();

            if (oldLabel is not null && oldLabel != _pageLabel)
            {
                ShowMessage(string.Format(TextResources.GetString("PageLock.Replaced"), _pageLabel, oldLabel));
            }
            else
            {
                ShowMessage(string.Format(TextResources.GetString("PageLock.Locked"), _pageLabel));
            }

            return true;
        }

        /// <summary>
        /// ページ固定を解除する
        /// </summary>
        public void Unlock()
        {
            if (_disposedValue) return;
            if (!IsLocked) return;

            ClearLock();
            ShowMessage(TextResources.GetString("PageLock.Unlocked"));
        }

        /// <summary>
        /// 固定状態を破棄する (通知なし)
        /// </summary>
        private void ClearLock()
        {
            _page = null;
            _imageSource = null;
            _imageSize = Size.Empty;
            _pageLabel = "";
            _sourceBookPath = "";
            _sourceBookLabel = "";
            _side = PageLockSide.Right;
            _paneWidth = 0.0;
            _isLocked = false;

            NotifyStateChanged();
        }

        private void NotifyStateChanged()
        {
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(ImageSource));
            OnPropertyChanged(nameof(PageLabel));
            OnPropertyChanged(nameof(SourceBookLabel));
            OnPropertyChanged(nameof(IsSourceBookVisible));
            OnPropertyChanged(nameof(PaneVisibility));
            OnPropertyChanged(nameof(PaneAlignment));
            OnPropertyChanged(nameof(PaneWidth));
            OnPropertyChanged(nameof(ViewMargin));
        }

        private void UpdatePaneWidth()
        {
            var paneWidth = IsLocked ? PageLockTools.GetPaneWidth(_viewSize, _imageSize) : 0.0;
            if (Math.Abs(_paneWidth - paneWidth) < 0.1) return;

            _paneWidth = paneWidth;
            OnPropertyChanged(nameof(PaneWidth));
            OnPropertyChanged(nameof(ViewMargin));
        }

        private Size GetViewSize()
        {
            if (!_viewSize.IsEmpty) return _viewSize;
            return MainViewComponent.Current.MainView.GetViewCanvasSize();
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

        private static PageLockSide GetLockSide(ViewContent viewContent)
        {
            try
            {
                var box = MainViewComponent.Current.PageFrameBoxPresenter.View;
                if (box is null) return PageLockSide.Right;

                var rect = viewContent.TransformToAncestor(box).TransformBounds(new Rect(0.0, 0.0, viewContent.ActualWidth, viewContent.ActualHeight));
                return PageLockTools.GetSide(rect, new Size(box.ActualWidth, box.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return PageLockSide.Right;
            }
        }

        private bool IsSamePage(Page page)
        {
            return _page is not null && _page == page;
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
                ClearLock();
                ShowMessage(TextResources.GetString("PageLock.AutoUnlocked"));
            }
            IsAvailable = available;
        }

        private void OnBookSettingChanged()
        {
            if (_disposedValue) return;

            var available = CheckAvailable();
            if (!available && IsLocked)
            {
                ClearLock();
            }
            IsAvailable = available;
        }

        private void OnBookChanged(Book? book)
        {
            if (_disposedValue) return;

            if (book is null || book.IsMedia)
            {
                if (IsLocked)
                {
                    ClearLock();
                }
                IsAvailable = false;
            }
            else
            {
                IsAvailable = CheckAvailable();
            }

            OnPropertyChanged(nameof(IsSourceBookVisible));
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
