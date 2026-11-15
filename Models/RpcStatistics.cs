using System.Diagnostics;

namespace FgoAuto.Models;

public readonly record struct RpcStatisticsSnapshot(long Calls, long FailedCalls, TimingStatisticsSnapshot Recent);

public sealed class RpcStatistics
{
    private readonly object _sync = new();
    private readonly RecentTimingSamples _durations = new();
    private long _calls, _failedCalls;
    public void Record(long startedAt, long completedAt, bool success)
    {
        if (startedAt <= 0 || completedAt < startedAt) return;
        lock (_sync)
        {
            _calls++;
            if (!success) _failedCalls++;
            _durations.Record(Stopwatch.GetElapsedTime(startedAt, completedAt).TotalMilliseconds, completedAt);
        }
    }
    public RpcStatisticsSnapshot GetSnapshot(long now = 0)
    {
        lock (_sync) return new(_calls, _failedCalls, _durations.GetSnapshot(now == 0 ? Stopwatch.GetTimestamp() : now));
    }
    public void Reset() { lock (_sync) { _calls = _failedCalls = 0; _durations.Clear(); } }
}
