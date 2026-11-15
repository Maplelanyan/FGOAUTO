using System.Diagnostics;

namespace FgoAuto.Models;

public sealed record ProcessResourceSnapshot(int ProcessId, bool Available, double CpuCorePercent,
    double WorkingSetMB, double PrivateMB);
public sealed record ResourceSnapshot(ProcessResourceSnapshot Client, ProcessResourceSnapshot Emulator);

/// <summary>Samples exact owned process IDs off the UI thread; 100% CPU means one logical core.</summary>
public sealed class ProcessResourceMonitor : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private ResourceSnapshot _latest = new(new(0, false, 0, 0, 0), new(0, false, 0, 0, 0));
    public ResourceSnapshot Latest => Volatile.Read(ref _latest);

    public ProcessResourceMonitor(Func<int> getEmulatorPid)
    {
        _worker = Task.Run(async () =>
        {
            using var client = new SampledProcess();
            using var emulator = new SampledProcess();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    var now = Stopwatch.GetTimestamp();
                    Volatile.Write(ref _latest, new(client.Read(Environment.ProcessId, now), emulator.Read(getEmulatorPid(), now)));
                } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }

    private sealed class SampledProcess : IDisposable
    {
        private Process? _process;
        private TimeSpan _cpu;
        private long _at;
        public ProcessResourceSnapshot Read(int id, long now)
        {
            try
            {
                if (_process?.Id != id)
                {
                    Dispose();
                    if (id <= 0) return new(id, false, 0, 0, 0);
                    _process = Process.GetProcessById(id);
                }
                _process!.Refresh();
                if (_process.HasExited) { Dispose(); return new(id, false, 0, 0, 0); }
                var cpu = _process.TotalProcessorTime;
                var elapsed = _at == 0 ? 0 : Stopwatch.GetElapsedTime(_at, now).TotalSeconds;
                var percent = elapsed <= 0 ? 0 : Math.Max(0, (cpu - _cpu).TotalSeconds / elapsed * 100);
                _at = now; _cpu = cpu;
                return new(id, true, percent, _process.WorkingSet64 / 1_000_000d, _process.PrivateMemorySize64 / 1_000_000d);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { Dispose(); return new(id, false, 0, 0, 0); }
        }
        public void Dispose() { _process?.Dispose(); _process = null; _at = 0; _cpu = default; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
