using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using FgoAuto.Models;
using FgoAuto.Services;

internal static class DiagnosticLifecycleChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FgoAuto-diagnostic-lifecycle-" + Guid.NewGuid().ToString("N"));
        // Inject tasks and owned buffers only. No emulator, gRPC endpoint or guest is started.
        await FaultedStreamCleanupAsync(directory);
        await PreviousObserverIsolationAsync(directory, faulted: false);
        await PreviousObserverIsolationAsync(directory, faulted: true);
        Require(!Directory.Exists(directory), "Diagnostic lifecycle checks wrote user/AVD settings.");
        Console.WriteLine("PASS diagnostic lifecycle: faulted-task cleanup, observer awaited, previous-session isolation");
    }

    private static async Task FaultedStreamCleanupAsync(string directory)
    {
        using var session = new CancellationTokenSource();
        using var preview = new VideoPreviewFrame(1, 1, ArrayPool<byte>.Shared.Rent(4), 1);
        using var service = new AndroidEmulatorService(directory);
        var observer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timestamp = Stopwatch.GetTimestamp();
        var frame = new GameFrame(1, 1, new byte[3], 1, timestamp);
        var signal = (SemaphoreSlim)GetField(service, "_previewSignal")!;

        SetField(service, "_streamCancellation", session);
        SetField(service, "_streamTask", Task.FromException(new InvalidDataException("Injected invalid screenshot data.")));
        SetField(service, "_previewTask", Task.FromException(new InvalidOperationException("Injected conversion failure.")));
        SetField(service, "_streamObserverTask", observer.Task);
        SetField(service, "_pendingPreview", preview);
        SetField(service, "_latestFrame", frame);
        service.FrameStatistics.Record(1, timestamp);
        service.FrameStatistics.Record(2, timestamp + 1);
        service.PreviewStatistics.RecordConversion(frame, timestamp, timestamp + 1);
        signal.Release();

        try
        {
            var stopping = service.StopAsync();
            Require(session.IsCancellationRequested, "Stopping a failed stream did not cancel its session.");
            Require(!stopping.IsCompleted, "Stopping did not await the outstanding stream observer.");
            observer.SetException(new InvalidOperationException("Injected observer failure."));
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));

            foreach (var name in new[] { "_streamTask", "_previewTask", "_streamObserverTask", "_streamCancellation", "_latestFrame", "_pendingPreview" })
                Require(GetField(service, name) is null, "Failed stream cleanup retained " + name + ".");
            RequireDisposed(() => { _ = preview.Pixels; }, "Failed stream cleanup did not dispose the pending preview buffer.");
            RequireDisposed(() => { _ = session.Token; }, "Failed stream cleanup did not dispose its cancellation source.");
            Require(!signal.Wait(0), "Failed stream cleanup retained a previous-session preview notification.");
            var frames = service.FrameStatistics.GetSnapshot();
            Require(frames.ReceivedFrames == 2 && frames.WindowIntervalSamples == 0 && frames.LastReceivedAtTimestamp == 0,
                "Failed stream cleanup did not reset intervals while retaining cumulative frame counts.");
            var stages = service.PreviewStatistics.GetSnapshot();
            Require(stages.ConvertedFrames == 0 && stages.Conversion.Samples == 0,
                "Failed stream cleanup retained conversion measurements.");
            Require(!service.IsRunning && !service.HasLiveScreenshotStream && service.RuntimeProcessId == 0,
                "Fault injection unexpectedly started an emulator or retained a running stream.");

            await service.StopAsync();
            await service.DisposeAsync();
            await service.DisposeAsync();
        }
        finally
        {
            // A failed assertion must also release the injected observer before service disposal.
            observer.TrySetResult();
        }
    }

    private static async Task PreviousObserverIsolationAsync(string directory, bool faulted)
    {
        using var previousSession = new CancellationTokenSource();
        using var currentSession = new CancellationTokenSource();
        using var service = new AndroidEmulatorService(directory);
        var previousStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousFirstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentFirstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(service, "_streamCancellation", currentSession);
        SetField(service, "_firstFrame", currentFirstFrame);
        var method = typeof(AndroidEmulatorService).GetMethod("ObserveScreenshotStreamAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Diagnostic observer method was not found.");
        var observation = (Task)method.Invoke(service, new object[] { previousStream.Task, previousSession, previousFirstFrame })!;

        if (faulted) previousStream.SetException(new InvalidDataException("Injected previous-session stream failure."));
        else previousStream.SetResult();
        await observation.WaitAsync(TimeSpan.FromSeconds(5));

        Require(previousSession.IsCancellationRequested, "The previous stream observer did not cancel its own session.");
        Require(previousFirstFrame.Task.IsFaulted, "The previous stream observer did not fail its own first-frame waiter.");
        _ = previousFirstFrame.Task.Exception; // Observe the expected exception without introducing an unobserved task fault.
        Require(!currentSession.IsCancellationRequested, "A previous stream observer cancelled the current session.");
        Require(!currentFirstFrame.Task.IsCompleted, "A previous stream observer completed the current first-frame waiter.");
        Require(ReferenceEquals(GetField(service, "_streamCancellation"), currentSession)
            && ReferenceEquals(GetField(service, "_firstFrame"), currentFirstFrame),
            "A previous stream observer replaced current-session state.");
    }

    private static FieldInfo FindField(string name) =>
        typeof(AndroidEmulatorService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Diagnostic lifecycle field was not found: " + name);

    private static object? GetField(AndroidEmulatorService service, string name) => FindField(name).GetValue(service);
    private static void SetField(AndroidEmulatorService service, string name, object? value) => FindField(name).SetValue(service, value);

    private static void RequireDisposed(Action read, string message)
    {
        try { read(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
