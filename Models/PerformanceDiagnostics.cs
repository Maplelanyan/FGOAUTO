using System.Diagnostics;

namespace FgoAuto.Models;

public enum PerformanceDiagnosticMode
{
    Normal,
    PausePreview,
    PauseScreenshotStream,
    PauseRecognition,
    Preview30Fps,
}

/// <summary>Session-only controls. They never change guest settings or saved rules.</summary>
public sealed class PerformanceDiagnostics
{
    private int _mode;
    private long _recognitionEpoch;
    public PerformanceDiagnosticMode Mode => (PerformanceDiagnosticMode)Volatile.Read(ref _mode);
    public long RecognitionEpoch => Interlocked.Read(ref _recognitionEpoch);
    public bool IsScreenshotStreamPaused => Mode == PerformanceDiagnosticMode.PauseScreenshotStream;
    public bool IsRecognitionPaused => Mode is PerformanceDiagnosticMode.PauseScreenshotStream or PerformanceDiagnosticMode.PauseRecognition;
    public bool IsPreviewPaused => Mode is PerformanceDiagnosticMode.PausePreview or PerformanceDiagnosticMode.PauseScreenshotStream;
    public int PreviewFramesPerSecond => Mode == PerformanceDiagnosticMode.Preview30Fps ? 30 : 60;

    internal void SetMode(PerformanceDiagnosticMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Interlocked.Increment(ref _recognitionEpoch);
        Volatile.Write(ref _mode, (int)mode);
    }

    public static string Describe(PerformanceDiagnosticMode mode) => mode switch
    {
        PerformanceDiagnosticMode.Normal => "正常模式",
        PerformanceDiagnosticMode.PausePreview => "暂停预览（保留截图与识别）",
        PerformanceDiagnosticMode.PauseScreenshotStream => "暂停截图流（游戏继续，识别暂停）",
        PerformanceDiagnosticMode.PauseRecognition => "暂停自动识别（保留预览）",
        PerformanceDiagnosticMode.Preview30Fps => "30 FPS 预览（截图与识别频率不变）",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <summary>Limits expensive work before it starts; the caller reads the latest frame AFTER waiting.</summary>
public sealed class PreviewWorkScheduler
{
    private long _nextAt, _intervalTicks;
    public TimeSpan GetDelay(long now, int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        var interval = Math.Max(1, Stopwatch.Frequency / framesPerSecond);
        if (_intervalTicks != interval) { _intervalTicks = interval; _nextAt = 0; }
        return _nextAt > now ? TimeSpan.FromSeconds((double)(_nextAt - now) / Stopwatch.Frequency) : TimeSpan.Zero;
    }
    public void RecordStarted(long now)
    {
        // Preserve the pacing deadline when a Windows timer wakes late, rather
        // than adding another full interval after every overshoot. At most one
        // frame of catch-up is allowed; obsolete frames are never queued.
        _nextAt = _nextAt == 0 ? now + _intervalTicks : Math.Max(now, _nextAt + _intervalTicks);
    }
    public void Reset() { _nextAt = _intervalTicks = 0; }
}
