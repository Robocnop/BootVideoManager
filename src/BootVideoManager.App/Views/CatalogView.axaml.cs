using Avalonia.Controls;
using BootVideoManager.App.ViewModels;

namespace BootVideoManager.App.Views;

public partial class CatalogView : UserControl
{
    /// <summary>Distance from the bottom (in pixels) at which the next page is loaded automatically.</summary>
    private const double InfiniteScrollThreshold = 600;

    public CatalogView()
    {
        InitializeComponent();
        ResultsScroller.ScrollChanged += OnResultsScrolled;
    }

    /// <summary>Infinite scroll: convenient with a mouse wheel, a touch screen or a controller.</summary>
    private void OnResultsScrolled(object? sender, ScrollChangedEventArgs e)
    {
        var remaining = ResultsScroller.Extent.Height - ResultsScroller.Viewport.Height - ResultsScroller.Offset.Y;
        if (remaining < InfiniteScrollThreshold
            && DataContext is CatalogViewModel { CanLoadMore: true } viewModel
            && viewModel.LoadMoreCommand.CanExecute(null))
        {
            viewModel.LoadMoreCommand.Execute(null);
        }
    }
}
