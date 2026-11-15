using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoAuto.Models;

namespace FgoAuto.ViewModels;

public partial class MainViewModel
{
    private volatile bool _previewDisposed;
    private readonly Stopwatch _previewFpsClock = new();
    private readonly object _previewMetricsLock = new();
    private readonly Queue<double> _renderIntervals = new(240);
    private DispatcherTimer? _previewTimer;
    private int _previewDispatchPending;
    private long _renderedFrames;
    private long _lastRenderedSequence = -1;
    private long _lastRenderedAtTimestamp;
    private long _previousReceivedFrames;
    private long _previousRenderedFrames;
    private long _lastPerformanceLogTimestamp;
    private int _previewNativeWidth;
    private int _previewNativeHeight;
    private long _loggedReceivedFrames, _loggedRenderedFrames;
    private long _longRenderIntervals;
    private double _maxRenderIntervalMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedPreviewImage))]
    public partial WriteableBitmap? LivePreviewImage { get; set; }

    [ObservableProperty]
    public partial long LivePreviewFrameNumber { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewStatusText))]
    public partial string LivePreviewStatus { get; set; } = "启动 Android 模拟器后显示实时预览";

    public Bitmap? DisplayedPreviewImage => IsSelectingTemplate ? PreviewImage : LivePreviewImage ?? PreviewImage;
    public string PreviewStatusText => IsSelectingTemplate ? "画面已固定，框选使用原始分辨率；完成后恢复实时预览" : LivePreviewStatus;

    partial void InitializePreview()
    {
        _previewFpsClock.Start();
        _lastPerformanceLogTimestamp = Stopwatch.GetTimestamp();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _previewTimer.Tick += OnPreviewTick;
        _previewTimer.Start();
    }

    private void OnEmulatorFrameAvailable()
    {
        if (_previewDisposed || IsSelectingTemplate || Interlocked.CompareExchange(ref _previewDispatchPending, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { RenderLatestEmulatorFrame(); }
            finally
            {
                Interlocked.Exchange(ref _previewDispatchPending, 0);
                // A final frame can arrive while the bitmap is copied. Schedule at most
                // one successor so that it does not wait for the statistics timer.
                if (!_previewDisposed && !IsSelectingTemplate && _emulator.HasPendingPreviewFrame)
                    OnEmulatorFrameAvailable();
            }
        }, DispatcherPriority.Render);
    }

    private void RenderLatestEmulatorFrame()
    {
        if (_previewDisposed || IsSelectingTemplate) return;
        using var frame = _emulator.TakeLatestPreviewFrame();
        if (frame is null) return;

        if (LivePreviewImage is null || LivePreviewImage.PixelSize != new PixelSize(frame.Width, frame.Height))
        {
            ClearVideoBitmap();
            LivePreviewImage = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var buffer = LivePreviewImage.Lock())
        {
            if (buffer.RowBytes == frame.RowBytes)
                Marshal.Copy(frame.Pixels, 0, buffer.Address, frame.RowBytes * frame.Height);
            else
                for (var y = 0; y < frame.Height; y++)
                    Marshal.Copy(frame.Pixels, y * frame.RowBytes, buffer.Address + y * buffer.RowBytes, frame.RowBytes);
        }
        _previewNativeWidth = frame.NativeWidth;
        _previewNativeHeight = frame.NativeHeight;
        LivePreviewFrameNumber = frame.Sequence;
        ScreenSizeText = $"{frame.Width} × {frame.Height}";
    }

    /// <summary>Records a distinct frame submitted by the preview control's DrawImage call.</summary>
    public void ReportPreviewRendered(long sequence, long renderedAtTimestamp)
    {
        if (_previewDisposed || IsSelectingTemplate || sequence <= 0) return;
        lock (_previewMetricsLock)
        {
            if (sequence == _lastRenderedSequence) return;
            if (_lastRenderedAtTimestamp > 0 && renderedAtTimestamp > _lastRenderedAtTimestamp)
            {
                var interval = Stopwatch.GetElapsedTime(_lastRenderedAtTimestamp, renderedAtTimestamp).TotalMilliseconds;
                if (interval > 50) _longRenderIntervals++;
                _maxRenderIntervalMs = Math.Max(_maxRenderIntervalMs, interval);
                if (_renderIntervals.Count == 240) _renderIntervals.Dequeue();
                _renderIntervals.Enqueue(interval);
            }
            _lastRenderedSequence = sequence;
            _lastRenderedAtTimestamp = renderedAtTimestamp;
            _renderedFrames++;
        }
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_previewDisposed) return;
        RenderLatestEmulatorFrame();
        var elapsedSeconds = _previewFpsClock.Elapsed.TotalSeconds;
        if (elapsedSeconds < 0.5) return;

        var received = _emulator.FrameStatistics.GetSnapshot();
        long renderedFrames;
        double renderIntervalP95;
        double maxRenderInterval;
        long longRenderIntervals;
        lock (_previewMetricsLock)
        {
            renderedFrames = _renderedFrames;
            var intervals = _renderIntervals.ToArray();
            Array.Sort(intervals);
            renderIntervalP95 = intervals.Length == 0 ? 0 : intervals[(int)Math.Ceiling(intervals.Length * 0.95) - 1];
            maxRenderInterval = _maxRenderIntervalMs;
            longRenderIntervals = _longRenderIntervals;
        }
        var receivedInPeriod = received.ReceivedFrames >= _previousReceivedFrames
            ? received.ReceivedFrames - _previousReceivedFrames : received.ReceivedFrames;
        var receiveFps = receivedInPeriod / elapsedSeconds;
        var drawFps = (renderedFrames - _previousRenderedFrames) / elapsedSeconds;
        var now = Stopwatch.GetTimestamp();
        var ageMs = received.LastReceivedAtTimestamp > 0
            ? Stopwatch.GetElapsedTime(received.LastReceivedAtTimestamp, now).TotalMilliseconds : 0;
        if (!IsEmulatorRunning)
        {
            LivePreviewStatus = "Android 模拟器未运行";
            _lastPerformanceLogTimestamp = now;
            _loggedReceivedFrames = received.ReceivedFrames;
            _loggedRenderedFrames = renderedFrames;
        }
        else if (!_emulator.HasLiveScreenshotStream)
            LivePreviewStatus = "模拟器截图流中断，请重启模拟器";
        else if (LivePreviewImage is { } image)
        {
            var waiting = receivedInPeriod == 0 && ageMs >= 1000 ? "等待新画面 · " : string.Empty;
            LivePreviewStatus = $"{waiting}接收 {receiveFps:F1} / 绘制 {drawFps:F1} 帧/秒 · {image.PixelSize.Width} × {image.PixelSize.Height}"
                + $" · P95 间隔 {received.ReceiveIntervalP95Ms:F1} / {renderIntervalP95:F1} ms · 距新帧 {ageMs:F0} ms";
            if (!IsSelectingTemplate && Stopwatch.GetElapsedTime(_lastPerformanceLogTimestamp, now).TotalSeconds >= 5)
            {
                var logSeconds = Stopwatch.GetElapsedTime(_lastPerformanceLogTimestamp, now).TotalSeconds;
                var receivedCount = received.ReceivedFrames >= _loggedReceivedFrames ? received.ReceivedFrames - _loggedReceivedFrames : received.ReceivedFrames;
                var loggedReceiveFps = receivedCount / logSeconds;
                var loggedDrawFps = (renderedFrames - _loggedRenderedFrames) / logSeconds;
                AddLog($"预览性能（近 {logSeconds:F1}s）：接收 {loggedReceiveFps:F1} / 绘制提交 {loggedDrawFps:F1} 帧/秒；{image.PixelSize.Width} × {image.PixelSize.Height}；"
                    + $"接收/绘制间隔 P95 {received.ReceiveIntervalP95Ms:F1}/{renderIntervalP95:F1} ms；距上次收帧 {ageMs:F0} ms；源序号缺口累计 {received.SourceSkippedFrames}。"
                    + $" 会话最大新帧间隔 {received.MaxReceiveIntervalMs:F1}/{maxRenderInterval:F1} ms；超过 50ms 次数 {received.LongReceiveIntervals}/{longRenderIntervals}（含静止间隔）。"
                    + (waiting.Length > 0 ? " 等待新画面，静止时无新帧不代表卡顿。" : string.Empty));
                _loggedReceivedFrames = received.ReceivedFrames;
                _loggedRenderedFrames = renderedFrames;
                _lastPerformanceLogTimestamp = now;
            }
        }
        else
            LivePreviewStatus = "正在等待模拟器首帧…";
        _previousReceivedFrames = received.ReceivedFrames;
        _previousRenderedFrames = renderedFrames;
        _previewFpsClock.Restart();
    }

    partial void OnIsSelectingTemplateChanged(bool value)
    {
        // A fixed selection image is excluded from live drawing statistics.
        lock (_previewMetricsLock)
        {
            _lastRenderedAtTimestamp = 0;
            _renderIntervals.Clear();
            _maxRenderIntervalMs = 0;
            _longRenderIntervals = 0;
        }
        if (!value)
            RenderLatestEmulatorFrame();
    }

    private void ClearVideoBitmap()
    {
        var previous = LivePreviewImage;
        LivePreviewImage = null;
        LivePreviewFrameNumber = 0;
        lock (_previewMetricsLock)
        {
            _lastRenderedSequence = -1;
            _lastRenderedAtTimestamp = 0;
            _renderIntervals.Clear();
            _maxRenderIntervalMs = 0;
            _longRenderIntervals = 0;
        }
        previous?.Dispose();
    }

    private void DisposePreview()
    {
        if (_previewDisposed) return;
        _previewDisposed = true;
        _previewTimer?.Stop();
        if (_previewTimer is not null) _previewTimer.Tick -= OnPreviewTick;
        ClearVideoBitmap();
        var snapshot = PreviewImage;
        PreviewImage = null;
        snapshot?.Dispose();
    }
}
