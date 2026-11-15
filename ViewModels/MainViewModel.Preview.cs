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
    private readonly FrameStatistics _drawStatistics = new();
    private DispatcherTimer? _previewTimer;
    private int _previewDispatchPending;
    private long _lastRenderedSequence = -1;
    private long _uploadedFrameSequence = -1, _uploadedFrameReceivedAt;
    private long _previousReceivedFrames;
    private long _previousRenderedFrames;
    private long _lastPerformanceLogTimestamp;
    private int _previewNativeWidth;
    private int _previewNativeHeight;
    private long _loggedReceivedFrames, _loggedRenderedFrames;
    private long _loggedAllocatedBytes;
    private TimeSpan _loggedGcPause;
    private int _loggedGen2Collections;

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
        ResetProcessMetricsBaseline();
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

        var copyStartedAt = Stopwatch.GetTimestamp();
        if (LivePreviewImage is null || LivePreviewImage.PixelSize != new PixelSize(frame.Width, frame.Height))
        {
            ClearVideoBitmap();
            LivePreviewImage = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            ScreenSizeText = $"{frame.Width} × {frame.Height}";
        }
        using (var buffer = LivePreviewImage.Lock())
        {
            if (buffer.RowBytes == frame.RowBytes)
                Marshal.Copy(frame.Pixels, 0, buffer.Address, frame.RowBytes * frame.Height);
            else
                for (var y = 0; y < frame.Height; y++)
                    Marshal.Copy(frame.Pixels, y * frame.RowBytes, buffer.Address + y * buffer.RowBytes, frame.RowBytes);
        }
        _emulator.PreviewStatistics.RecordUiCopy(frame.PreviewReadyAtTimestamp,
            copyStartedAt, Stopwatch.GetTimestamp());
        _previewNativeWidth = frame.NativeWidth;
        _previewNativeHeight = frame.NativeHeight;
        lock (_previewMetricsLock)
        {
            _uploadedFrameSequence = frame.Sequence;
            _uploadedFrameReceivedAt = frame.ReceivedAtTimestamp;
        }
        LivePreviewFrameNumber = frame.Sequence;
    }

    /// <summary>Records a distinct frame submitted by the preview control's DrawImage call.</summary>
    public void ReportPreviewRendered(long sequence, long renderedAtTimestamp)
    {
        if (_previewDisposed || IsSelectingTemplate || sequence <= 0) return;
        lock (_previewMetricsLock)
        {
            if (sequence == _lastRenderedSequence) return;
            _lastRenderedSequence = sequence;
            _drawStatistics.Record(sequence, renderedAtTimestamp);
            if (sequence == _uploadedFrameSequence)
                _emulator.PreviewStatistics.RecordDraw(_uploadedFrameReceivedAt, renderedAtTimestamp);
        }
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_previewDisposed) return;
        RenderLatestEmulatorFrame();
        var elapsedSeconds = _previewFpsClock.Elapsed.TotalSeconds;
        if (elapsedSeconds < 0.5) return;

        var now = Stopwatch.GetTimestamp();
        var received = _emulator.FrameStatistics.GetSnapshot(now);
        var drawn = _drawStatistics.GetSnapshot(now);
        var renderedFrames = drawn.ReceivedFrames;
        var receivedInPeriod = received.ReceivedFrames >= _previousReceivedFrames
            ? received.ReceivedFrames - _previousReceivedFrames : received.ReceivedFrames;
        var receiveFps = receivedInPeriod / elapsedSeconds;
        var drawFps = Math.Max(0, renderedFrames - _previousRenderedFrames) / elapsedSeconds;
        var ageMs = received.LastReceivedAtTimestamp > 0
            ? Stopwatch.GetElapsedTime(received.LastReceivedAtTimestamp, now).TotalMilliseconds : 0;
        if (!IsEmulatorRunning)
        {
            LivePreviewStatus = "Android 模拟器未运行";
            _lastPerformanceLogTimestamp = now;
            _loggedReceivedFrames = received.ReceivedFrames;
            _loggedRenderedFrames = renderedFrames;
            ResetProcessMetricsBaseline();
        }
        else if (!_emulator.HasLiveScreenshotStream)
            LivePreviewStatus = "模拟器截图流中断，请重启模拟器";
        else if (LivePreviewImage is { } image)
        {
            var waiting = receivedInPeriod == 0 && ageMs >= 1000 ? "等待新画面 · " : string.Empty;
            var intervals = received.WindowIntervalSamples > 0 && drawn.WindowIntervalSamples > 0
                ? $"{received.ReceiveIntervalP95Ms:F1}/{drawn.ReceiveIntervalP95Ms:F1} ms" : "暂无样本";
            LivePreviewStatus = $"{waiting}接收 {receiveFps:F1} / 绘制提交 {drawFps:F1} 帧/秒 · {image.PixelSize.Width} × {image.PixelSize.Height}"
                + $" · 近5s间隔P95 {intervals} · 距新帧 {ageMs:F0} ms";
            if (!IsSelectingTemplate && Stopwatch.GetElapsedTime(_lastPerformanceLogTimestamp, now).TotalSeconds >= 5)
            {
                var logSeconds = Stopwatch.GetElapsedTime(_lastPerformanceLogTimestamp, now).TotalSeconds;
                var receivedCount = received.ReceivedFrames >= _loggedReceivedFrames ? received.ReceivedFrames - _loggedReceivedFrames : received.ReceivedFrames;
                var loggedReceiveFps = receivedCount / logSeconds;
                var loggedDrawFps = (renderedFrames - _loggedRenderedFrames) / logSeconds;
                var stages = _emulator.PreviewStatistics.GetSnapshot(now);
                var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
                var gcPause = GC.GetTotalPauseDuration();
                var gen2Collections = GC.CollectionCount(2);
                var touch = _previewTouches.GetSnapshot();
                var sourceDelivery = received.SourceTimestampSamples > 0
                    ? $"{received.EstimatedGenerationToReceiveP95Ms:F1} ms/{received.SourceTimestampSamples}样本"
                    : "暂无有效源时间戳样本";
                AddLog($"预览性能（近 {logSeconds:F1}s）：接收 {loggedReceiveFps:F1} / 绘制提交 {loggedDrawFps:F1} 帧/秒；{image.PixelSize.Width} × {image.PixelSize.Height}；"
                    + $"近5s接收/绘制间隔 P95 {intervals}（样本 {received.WindowIntervalSamples}/{drawn.WindowIntervalSamples}）；距上次收帧 {ageMs:F0} ms；源序号缺口累计 {received.SourceSkippedFrames}。"
                    + $" 模拟器估计图像生成至收到完整帧P95 {sourceDelivery}（近5s，非游戏源帧时间）。"
                    + $" 收帧后耗时P95：转换 {FormatTiming(stages.Conversion)}，收帧至预览就绪 {FormatTiming(stages.ReceiveToPreview)}，UI等待 {FormatTiming(stages.PreviewToUi)}，位图复制 {FormatTiming(stages.UiCopy)}，收帧至绘制提交 {FormatTiming(stages.ReceiveToDraw)}（近5s，不含模拟器渲染/截图和显示器呈现）。"
                    + $" 进程分配 {Math.Max(0, allocatedBytes - _loggedAllocatedBytes) / logSeconds / 1_000_000d:F1} MB/s，GC暂停 {Math.Max(0, (gcPause - _loggedGcPause).TotalMilliseconds):F1} ms，Gen2 {Math.Max(0, gen2Collections - _loggedGen2Collections)} 次。"
                    + $" 触控RPC P95 {touch.RpcP95Ms:F1} ms（最近120次），待处理手势 {touch.PendingGestures}，累计合并移动 {touch.CoalescedMoves}，丢弃过期/超额手势 {touch.DiscardedGestures}。"
                    + $" 会话最大新帧间隔 {received.MaxReceiveIntervalMs:F1}/{drawn.MaxReceiveIntervalMs:F1} ms；超过50ms次数 {received.LongReceiveIntervals}/{drawn.LongReceiveIntervals}；以上新帧速率与间隔含静止时间，不是游戏FPS。"
                    + (waiting.Length > 0 ? " 等待新画面，静止时无新帧不代表卡顿。" : string.Empty));
                _loggedAllocatedBytes = allocatedBytes;
                _loggedGcPause = gcPause;
                _loggedGen2Collections = gen2Collections;
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
        _drawStatistics.ResetIntervals();
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
            _uploadedFrameSequence = -1;
            _uploadedFrameReceivedAt = 0;
            _drawStatistics.ResetIntervals();
        }
        previous?.Dispose();
    }

    private static string FormatTiming(TimingStatisticsSnapshot timing) =>
        timing.Samples > 0 ? $"{timing.P95Ms:F1} ms/{timing.Samples}样本" : "暂无样本";

    private void ResetProcessMetricsBaseline()
    {
        _loggedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _loggedGcPause = GC.GetTotalPauseDuration();
        _loggedGen2Collections = GC.CollectionCount(2);
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
