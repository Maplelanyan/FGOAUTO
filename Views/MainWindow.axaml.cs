using Avalonia.Controls;
using FgoAuto.Controls;
using FgoAuto.ViewModels;

namespace FgoAuto.Views;

public partial class MainWindow : Window
{
    private ScreenshotPreview? _activeTouchPreview;

    public MainWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => _activeTouchPreview?.CancelTouch();
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

    private void OnPreviewTouchUpdated(object? sender, EmulatorTouchEventArgs e)
    {
        _activeTouchPreview = e.Pressure > 0 ? sender as ScreenshotPreview : null;
        if (DataContext is MainViewModel viewModel)
            viewModel.QueuePreviewTouch(e.X, e.Y, e.Pressure);
    }

    private void OnPreviewFrameRendered(object? sender, PreviewFrameRenderedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.ReportPreviewRendered(e.Sequence, e.RenderedAtTimestamp);
    }
}
