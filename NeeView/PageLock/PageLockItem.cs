using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace NeeView
{
    /// <summary>
    /// ページ固定の参照項目
    /// </summary>
    /// <remarks>
    /// 固定したページのスナップショットと表示情報を保持する。
    /// 参照区の並び順はコレクションの順序(固定順)で表現する。
    /// </remarks>
    public class PageLockItem : ObservableObject
    {
        private bool _isSourceBookVisible;
        private double _cellLength;
        private double _cellWidth = double.NaN;
        private double _cellHeight = double.NaN;
        private bool _isCurrentPage;

        public PageLockItem(Page page, ImageSource imageSource, Size imageSize, string pageLabel, string sourceBookPath, string sourceBookLabel)
        {
            Page = page;
            ImageSource = imageSource;
            ImageSize = imageSize;
            PageLabel = pageLabel;
            SourceBookPath = sourceBookPath;
            SourceBookLabel = sourceBookLabel;
        }

        /// <summary>
        /// 固定元のページ
        /// </summary>
        public Page Page { get; }

        /// <summary>
        /// 固定ページのスナップショット
        /// </summary>
        public ImageSource ImageSource { get; }

        /// <summary>
        /// スナップショットのピクセルサイズ(自動回転適用済)
        /// </summary>
        public Size ImageSize { get; }

        /// <summary>
        /// 固定ページの表示名
        /// </summary>
        public string PageLabel { get; }

        /// <summary>
        /// 固定元のブックパス
        /// </summary>
        public string SourceBookPath { get; }

        /// <summary>
        /// 固定元のブック名
        /// </summary>
        public string SourceBookLabel { get; }

        /// <summary>
        /// 固定元のブック名を表示するか?
        /// </summary>
        public bool IsSourceBookVisible
        {
            get => _isSourceBookVisible;
            set
            {
                if (_isSourceBookVisible != value)
                {
                    _isSourceBookVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 参照区での長さ(横並び:幅、縦並び:高さ)
        /// </summary>
        public double CellLength
        {
            get => _cellLength;
            set
            {
                if (_cellLength != value)
                {
                    _cellLength = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 参照格の幅 (NaN なら自動)
        /// </summary>
        public double CellWidth
        {
            get => _cellWidth;
            set
            {
                if (_cellWidth != value && !(double.IsNaN(_cellWidth) && double.IsNaN(value)))
                {
                    _cellWidth = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 参照格の高さ (NaN なら自動)
        /// </summary>
        public double CellHeight
        {
            get => _cellHeight;
            set
            {
                if (_cellHeight != value && !(double.IsNaN(_cellHeight) && double.IsNaN(value)))
                {
                    _cellHeight = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 現在ページ?
        /// </summary>
        public bool IsCurrentPage
        {
            get => _isCurrentPage;
            set
            {
                if (_isCurrentPage != value)
                {
                    _isCurrentPage = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// ページの同一性判定
        /// </summary>
        public bool IsSamePage(Page? page)
        {
            return page is not null && Page == page;
        }
    }
}
