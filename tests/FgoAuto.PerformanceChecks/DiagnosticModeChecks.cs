using System.Diagnostics;
using FgoAuto.Models;
using FgoAuto.Services;

internal static class DiagnosticModeChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FgoAuto-diagnostic-check-" + Guid.NewGuid().ToString("N"));
        // No AVD is created and no simulator/guest is launched by these checks.
        using (var service = new AndroidEmulatorService(directory))
        {
            long previousEpoch = service.Diagnostics.RecognitionEpoch;
            foreach (var mode in Enum.GetValues<PerformanceDiagnosticMode>())
            {
                await service.SetDiagnosticModeAsync(mode);
                var diagnostics = service.Diagnostics;
                Require(diagnostics.Mode == mode, "Diagnostic mode was not applied.");
                Require(diagnostics.RecognitionEpoch > previousEpoch, "Pause/resume did not invalidate in-flight recognition.");
                previousEpoch = diagnostics.RecognitionEpoch;
                Require(diagnostics.IsRecognitionPaused == (mode is PerformanceDiagnosticMode.PauseRecognition or PerformanceDiagnosticMode.PauseScreenshotStream),
                    "Recognition pause semantics changed.");
                Require(diagnostics.IsPreviewPaused == (mode is PerformanceDiagnosticMode.PausePreview or PerformanceDiagnosticMode.PauseScreenshotStream),
                    "Preview pause accidentally affects another mode.");
                Require(diagnostics.PreviewFramesPerSecond == (mode == PerformanceDiagnosticMode.Preview30Fps ? 30 : 60),
                    "30 FPS setting affected screenshot/recognition mode selection.");
                Require(!service.IsRunning && !service.HasLiveScreenshotStream && service.RuntimeProcessId == 0,
                    "Changing diagnostics launched an emulator or started work unexpectedly.");
                if (mode == PerformanceDiagnosticMode.PauseScreenshotStream)
                {
                    Require(service.CaptureLatestFrame() is null, "Paused stream exposed a stale frame.");
                    try { await service.CaptureFreshFrameAsync(); throw new Exception("Paused stream attempted a unary screenshot."); }
                    catch (InvalidOperationException error) when (error.Message.Contains("性能诊断")) { }
                }
            }
            try { await service.SetDiagnosticModeAsync((PerformanceDiagnosticMode)99); throw new Exception("Invalid mode accepted."); }
            catch (ArgumentOutOfRangeException) { }
            await service.SetDiagnosticModeAsync(PerformanceDiagnosticMode.Normal);
        }
        Require(!Directory.Exists(directory), "Session diagnostics wrote user/AVD settings.");

        static long At(double ms) => Stopwatch.Frequency + (long)(Stopwatch.Frequency * ms / 1000d);
        var scheduler = new PreviewWorkScheduler();
        Require(scheduler.GetDelay(At(0), 30) == TimeSpan.Zero, "First frame was delayed.");
        scheduler.RecordStarted(At(0));
        Require(scheduler.GetDelay(At(16), 30).TotalMilliseconds is > 17 and < 18,
            "30 FPS cap was applied after work or used the wrong interval.");
        Require(scheduler.GetDelay(At(40), 30) == TimeSpan.Zero, "Latest frame after a slow interval was unnecessarily delayed.");
        scheduler.RecordStarted(At(1000));
        Require(scheduler.GetDelay(At(1001), 60).TotalMilliseconds < 16,
            "Changing mode retained a 30 FPS delay.");
        scheduler.Reset();
        Require(scheduler.GetDelay(At(1001), 30) == TimeSpan.Zero, "Resuming preview retained old pacing state.");
        scheduler.Reset();
        var simulated = At(0);
        for (var frame = 0; frame < 30; frame++)
        {
            var delay = scheduler.GetDelay(simulated, 30);
            simulated += (long)((delay.TotalMilliseconds + 12) * Stopwatch.Frequency / 1000d);
            scheduler.RecordStarted(simulated);
            simulated += (long)(2 * Stopwatch.Frequency / 1000d);
        }
        Require(Stopwatch.GetElapsedTime(At(0), simulated).TotalMilliseconds < 1100,
            "Timer overshoot accumulated into an unintended much lower preview rate.");
        Console.WriteLine("PASS session diagnostics: mode semantics, no guest/config mutation, no paused unary capture, work pacing");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
