//#define LOCAL_DEBUG

using NeeLaboratory.ComponentModel;
using NeeLaboratory.Generators;
using NeeView.PageFrames;
using NeeView.Windows;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NeeView
{
    /// <summary>
    /// MainView.xaml の相互作用ロジック
    /// </summary>
    [LocalDebug]
    public partial class MainView : UserControl, IHasDeviceInput, IDisposable, ICursorSetter
    {
        private MainViewViewModel? _vm;
        private Window? _owner;
        private readonly DpiScaleProvider _dpiProvider = new();
        private readonly PageFrameBackground _background;
        private bool _disposedValue;
        private readonly DisposableCollection _disposables = new();
        private SlideShowInput? _slideShowInput;
        private readonly MainViewCursor _mainViewCursor;
        private CommandMenuAdapter _commandMenu;

        public MainView()
        {
            InitializeComponent();

            _background = new PageFrameBackground(_dpiProvider);
            _background.SetBinding(PageFrameBackground.PageProperty, new Binding(nameof(MainViewViewModel.SelectedPage)));
            this.MainViewPanel.Children.Insert(0, _background);

            _commandMenu = new CommandMenuAdapter(this.CommandContextMenu);

            _mainViewCursor = new MainViewCursor(this.View);
            _disposables.Add(_mainViewCursor);

            this.Loaded += MainView_Loaded;
            this.Unloaded += MainView_Unloaded;
            this.DataContextChanged += MainView_DataContextChanged;

            _disposables.Add(SlideShow.Current.SubscribePropertyChanged(nameof(SlideShow.IsPlaying), SlideShow_IsPlayingSlideShowPropertyChanged));
            _disposables.Add(SlideShow.Current.SubscribePlayed(SlideShow_Played));

            _disposables.Add(Config.Current.SlideShow.SubscribePropertyChanged(nameof(SlideShowConfig.IsTimerVisible), SlideShowConfig_IsTimerVisiblePropertyChanged));
        }


        public PageFrameBackground PageFrameBackground => _background;

        public MouseInput? MouseInput => _vm?.MouseInput;

        public TouchInput? TouchInput => _vm?.TouchInput;

        public DpiScaleProvider DpiProvider => _dpiProvider;

        public CommandMenuAdapter CommandMenu => _commandMenu;


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _disposables.Dispose();
                    _slideShowInput?.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public void Initialize()
        {
            _vm = this.DataContext as MainViewViewModel;
            if (_vm is null)
            {
                return;
            }

            ContentDropManager.Current.SetDragDropEvent(this.View);

            this.NowLoadingView.Source = NowLoading.Current;

            _vm.ViewComponent.OpenContextMenuRequest += (s, e) => OpenContextMenu();
            _vm.ViewComponent.FocusMainViewRequest += (s, e) => FocusMainView();

            _vm.ContextMenu.Closed += ContextMenu_Closed;

            NeeView.PageLock.Current.LayoutChanged -= PageLock_LayoutChanged;
            NeeView.PageLock.Current.LayoutChanged += PageLock_LayoutChanged;
            NeeView.PageLock.Current.LockChanged -= PageLock_LockChanged;
            NeeView.PageLock.Current.LockChanged += PageLock_LockChanged;

            NeeView.PageLock.Current.SetViewSize(GetViewCanvasSize());
            UpdatePageLockLayout();

            _mainViewCursor.Initialize();
        }

        private void MainView_DataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is MainView control)
            {
                control.Initialize();
            }
        }

        private void MainView_Loaded(object sender, RoutedEventArgs e)
        {
            _slideShowInput?.Dispose();
            _slideShowInput = new SlideShowInput(this, SlideShow.Current);

            var window = Window.GetWindow(this);
            if (_owner == window) return;

            SetOwnerWindow(window);

            var dpiScale = _owner is IDpiScaleProvider dpiProvider ? dpiProvider.GetDpiScale() : VisualTreeHelper.GetDpi(this);
            _dpiProvider.SetDipScale(dpiScale);
        }

        private void MainView_Unloaded(object sender, RoutedEventArgs e)
        {
            _slideShowInput?.Dispose();

            ResetOwnerWindow();
        }

        private void SlideShow_Played(object? sender, SlideShowPlayedEventArgs e)
        {
            //Debug.WriteLine($"## SlideShow: {e.IsPlaying}, {e.IntervalMilliseconds:f0}");

            AppDispatcher.BeginInvoke(() =>
            {
                var ani = e.IsPlaying ? new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(e.IntervalMilliseconds)) : null;
                this.SlideShowTimer.BeginAnimation(SimpleProgressBar.ValueProperty, ani, HandoffBehavior.SnapshotAndReplace);
            });
        }

        private void SlideShow_IsPlayingSlideShowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateSlideShowTimerVisibility();
        }

        private void SlideShowConfig_IsTimerVisiblePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateSlideShowTimerVisibility();
        }

        private void UpdateSlideShowTimerVisibility()
        {
            this.SlideShowTimer.Visibility = (SlideShow.Current.IsPlaying && Config.Current.SlideShow.IsTimerVisible) ? Visibility.Visible : Visibility.Collapsed;
        }



        private void SetOwnerWindow(Window window)
        {
            _owner = window;
            _owner.Activated += Window_Activated;
            _owner.Deactivated += Window_Deactivated;
        }

        private void ResetOwnerWindow()
        {
            if (_owner != null)
            {
                _owner.Activated -= Window_Activated;
                _owner.Deactivated -= Window_Deactivated;
                _owner = null;
            }
        }

        private void Window_Activated(object? sender, EventArgs e)
        {
            _mainViewCursor.ResetCursorVisible();
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            _mainViewCursor.ResetCursorVisible();
        }

        public void SetCursor(Cursor? cursor)
        {
            _mainViewCursor.SetCursor(cursor);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);

            _dpiProvider.SetDipScale(newDpi);
        }


        public void FocusMainView()
        {
            this.View.Focus();
        }

        /// <summary>
        /// ウィンドウサイズ補正
        /// </summary>
        public void StretchWindow()
        {
            var window = Window.GetWindow(this);
            if (window is null) return;
            if (window.WindowState != WindowState.Normal) return;
            if (_vm is null) return;

            try
            {
                var canvasSize = new Size(this.View.ActualWidth, this.View.ActualHeight);
                var content = GetSelectedPageFrameContent();
                if (content is null) return;
                MainViewViewModel.StretchWindow(window, canvasSize, content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return;
            }
        }

        /// <summary>
        /// 自動ウィンドウサイズ補正用
        /// </summary>
        public void AutoStretchWindow()
        {
            var window = Window.GetWindow(this);
            if (window is null) return;
            if (window.WindowState != WindowState.Normal) return;
            if (_vm is null) return;

            try
            {
                using var key = PageFrameProfile.ReferenceSizeLocker.Lock();
                var canvasSize = new Size(this.View.ActualWidth, this.View.ActualHeight);
                var content = GetSelectedPageFrameContent();
                if (content is null) return;
                MainViewViewModel.StretchReferenceWindow(window, canvasSize, content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return;
            }
        }

        private static PageFrameContent? GetSelectedPageFrameContent()
        {
            var box = PageFrameBoxPresenter.Current.View;
            if (box is null) return null;

            var pageFrameContent = box.GetSelectedPageFrameContent();
            if (pageFrameContent is null) return null;

            return pageFrameContent;
        }

        private static Size GetContentRenderSize(bool ignoreScale)
        {
            var box = PageFrameBoxPresenter.Current.View;
            if (box is null) return Size.Empty;

            var pageFrameContent = box.GetSelectedPageFrameContent();
            if (pageFrameContent is null) return Size.Empty;

            LocalDebug.WriteLine($"GetContentRenderSize(): PageFrameContent={pageFrameContent}");

            var size = pageFrameContent.GetContentRect().Size;
            var scale = ignoreScale ? pageFrameContent.Transform.Scale : 1.0;
            if (scale < 0.001) return Size.Empty;

            return new Size(size.Width / scale, size.Height / scale);
        }


        #region ContextMenu

        private ViewContent? _contextMenuTargetViewContent;

        /// <summary>
        /// マウス位置のページを取得する
        /// </summary>
        public ViewContent? GetViewContentAtMousePosition()
        {
            var box = _vm?.PageFrameBoxPresenter.View;
            if (box is null) return null;

            var point = Mouse.GetPosition(box);
            if (point.X < 0.0 || point.Y < 0.0 || point.X >= box.ActualWidth || point.Y >= box.ActualHeight) return null;

            return box.GetViewContentAt(point);
        }

        /// <summary>
        /// コンテキストメニューで選択されたページを取得する
        /// </summary>
        /// <remarks>
        /// コンテキストメニューが開いていない場合は null
        /// </remarks>
        public ViewContent? GetContextMenuTargetViewContent()
        {
            if (_vm?.ContextMenu.IsOpen != true) return null;
            return _contextMenuTargetViewContent;
        }

        /// <summary>
        /// 参照表示の位置計算用の表示領域サイズを取得する
        /// </summary>
        public Size GetViewCanvasSize()
        {
            return new Size(this.MainViewCanvas.ActualWidth, this.MainViewCanvas.ActualHeight);
        }

        public void OpenContextMenu()
        {
            if (this.MainViewPanel.ContextMenu != null)
            {
                UpdateContextMenuTarget();
                _vm?.UpdateContextMenu();
                this.MainViewPanel.ContextMenu.DataContext = _vm;
                this.MainViewPanel.ContextMenu.PlacementTarget = this.MainViewPanel;
                this.MainViewPanel.ContextMenu.Placement = PlacementMode.MousePoint;
                this.MainViewPanel.ContextMenu.IsOpen = true;
            }
        }

        private void MainViewPanel_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            UpdateContextMenuTarget();
            _vm?.UpdateContextMenu();
        }

        private void UpdateContextMenuTarget()
        {
            _contextMenuTargetViewContent = GetViewContentAtMousePosition();
        }

        private void ContextMenu_Closed(object? sender, RoutedEventArgs e)
        {
            _contextMenuTargetViewContent = null;
        }

        private void PinnedPageCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: PageLockItem item })
            {
                NeeView.PageLock.Current.UnlockItem(item);
            }
        }

        private void PageLock_LayoutChanged(object? sender, EventArgs e)
        {
            UpdatePageLockLayout();
        }

        private void PageLock_LockChanged(object? sender, EventArgs e)
        {
            _vm?.PageFrameBoxPresenter.View?.RefreshPageLockFilter();
        }

        /// <summary>
        /// 参照区のレイアウトを表示行に反映する
        /// </summary>
        private void UpdatePageLockLayout()
        {
            var rowScale = NeeView.PageLock.Current.RowScale;
            this.DisplayRow.LayoutTransform = Math.Abs(rowScale - 1.0) < 0.0001 ? null : new ScaleTransform(rowScale, rowScale);
        }

        private void MainViewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            NeeView.PageLock.Current.SetViewSize(e.NewSize);
        }

        #endregion ContextMenu

        #region SizeChanged

        private readonly System.Threading.Lock _windowSizeChangedLock = new();
        private bool _isResizeLocked;

        public void SetResizeLock(bool locked)
        {
            lock (_windowSizeChangedLock)
            {
                if (_isResizeLocked == locked) return;
                _isResizeLocked = locked;
            }
        }

        private void MainView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var window = Window.GetWindow(this);
            //var windowStateManager = ((window as IHasWindowController)?.WindowController.WindowStateManager) ?? throw new InvalidOperationException();

            // 最小化では処理しない
            if (window.WindowState == WindowState.Minimized) return;

            bool isResizeLocked;
            lock (_windowSizeChangedLock)
            {
                isResizeLocked = _isResizeLocked;
            }

            if (!isResizeLocked)
            {
                //Debug.WriteLine($"ViewSizeChange: {windowStateManager.CurrentState} {e.NewSize} ");
            }
            else
            {
                //Debug.WriteLine($"ViewSizeChange: Locked");
            }
        }

        #endregion SizeChanged

    }
}
