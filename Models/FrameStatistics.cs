using System.Diagnostics;

namespace FgoAuto.Models;

public sealed record FrameStatisticsSnapshot(long ReceivedFrames, long SourceSkippedFrames,
    long LastReceivedAtTimestamp, double ReceiveIntervalP95Ms, double MaxReceiveIntervalMs = 0,
    long LongReceiveIntervals = 0, int WindowIntervalSamples = 0,
    double EstimatedGenerationToReceiveP95Ms = 0, int SourceTimestampSamples = 0);

/// <summary>
/// Counts new frames, not refreshes or game renders. Recent intervals use a fixed
/// five-second window; session totals preserve gaps even after their samples expire.
/// </summary>
public sealed class FrameStatistics
{
    private readonly object _sync = new();
    private readonly RecentTimingSamples _intervals = new();
    private readonly RecentTimingSamples _estimatedGenerationToReceive = new();
    private long _received, _skipped, _lastAt, _lastSequence = -1;
    private double _maxInterval;
    private long _longIntervals;

    /// <param name="capturedAt">
    /// Estimated emulator image-generation time mapped onto the local Stopwatch
    /// clock; zero means unavailable. This is not the game's render timestamp.
    /// </param>
    public void Record(long sourceSequence, long receivedAt, long capturedAt = 0)
    {
        lock (_sync)
        {
            // Only this frame's source/arrival pair measures delivery latency.
            // The gap since the previous frame can include idle time and must
            // never be used as a delivery-latency sample.
            if (capturedAt > 0 && capturedAt <= receivedAt)
                _estimatedGenerationToReceive.Record(
                    Stopwatch.GetElapsedTime(capturedAt, receivedAt).TotalMilliseconds, receivedAt);
            if (_lastAt > 0 && receivedAt > _lastAt)
            {
                var interval = Stopwatch.GetElapsedTime(_lastAt, receivedAt).TotalMilliseconds;
                _maxInterval = Math.Max(_maxInterval, interval);
                if (interval > 50) _longIntervals++;
                // The interval belongs to the window in which it ENDS. A first
                // frame after a long gap must not hide that gap.
                _intervals.Record(interval, receivedAt);
            }
            if (_lastSequence >= 0 && sourceSequence > _lastSequence + 1)
                _skipped += sourceSequence - _lastSequence - 1;
            _lastSequence = sourceSequence;
            _lastAt = receivedAt;
            _received++;
        }
    }

    /// <summary>Starts a new interval observation without changing cumulative frame counts.</summary>
    public void ResetIntervals()
    {
        lock (_sync)
        {
            _lastAt = _longIntervals = 0;
            _maxInterval = 0;
            _intervals.Clear();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _received = _skipped = _lastAt = _longIntervals = 0;
            _maxInterval = 0;
            _lastSequence = -1;
            _intervals.Clear();
            _estimatedGenerationToReceive.Clear();
        }
    }

    public FrameStatisticsSnapshot GetSnapshot(long nowTimestamp = 0)
    {
        if (nowTimestamp == 0) nowTimestamp = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            var recent = _intervals.GetSnapshot(nowTimestamp);
            var delivery = _estimatedGenerationToReceive.GetSnapshot(nowTimestamp);
            return new(_received, _skipped, _lastAt, recent.P95Ms, _maxInterval, _longIntervals,
                recent.Samples, delivery.P95Ms, delivery.Samples);
        }
    }
}

public readonly record struct TimingStatisticsSnapshot(int Samples, double P95Ms, double MaxMs);

/// <summary>Locking is supplied by the owning statistics object. No allocation when recording.</summary>
internal sealed class RecentTimingSamples
{
    internal const double WindowSeconds = 5;
    internal const int MaximumSamples = 2048;
    private readonly Queue<Sample> _samples = new(MaximumSamples);
    private readonly record struct Sample(long At, double Milliseconds);

    public void Record(double milliseconds, long at)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        Prune(at);
        if (_samples.Count == MaximumSamples) _samples.Dequeue();
        _samples.Enqueue(new Sample(at, milliseconds));
    }

    public TimingStatisticsSnapshot GetSnapshot(long now)
    {
        Prune(now);
        if (_samples.Count == 0) return default;
        var sorted = _samples.Select(sample => sample.Milliseconds).Order().ToArray();
        return new(sorted.Length, sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
    }

    public void Clear() => _samples.Clear();

    private void Prune(long now)
    {
        var oldest = now - (long)(WindowSeconds * Stopwatch.Frequency);
        while (_samples.TryPeek(out var sample) && sample.At < oldest) _samples.Dequeue();
    }
}
