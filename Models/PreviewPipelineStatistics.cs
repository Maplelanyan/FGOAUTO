using System.Diagnostics;

namespace FgoAuto.Models;

public sealed record PreviewPipelineStatisticsSnapshot(TimingStatisticsSnapshot Conversion,
    TimingStatisticsSnapshot ReceiveToPreview, TimingStatisticsSnapshot PreviewToUi,
    TimingStatisticsSnapshot UiCopy, TimingStatisticsSnapshot ReceiveToDraw);

/// <summary>
/// Bounded five-second observations of work AFTER a complete frame arrives.
/// These timings exclude emulator rendering/readback and compositor presentation.
/// </summary>
public sealed class PreviewPipelineStatistics
{
    private readonly object _sync = new();
    private readonly RecentTimingSamples _conversion = new(), _receiveToPreview = new(),
        _previewToUi = new(), _uiCopy = new(), _receiveToDraw = new();

    public void RecordConversion(GameFrame frame, long startedAt, long completedAt)
    {
        lock (_sync)
        {
            Record(_conversion, startedAt, completedAt);
            Record(_receiveToPreview, frame.ReceivedAtTimestamp, completedAt);
        }
    }

    public void RecordUiCopy(long readyAt, long startedAt, long completedAt)
    {
        lock (_sync)
        {
            Record(_previewToUi, readyAt, startedAt);
            Record(_uiCopy, startedAt, completedAt);
        }
    }

    public void RecordDraw(long receivedAt, long submittedAt)
    {
        lock (_sync) Record(_receiveToDraw, receivedAt, submittedAt);
    }

    public PreviewPipelineStatisticsSnapshot GetSnapshot(long nowTimestamp = 0)
    {
        if (nowTimestamp == 0) nowTimestamp = Stopwatch.GetTimestamp();
        lock (_sync)
            return new(_conversion.GetSnapshot(nowTimestamp), _receiveToPreview.GetSnapshot(nowTimestamp),
                _previewToUi.GetSnapshot(nowTimestamp), _uiCopy.GetSnapshot(nowTimestamp), _receiveToDraw.GetSnapshot(nowTimestamp));
    }

    public void Reset()
    {
        lock (_sync)
        {
            _conversion.Clear(); _receiveToPreview.Clear(); _previewToUi.Clear();
            _uiCopy.Clear(); _receiveToDraw.Clear();
        }
    }

    private static void Record(RecentTimingSamples samples, long startedAt, long completedAt)
    {
        // Zero denotes unavailable timing (e.g. synthetic or legacy preview frames).
        if (startedAt > 0 && completedAt >= startedAt)
            samples.Record(Stopwatch.GetElapsedTime(startedAt, completedAt).TotalMilliseconds, completedAt);
    }
}
