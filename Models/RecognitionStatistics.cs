using System.Diagnostics;

namespace FgoAuto.Models;

public sealed record RecognitionStatisticsSnapshot(long TotalRounds, int WindowRounds,
    double MeanMs, double P95Ms, double DutyRatio)
{
    public override string ToString() =>
        $"识别近5秒 {WindowRounds} 轮，平均 {MeanMs:F1}ms / P95 {P95Ms:F1}ms，墙钟占用 {DutyRatio:P1}；累计 {TotalRounds} 轮";
}

/// <summary>Measures matching wall time, including grayscale conversion, parallel work and scheduling.</summary>
public sealed class RecognitionStatistics
{
    private const int MaximumSamples = 2048;
    private readonly object _sync = new();
    private readonly Queue<Sample> _samples = new(MaximumSamples);
    private long _totalRounds;
    private readonly record struct Sample(long StartedAt, long CompletedAt);

    public void Record(long startedAt, long completedAt = 0)
    {
        if (completedAt == 0) completedAt = Stopwatch.GetTimestamp();
        if (startedAt <= 0 || completedAt < startedAt) return;
        lock (_sync)
        {
            Prune(completedAt);
            if (_samples.Count == MaximumSamples) _samples.Dequeue();
            _samples.Enqueue(new(startedAt, completedAt));
            _totalRounds++;
        }
    }

    public RecognitionStatisticsSnapshot GetSnapshot(long nowTimestamp = 0)
    {
        if (nowTimestamp == 0) nowTimestamp = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            Prune(nowTimestamp);
            if (_samples.Count == 0) return new(_totalRounds, 0, 0, 0, 0);
            var oldest = nowTimestamp - 5 * Stopwatch.Frequency;
            var durations = _samples.Select(sample =>
                Stopwatch.GetElapsedTime(sample.StartedAt, sample.CompletedAt).TotalMilliseconds).Order().ToArray();
            var busyTicks = _samples.Sum(sample => (double)Math.Max(0,
                Math.Min(nowTimestamp, sample.CompletedAt) - Math.Max(oldest, sample.StartedAt)));
            return new(_totalRounds, durations.Length, durations.Average(),
                durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
                Math.Clamp(busyTicks / (5 * Stopwatch.Frequency), 0, 1));
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _samples.Clear();
            _totalRounds = 0;
        }
    }

    private void Prune(long now)
    {
        var oldest = now - 5 * Stopwatch.Frequency;
        while (_samples.TryPeek(out var sample) && sample.CompletedAt < oldest) _samples.Dequeue();
    }
}
