using Avalonia.Controls;
using FgoAuto.Controls;
using FgoAuto.ViewModels;

namespace FgoAuto.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnPreviewSelectionUpdated(object? sender, SelectionRegionEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.UpdateTemplateSelectionHint(e.Left, e.Top, e.Width, e.Height);
    }

    private void OnPreviewSelectionCompleted(object? sender, SelectionRegionEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.CreateTemplateFromSelection(e.Left, e.Top, e.Width, e.Height);
    }

    private async void OnPreviewTouchUpdated(object? sender, EmulatorTouchEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await viewModel.SendPreviewTouchAsync(e.X, e.Y, e.Pressure);
    }

    private void OnPreviewFrameRendered(object? sender, PreviewFrameRenderedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.ReportPreviewRendered(e.Sequence, e.RenderedAtTimestamp);
    }
}
