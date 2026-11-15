using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Diagnostics;

namespace FgoAuto.Controls;

public sealed class SelectionRegionEventArgs(int left, int top, int width, int height) : EventArgs
{
    public int Left { get; } = left;
    public int Top { get; } = top;
    public int Width { get; } = width;
    public int Height { get; } = height;
}

public sealed class EmulatorTouchEventArgs(int x, int y, int pressure) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
    public int Pressure { get; } = pressure;
}

public sealed class PreviewFrameRenderedEventArgs(long sequence, long renderedAtTimestamp) : EventArgs
{
    public long Sequence { get; } = sequence;
    public long RenderedAtTimestamp { get; } = renderedAtTimestamp;
}

public sealed class ScreenshotPreview : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ScreenshotPreview, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<bool> IsSelectingProperty =
        AvaloniaProperty.Register<ScreenshotPreview, bool>(nameof(IsSelecting));

    public static readonly StyledProperty<long> FrameNumberProperty =
        AvaloniaProperty.Register<ScreenshotPreview, long>(nameof(FrameNumber));

    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<ScreenshotPreview, bool>(nameof(IsInteractive));

    private PixelPoint? _dragStart;
    private PixelRegion? _selection;
    private long _lastRenderedFrame = -1;
    private bool _touchActive;
    private PixelPoint _lastTouchPoint;
    private long _lastTouchTimestamp;

    public long FrameNumber { get => GetValue(FrameNumberProperty); set => SetValue(FrameNumberProperty, value); }
    public bool IsInteractive { get => GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }
    public long RenderedFrameCount { get; private set; }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsSelecting
    {
        get => GetValue(IsSelectingProperty);
        set => SetValue(IsSelectingProperty, value);
    }

    public event EventHandler<SelectionRegionEventArgs>? SelectionUpdated;
    public event EventHandler<SelectionRegionEventArgs>? SelectionCompleted;
    public event EventHandler<EmulatorTouchEventArgs>? TouchUpdated;
    /// <summary>Raised when a distinct live frame is submitted to DrawImage, before compositor presentation.</summary>
    public event EventHandler<PreviewFrameRenderedEventArgs>? FrameRendered;

    static ScreenshotPreview()
    {
        AffectsRender<ScreenshotPreview>(SourceProperty, IsSelectingProperty, FrameNumberProperty, IsInteractiveProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameNumberProperty && FrameNumber <= 0)
            _lastRenderedFrame = -1;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bitmap = Source;
        if (bitmap is null)
            return;

        var destination = GetDestinationRect(bitmap);
        if (destination.Width <= 0 || destination.Height <= 0)
            return;

        var source = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        context.DrawImage(bitmap, source, destination);
        if (!IsSelecting && FrameNumber > 0 && _lastRenderedFrame != FrameNumber)
        {
            _lastRenderedFrame = FrameNumber;
            RenderedFrameCount++;
            FrameRendered?.Invoke(this, new PreviewFrameRenderedEventArgs(FrameNumber, Stopwatch.GetTimestamp()));
        }

        if (_selection is not { } selection)
            return;

        var scaleX = destination.Width / bitmap.PixelSize.Width;
        var scaleY = destination.Height / bitmap.PixelSize.Height;
        var selectionRect = new Rect(
            destination.X + selection.Left * scaleX,
            destination.Y + selection.Top * scaleY,
            selection.Width * scaleX,
            selection.Height * scaleY);
        var fill = new SolidColorBrush(Color.FromArgb(62, 34, 211, 238));
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(34, 211, 238)), 2);
        context.DrawRectangle(fill, outline, selectionRect, 3, 3);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Source is not { } bitmap)
            return;

        var position = e.GetPosition(this);
        if (!TryMapToSource(bitmap, position, clamp: false, out var point))
            return;

        if (!IsSelecting)
        {
            if (!IsInteractive)
                return;
            _touchActive = true;
            _lastTouchPoint = point;
            _lastTouchTimestamp = Stopwatch.GetTimestamp();
            e.Pointer.Capture(this);
            TouchUpdated?.Invoke(this, new EmulatorTouchEventArgs(point.X, point.Y, 1));
            e.Handled = true;
            return;
        }

        _dragStart = point;
        _selection = new PixelRegion(point.X, point.Y, 1, 1);
        e.Pointer.Capture(this);
        RaiseSelectionUpdated(_selection.Value);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_touchActive)
        {
            if (Source is { } touchBitmap && IsInteractive
                && TryMapToSource(touchBitmap, e.GetPosition(this), clamp: true, out var touchPoint))
            {
                var elapsed = Stopwatch.GetElapsedTime(_lastTouchTimestamp);
                var moved = Math.Abs(touchPoint.X - _lastTouchPoint.X) + Math.Abs(touchPoint.Y - _lastTouchPoint.Y) >= 2;
                if (moved && elapsed >= TimeSpan.FromMilliseconds(16))
                {
                    _lastTouchPoint = touchPoint;
                    _lastTouchTimestamp = Stopwatch.GetTimestamp();
                    TouchUpdated?.Invoke(this, new EmulatorTouchEventArgs(touchPoint.X, touchPoint.Y, 1));
                }
            }
            e.Handled = true;
            return;
        }
        if (_dragStart is not { } start || Source is not { } bitmap ||
            !TryMapToSource(bitmap, e.GetPosition(this), clamp: true, out var point))
            return;

        _selection = PixelRegion.FromPoints(start, point);
        RaiseSelectionUpdated(_selection.Value);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_touchActive)
        {
            if (Source is { } touchBitmap && TryMapToSource(touchBitmap, e.GetPosition(this), clamp: true, out var touchPoint))
                TouchUpdated?.Invoke(this, new EmulatorTouchEventArgs(touchPoint.X, touchPoint.Y, 0));
            else
                TouchUpdated?.Invoke(this, new EmulatorTouchEventArgs(_lastTouchPoint.X, _lastTouchPoint.Y, 0));
            _touchActive = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_dragStart is not { } start || Source is not { } bitmap)
            return;

        var hasEnd = TryMapToSource(bitmap, e.GetPosition(this), clamp: true, out var end);
        e.Pointer.Capture(null);
        _dragStart = null;
        var region = hasEnd ? PixelRegion.FromPoints(start, end) : _selection;
        _selection = null;
        InvalidateVisual();

        if (region is { } completed)
            SelectionCompleted?.Invoke(this, new SelectionRegionEventArgs(completed.Left, completed.Top, completed.Width, completed.Height));
        e.Handled = true;
    }

    private void RaiseSelectionUpdated(PixelRegion region) =>
        SelectionUpdated?.Invoke(this, new SelectionRegionEventArgs(region.Left, region.Top, region.Width, region.Height));

    private Rect GetDestinationRect(Bitmap bitmap)
    {
        var available = Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0 || bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            return new Rect(0, 0, 0, 0);

        var scale = Math.Min(available.Width / bitmap.PixelSize.Width, available.Height / bitmap.PixelSize.Height);
        var width = bitmap.PixelSize.Width * scale;
        var height = bitmap.PixelSize.Height * scale;
        return new Rect((available.Width - width) / 2, (available.Height - height) / 2, width, height);
    }

    private bool TryMapToSource(Bitmap bitmap, Point position, bool clamp, out PixelPoint pixel)
    {
        pixel = default;
        var destination = GetDestinationRect(bitmap);
        if (destination.Width <= 0 || destination.Height <= 0)
            return false;

        if (!clamp && !destination.Contains(position))
            return false;

        var x = (position.X - destination.X) * bitmap.PixelSize.Width / destination.Width;
        var y = (position.Y - destination.Y) * bitmap.PixelSize.Height / destination.Height;
        pixel = new PixelPoint(
            Math.Clamp((int)Math.Floor(x), 0, bitmap.PixelSize.Width - 1),
            Math.Clamp((int)Math.Floor(y), 0, bitmap.PixelSize.Height - 1));
        return true;
    }

    private readonly record struct PixelPoint(int X, int Y);

    private readonly record struct PixelRegion(int Left, int Top, int Width, int Height)
    {
        public static PixelRegion FromPoints(PixelPoint first, PixelPoint second)
        {
            var left = Math.Min(first.X, second.X);
            var top = Math.Min(first.Y, second.Y);
            return new PixelRegion(left, top, Math.Abs(second.X - first.X) + 1, Math.Abs(second.Y - first.Y) + 1);
        }
    }
}
