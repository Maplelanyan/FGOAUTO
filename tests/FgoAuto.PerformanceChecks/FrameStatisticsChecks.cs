using System.Diagnostics;
using FgoAuto.Models;

internal static class FrameStatisticsChecks
{
    public static void Run()
    {
        CheckLongGapsAndIdle();
        CheckFixedWindowAndBounds();
        CheckSourceDeliveryTiming();
        CheckStageTiming();
        Console.WriteLine("PASS frame statistics: fixed window, idle/gaps, bounded storage, estimated source delivery, arrival-based stages");
    }

    private static void CheckLongGapsAndIdle()
    {
        var stats = new FrameStatistics();
        stats.Record(0, At(0));
        stats.Record(1, At(16));
        stats.Record(4, At(32));
        stats.Record(5, At(432));
        var busy = stats.GetSnapshot(At(432));
        Require(busy.ReceivedFrames == 4 && busy.SourceSkippedFrames == 2, "Frame/source-gap counts changed.");
        Near(busy.ReceiveIntervalP95Ms, 400, "A long gap must be visible when the next frame arrives.");
        Require(busy.LongReceiveIntervals == 1, "Long interval was hidden.");

        var idle = stats.GetSnapshot(At(6000));
        Require(idle.WindowIntervalSamples == 0 && idle.ReceiveIntervalP95Ms == 0,
            "Old timing samples survived outside the five-second window.");
        Require(idle.ReceivedFrames == 4 && idle.LastReceivedAtTimestamp == At(432),
            "An idle observation manufactured frames or changed the last arrival.");
        Near(idle.MaxReceiveIntervalMs, 400, "Expiring samples lost the session maximum gap.");

        stats.Record(0, At(6016));
        stats.Record(2, At(6032));
        var resumed = stats.GetSnapshot(At(6032));
        Require(resumed.ReceivedFrames == 6 && resumed.SourceSkippedFrames == 3,
            "Source sequence reset corrupted cumulative counts.");
        Near(resumed.MaxReceiveIntervalMs, 5584, "Idle/resume gap was silently discarded.");

        stats.Reset();
        var reset = stats.GetSnapshot(At(6032));
        Require(reset.ReceivedFrames == 0 && reset.SourceSkippedFrames == 0
            && reset.LastReceivedAtTimestamp == 0 && reset.WindowIntervalSamples == 0
            && reset.MaxReceiveIntervalMs == 0, "New session retained previous observations.");
    }

    private static void CheckFixedWindowAndBounds()
    {
        var stats = new FrameStatistics();
        for (var frame = 0; frame <= 10; frame++) stats.Record(frame, At(frame * 100));
        Near(stats.GetSnapshot(At(1000)).ReceiveIntervalP95Ms, 100, "Slow animation interval is incorrect.");
        stats.Record(11, At(6000));
        for (var frame = 1; frame <= 20; frame++) stats.Record(frame + 11, At(6000 + frame * 16));
        var recent = stats.GetSnapshot(At(6320));
        Require(recent.WindowIntervalSamples == 21, "Window is based on frame count rather than elapsed time.");
        Near(recent.ReceiveIntervalP95Ms, 16, "Old slow-animation samples leaked into the new window.");
        Near(recent.MaxReceiveIntervalMs, 5000, "Long gap vanished from session diagnostics.");

        stats.ResetIntervals();
        var before = stats.GetSnapshot(At(6320));
        Require(before.ReceivedFrames == 32 && before.WindowIntervalSamples == 0,
            "Resetting drawing interval history corrupted frame counts.");

        var burst = new FrameStatistics();
        for (var frame = 0; frame < 10_000; frame++) burst.Record(frame, At(frame * 0.1));
        var bounded = burst.GetSnapshot(At(1000));
        Require(bounded.ReceivedFrames == 10_000 && bounded.WindowIntervalSamples is > 0 and <= 2048,
            "Unusually high frame rates grew storage without bounds or lost cumulative counts.");
    }

    private static void CheckSourceDeliveryTiming()
    {
        var stats = new FrameStatistics();
        stats.Record(0, At(100), At(88));
        stats.Record(1, At(116)); // Unknown source time must not become a zero-latency sample.
        stats.Record(2, At(132), At(133)); // A future clock estimate is invalid.
        stats.Record(3, At(148), -1);
        var first = stats.GetSnapshot(At(148));
        Require(first.SourceTimestampSamples == 1, "Missing/future source timestamps manufactured delivery samples.");
        Near(first.EstimatedGenerationToReceiveP95Ms, 12, "Estimated source-to-arrival timing is incorrect.");

        var idle = stats.GetSnapshot(At(6000));
        Require(idle.SourceTimestampSamples == 0, "Idle observation retained old delivery samples indefinitely.");
        stats.Record(4, At(10_000), At(9997));
        var resumed = stats.GetSnapshot(At(10_000));
        Require(resumed.SourceTimestampSamples == 1, "Fresh resumed frame was not measured.");
        Near(resumed.EstimatedGenerationToReceiveP95Ms, 3, "Idle gap was confused with frame delivery latency.");
        Near(resumed.MaxReceiveIntervalMs, 9852, "Delivery statistics hid the separate idle/stall interval.");

        stats.Record(5, At(10_020), At(10_020)); // A valid measured zero remains a real sample.
        Require(stats.GetSnapshot(At(10_020)).SourceTimestampSamples == 2,
            "A valid zero delivery delay was confused with a missing timestamp.");
        stats.Reset();
        var reset = stats.GetSnapshot(At(10_020));
        Require(reset.SourceTimestampSamples == 0 && reset.EstimatedGenerationToReceiveP95Ms == 0,
            "New session retained source delivery timing.");
    }

    private static void CheckStageTiming()
    {
        var stats = new PreviewPipelineStatistics();
        var frame = new GameFrame(1, 1, new byte[3], 1, At(-500), receivedAtTimestamp: At(0));
        stats.RecordConversion(frame, At(2), At(4));
        stats.RecordUiCopy(At(4), At(10), At(11));
        stats.RecordDraw(At(0), At(16));
        var timings = stats.GetSnapshot(At(16));
        Near(timings.Conversion.P95Ms, 2, "Conversion timing includes unrelated work.");
        Near(timings.ReceiveToPreview.P95Ms, 4, "Preview timing used generation instead of arrival.");
        Near(timings.PreviewToUi.P95Ms, 6, "UI scheduling delay is incorrect.");
        Near(timings.UiCopy.P95Ms, 1, "Bitmap copy timing is incorrect.");
        Near(timings.ReceiveToDraw.P95Ms, 16, "Draw submission timing used generation instead of arrival.");

        stats.RecordUiCopy(0, At(20), At(21));
        stats.RecordDraw(0, At(30));
        var missing = stats.GetSnapshot(At(30));
        Require(missing.ReceiveToDraw.Samples == 1 && missing.PreviewToUi.Samples == 1,
            "Missing source timestamps manufactured latency samples.");
        var expired = stats.GetSnapshot(At(6000));
        Require(expired.Conversion.Samples == 0 && expired.UiCopy.Samples == 0
            && expired.ReceiveToDraw.Samples == 0, "Stage timings retained old scene data indefinitely.");
        stats.Reset();
        Require(stats.GetSnapshot(At(6000)).ReceiveToDraw.Samples == 0, "Reset failed.");
    }

    private static long At(double milliseconds) => Stopwatch.Frequency
        + (long)(milliseconds * Stopwatch.Frequency / 1000d);

    private static void Near(double value, double expected, string message) =>
        Require(Math.Abs(value - expected) <= 0.02, $"{message} Expected {expected}, got {value}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
