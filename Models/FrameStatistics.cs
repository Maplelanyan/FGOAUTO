using System.Diagnostics;

namespace FgoAuto.Models;

public sealed record FrameStatisticsSnapshot(long ReceivedFrames, long SourceSkippedFrames,
    long LastReceivedAtTimestamp, double ReceiveIntervalP95Ms, double MaxReceiveIntervalMs = 0, long LongReceiveIntervals = 0);

/// <summary>Bounded stream observations, including stalls and idle periods without hiding long gaps.</summary>
public sealed class FrameStatistics
{
    private readonly object _sync = new();
    private readonly Queue<double> _intervals = new(240);
    private long _received, _skipped, _lastAt, _lastSequence = -1;
    private double _maxInterval;
    private long _longIntervals;

    public void Record(long sourceSequence, long receivedAt)
    {
        lock (_sync)
        {
            if (_lastAt > 0)
            {
                var interval = Stopwatch.GetElapsedTime(_lastAt, receivedAt).TotalMilliseconds;
                if (interval > 0)
                {
                    _maxInterval = Math.Max(_maxInterval, interval);
                    if (interval > 50) _longIntervals++;
                    if (_intervals.Count == 240) _intervals.Dequeue();
                    _intervals.Enqueue(interval);
                }
            }
            if (_lastSequence >= 0 && sourceSequence > _lastSequence + 1)
                _skipped += sourceSequence - _lastSequence - 1;
            _lastSequence = sourceSequence;
            _lastAt = receivedAt;
            _received++;
        }
    }

    public void Reset()
    {
        lock (_sync) { _received = _skipped = _lastAt = _longIntervals = 0; _maxInterval = 0; _lastSequence = -1; _intervals.Clear(); }
    }

    public FrameStatisticsSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var sorted = _intervals.Order().ToArray();
            var p95 = sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1)];
            return new(_received, _skipped, _lastAt, p95, _maxInterval, _longIntervals);
        }
    }
}
