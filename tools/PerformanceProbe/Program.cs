using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FgoAuto.Controls;
using FgoAuto.Models;
using FgoAuto.Services;
using FgoAuto.PerformanceProbe;
using FgoAuto.ViewModels;

internal static class Program
{
    public static int Seconds { get; private set; } = 10;
    public static string Output { get; private set; } = string.Empty;
    public static bool BuildOnly { get; private set; }
    public static bool? OpenGlMode { get; private set; }
    public static bool IncludeH264 { get; private set; }
    public static bool ObserveFgo { get; private set; }
    public static bool RecognitionLoad { get; private set; }
    public static bool OnlyPreview30 { get; private set; }
    public static bool CompareRecognition { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds") Seconds = int.Parse(args[++i]);
            else if (args[i] == "--output") Output = Path.GetFullPath(args[++i]);
            else if (args[i] == "--describe-only") BuildOnly = true;
            else if (args[i] == "--standard") OpenGlMode = false;
            else if (args[i] == "--opengl") OpenGlMode = true;
            else if (args[i] == "--h264") IncludeH264 = true;
            else if (args[i] == "--observe-fgo") ObserveFgo = true;
            else if (args[i] == "--recognition-load") RecognitionLoad = true;
            else if (args[i] == "--only-preview30") OnlyPreview30 = true;
            else if (args[i] == "--compare-recognition") CompareRecognition = true;
            else throw new ArgumentException("Unknown diagnostic argument.");
        }
        if (CompareRecognition) { Environment.ExitCode = RecognitionComparison.Run(); return; }
        if (Seconds is < 5 or > 30) throw new ArgumentOutOfRangeException(nameof(Seconds));
        if (Output.Length == 0) Output = Path.Combine(AppContext.BaseDirectory,
            $"managed-preview-{DateTime.Now:yyyyMMdd-HHmmss}-{(GCSettings.IsServerGC ? "server" : "workstation")}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        if (BuildOnly)
        {
            Console.WriteLine("ManagedPreviewChecks: real MainWindow/MainViewModel, local Chromium animation, no game input, no installation or license acceptance.");
            Console.WriteLine($"Runtime: ServerGC={GCSettings.IsServerGC}; configuration=" + JsonSerializer.Serialize(GC.GetConfigurationVariables()));
            return;
        }
        if (Process.GetProcesses().Any(p => p.ProcessName is "FgoAuto" or "emulator"
            || p.ProcessName.StartsWith("qemu-system-x86_64", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Existing FgoAuto/emulator detected. Stop the other diagnostic before running this harness.");
        OpenCvSharp.Cv2.SetNumThreads(Math.Min(2, Environment.ProcessorCount));
        AppBuilder.Configure<ManagedPreviewApp>().UsePlatformDetect().WithInterFont()
            .StartWithClassicDesktopLifetime([]);
    }
}

public sealed class ManagedPreviewApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var vm = new MainViewModel();
        var window = new FgoAuto.Views.MainWindow
        {
            DataContext = vm, ShowActivated = false, IsHitTestVisible = false,
            Title = "FgoAuto · 画面性能测量（仅本地测试动画）",
        };
        desktop.MainWindow = window;
        var lifetime = new CancellationTokenSource();
        window.Closed += (_, _) => lifetime.Cancel();
        // Posted after framework initialization so the actual desktop window is shown.
        Dispatcher.UIThread.Post(async () =>
        {
            try { await MeasureAsync(vm, window, lifetime.Token); }
            catch (Exception error)
            {
                // Do not dump internals or emulator discovery/credential files.
                Console.Error.WriteLine($"Managed preview measurement failed: {error.GetType().Name}: {error.Message}");
                Environment.ExitCode = 1;
            }
            finally
            {
                vm.Dispose();
                window.Close();
                desktop.Shutdown(Environment.ExitCode);
                lifetime.Dispose();
            }
        }, DispatcherPriority.Background);
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task MeasureAsync(MainViewModel vm, Window window, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FgoAuto", "android-runtime");
        var sdk = Path.Combine(root, "sdk");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        using var animation = new AnimationServer();
        Process? qemu = null;
        string? foreground = null;
        object? measurement = null;
        object? fgoObservation = null;
        string? failure = null;
        var sourceWarmupReports = 0;
        Task? matching = null;
        using var matchingStop = new CancellationTokenSource();
        var emulator = PrivateField<AndroidEmulatorService>(vm, "_emulator");
        var originalPerformanceMode = emulator.UsePerformanceMode;
        var originalOpenGlMode = emulator.UseOpenGlMode;
        if (Program.OpenGlMode is { } graphicsMode)
        {
            // Diagnostic process-local launch choice: no settings setter and no
            // production code/config changes are needed to compare the modes.
            typeof(AndroidEmulatorService).GetField("<UseOpenGlMode>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emulator, graphicsMode);
        }
        var touch = PrivateField<PreviewTouchDispatcher>(vm, "_previewTouches");
        var draw = new FrameStatistics();
        ScreenshotPreview? preview = null;
        EventHandler<PreviewFrameRenderedEventArgs> onDraw = (_, e) => draw.Record(e.Sequence, e.RenderedAtTimestamp);
        try
        {
            Console.WriteLine($"Starting managed preview measurement; ServerGC={GCSettings.IsServerGC}; OpenGlMode={emulator.UseOpenGlMode}; seconds={Program.Seconds}; credentials are not printed.");
            Console.WriteLine("GC configuration: " + JsonSerializer.Serialize(GC.GetConfigurationVariables()));
            var started = DateTime.UtcNow;
            await vm.StartEmulatorCommand.ExecuteAsync(null);
            if (!vm.IsEmulatorRunning || !emulator.HasLiveScreenshotStream)
                throw new InvalidOperationException("Managed emulator did not reach streaming state: " + vm.EmulatorStatus);
            qemu = Process.GetProcesses().FirstOrDefault(p => p.ProcessName.StartsWith("qemu-system-x86_64", StringComparison.OrdinalIgnoreCase)
                && p.StartTime.ToUniversalTime() >= started.AddSeconds(-1))
                ?? throw new InvalidOperationException("Managed emulator process was not found.");
            // The emulator's boot log can precede Android's completed boot and
            // Launcher replacing FallbackHome. Starting Chrome before that can
            // put it behind Launcher during the measured interval.
            var ready = Stopwatch.StartNew();
            while (ready.Elapsed < TimeSpan.FromSeconds(60))
            {
                token.ThrowIfCancellationRequested();
                var booted = (await AdbAsync("shell", "getprop", "sys.boot_completed")).Trim() == "1";
                var component = Foreground(await AdbAsync("shell", "dumpsys", "activity", "activities"));
                if (booted && component is not null && !component.EndsWith("/.FallbackHome", StringComparison.Ordinal))
                {
                    foreground = component;
                    break;
                }
                await Task.Delay(500, token);
            }
            if (foreground is null) throw new InvalidOperationException("Android boot/foreground did not finish initializing.");
            await AdbAsync("shell", "am", "start", "-a", "android.intent.action.VIEW", "-d",
                $"http://10.0.2.2:{animation.Port}/", "com.android.chrome");
            var loading = Stopwatch.StartNew();
            while (animation.ReportCount == 0 && loading.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(200, token);
            if (animation.ReportCount == 0)
                throw new InvalidOperationException("Local animation did not report; Chrome may require setup. No terms or setup prompts were accepted.");
            preview = window.GetVisualDescendants().OfType<ScreenshotPreview>().Single();
            preview.FrameRendered += onDraw;
            await Task.Delay(8000, token);
            var warmedForeground = Foreground(await AdbAsync("shell", "dumpsys", "activity", "activities"));
            if (warmedForeground?.StartsWith("com.android.chrome/", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Chrome lost foreground during warmup: " + warmedForeground);
            sourceWarmupReports = animation.ReportCount;
            if (sourceWarmupReports < 4)
                throw new InvalidOperationException("Insufficient live source animation during warmup.");
            var queries = vm.ClickRules.Select(rule => new ImageMatchQuery(rule.Target.Id, rule.Target.ImagePath,
                rule.Threshold, rule.IsCardRule, rule.PreferTopmostMatch, rule.IsNoblePhantasm)).ToArray();
            var recognition = PrivateField<RecognitionStatistics>(vm, "_recognitionStatistics");
            if (Program.RecognitionLoad && queries.Length > 0)
            {
                matching = Task.Run(async () =>
                {
                    using var matcher = new ImageMatchService();
                    long previous = -1;
                    while (!matchingStop.IsCancellationRequested)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var frame = emulator.CaptureLatestFrame();
                        if (!emulator.Diagnostics.IsRecognitionPaused && frame is not null
                            && frame.FrameId != previous && frame.Age < TimeSpan.FromMilliseconds(700))
                        {
                            previous = frame.FrameId;
                            matcher.FindAll(frame, queries);
                            recognition.Record(started);
                        }
                        var remaining = TimeSpan.FromMilliseconds(50) - Stopwatch.GetElapsedTime(started);
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, matchingStop.Token);
                    }
                }, matchingStop.Token);
            }

            var measurements = new List<object>();
            measurement = measurements;
            foreach (var (phase, mode) in new[]
            {
                ("normal", PerformanceDiagnosticMode.Normal),
                ("pause-preview", PerformanceDiagnosticMode.PausePreview),
                ("preview-30", PerformanceDiagnosticMode.Preview30Fps),
                ("pause-stream", PerformanceDiagnosticMode.PauseScreenshotStream),
                ("pause-recognition", PerformanceDiagnosticMode.PauseRecognition),
                ("normal-repeat", PerformanceDiagnosticMode.Normal),
            }.Where(phase => !Program.OnlyPreview30 || phase.Item2 == PerformanceDiagnosticMode.Preview30Fps))
            {
                animation.Phase = "warmup-" + phase;
                await emulator.SetDiagnosticModeAsync(mode, token);
                await Task.Delay(2000, token);
                typeof(MainViewModel).GetMethod("ResetPreviewMeasurementWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
                draw.Reset();
                animation.Phase = phase;
                var receivedBefore = emulator.FrameStatistics.GetSnapshot().ReceivedFrames;
                var renderedBefore = preview.RenderedFrameCount;
                var allocatedBefore = GC.GetTotalAllocatedBytes(false);
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var pauseBefore = GC.GetTotalPauseDuration();
                using var self = Process.GetCurrentProcess();
                self.Refresh(); qemu.Refresh();
                var selfCpuBefore = self.TotalProcessorTime;
                var qemuCpuBefore = qemu.TotalProcessorTime;
                var measured = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(Program.Seconds), token);
                var wall = measured.Elapsed.TotalSeconds;
                animation.Phase = "drain";
                self.Refresh(); qemu.Refresh();
                var received = emulator.FrameStatistics.GetSnapshot();
                var drawn = draw.GetSnapshot();
                var stages = emulator.PreviewStatistics.GetSnapshot();
                var touchSnapshot = touch.GetSnapshot();
                var submittedFps = (preview.RenderedFrameCount - renderedBefore) / wall;
                var allocatedMBps = (GC.GetTotalAllocatedBytes(false) - allocatedBefore) / wall / 1_000_000d;
                var gcCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
                var gcPauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
                var clientCpuCorePercent = (self.TotalProcessorTime - selfCpuBefore).TotalSeconds / wall * 100;
                var emulatorCpuCorePercent = (qemu.TotalProcessorTime - qemuCpuBefore).TotalSeconds / wall * 100;
                var renderedAtEnd = preview.RenderedFrameCount;
                await Task.Delay(1200, token);
                var liveReports = animation.Reports.Where(report =>
                    report.GetProperty("phase").GetString() == phase
                    && report.GetProperty("visible").GetString() == "visible").ToArray();
                if (liveReports.Length < Math.Max(2, Program.Seconds / 2))
                    throw new InvalidOperationException("No sustained visible source animation in " + phase + "; this run cannot compare performance.");
                var sourceFrames = liveReports.Sum(report => report.GetProperty("frames").GetInt32());
                var sourceMs = liveReports.Sum(report => report.GetProperty("milliseconds").GetDouble());
                var row = new
                {
                    phase, mode = mode.ToString(), seconds = wall,
                    sourceRafFps = sourceFrames / sourceMs * 1000, visibleReports = liveReports.Length,
                    receivedFps = (received.ReceivedFrames - receivedBefore) / wall,
                    submittedFps,
                    receiveIntervalP95Ms = received.ReceiveIntervalP95Ms,
                    drawIntervalP95Ms = drawn.ReceiveIntervalP95Ms,
                    sourceEstimatedDeliveryP95Ms = received.EstimatedGenerationToReceiveP95Ms,
                    stages,
                    allocatedMBps, gcCollections, gcPauseMs, clientCpuCorePercent, emulatorCpuCorePercent,
                    clientWorkingSetMB = self.WorkingSet64 / 1_000_000d,
                    emulatorWorkingSetMB = qemu.WorkingSet64 / 1_000_000d,
                    previewSize = vm.ScreenSizeText, useOpenGlMode = emulator.UseOpenGlMode,
                    usePerformanceMode = emulator.UsePerformanceMode,
                    touch = touchSnapshot, automationRunning = vm.IsRunning,
                    recognitionLoad = Program.RecognitionLoad ? $"read-only matcher, {queries.Length} saved queries, max20Hz; NO rule execution/touches" : "not started; pause-recognition validates control only",
                    recognition = recognition.GetSnapshot(),
                };
                measurements.Add(row);
                Console.WriteLine("PHASE " + JsonSerializer.Serialize(row));
                if (!emulator.IsRunning || qemu.HasExited || qemu.Id != emulator.RuntimeProcessId)
                    throw new InvalidOperationException("Diagnostic changed emulator lifetime/process.");
                if (vm.IsRunning || touchSnapshot.SentCommands != 0)
                    throw new InvalidOperationException("Unexpected automation or touch input during visual-only measurement.");
                if (mode == PerformanceDiagnosticMode.PauseScreenshotStream)
                {
                    if (received.ReceivedFrames != receivedBefore || emulator.CaptureLatestFrame() is not null || emulator.HasLiveScreenshotStream)
                        throw new InvalidOperationException("Paused screenshot stream kept processing frames.");
                }
                else if (received.ReceivedFrames <= receivedBefore)
                    throw new InvalidOperationException("No new screenshots in " + phase);
                if (mode is PerformanceDiagnosticMode.PausePreview or PerformanceDiagnosticMode.PauseScreenshotStream)
                {
                    if (renderedAtEnd != renderedBefore || stages.Conversion.Samples != 0)
                        throw new InvalidOperationException("Paused preview kept converting/submitting frames.");
                }
                if (mode == PerformanceDiagnosticMode.Preview30Fps && stages.ConvertedFrames > wall * 30 + 2)
                    throw new InvalidOperationException("Preview processing exceeded the 30 FPS cap.");
            }
            if (Program.IncludeH264)
            {
                const string phase = "h264-readonly";
                animation.Phase = "h264-warmup";
                await emulator.SetDiagnosticModeAsync(PerformanceDiagnosticMode.PauseScreenshotStream, token);
                try
                {
                    await using var video = await H264ReadOnlySession.StartAsync(
                        Path.Combine(sdk, "platform-tools", "adb.exe"), "emulator-5554", token);
                    await Task.Delay(2000, token);
                    animation.Phase = phase;
                    var framesBefore = video.DecodedFrames;
                    var bytesBefore = video.EncodedBytes;
                    var allocatedBefore = GC.GetTotalAllocatedBytes(false);
                    var pauseBefore = GC.GetTotalPauseDuration();
                    using var self = Process.GetCurrentProcess();
                    self.Refresh(); qemu.Refresh();
                    var selfBefore = self.TotalProcessorTime;
                    var qemuBefore = qemu.TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    await Task.Delay(TimeSpan.FromSeconds(Program.Seconds), token);
                    var wall = clock.Elapsed.TotalSeconds;
                    animation.Phase = "drain";
                    self.Refresh(); qemu.Refresh();
                    var frames = video.DecodedFrames - framesBefore;
                    var encodedBytes = video.EncodedBytes - bytesBefore;
                    var allocatedMBps = (GC.GetTotalAllocatedBytes(false) - allocatedBefore) / wall / 1_000_000d;
                    var gcPauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
                    var clientCpuCorePercent = (self.TotalProcessorTime - selfBefore).TotalSeconds / wall * 100;
                    var emulatorCpuCorePercent = (qemu.TotalProcessorTime - qemuBefore).TotalSeconds / wall * 100;
                    using var decoded = video.TakeLatestFrame();
                    await Task.Delay(1200, token);
                    var reports = animation.Reports.Where(report => report.GetProperty("phase").GetString() == phase
                        && report.GetProperty("visible").GetString() == "visible").ToArray();
                    var valid = reports.Length >= Math.Max(2, Program.Seconds / 2) && video.Failure is null && frames > 0;
                    var sourceMs = reports.Sum(report => report.GetProperty("milliseconds").GetDouble());
                    var row = new
                    {
                        phase, valid, seconds = wall, decodedFps = frames / wall,
                        sourceRafFps = sourceMs > 0 ? reports.Sum(report => report.GetProperty("frames").GetInt32()) / sourceMs * 1000 : 0,
                        encodedMBps = encodedBytes / wall / 1_000_000d, allocatedMBps, gcPauseMs,
                        clientCpuCorePercent, emulatorCpuCorePercent, decoderSize = new { decoded?.Width, decoded?.Height },
                        failure = video.Failure?.Message, encoder = video.ServerLog,
                        note = "Experimental 1280px / max60fps / 12Mbps H.264 + FFmpeg software decode + BGRA conversion. No UI upload, no automation, no PTS-to-present latency measurement; not a production replacement.",
                    };
                    measurements.Add(row);
                    Console.WriteLine("PHASE " + JsonSerializer.Serialize(row));
                }
                catch (Exception error)
                {
                    measurements.Add(new { phase, valid = false, failure = error.GetType().Name + ": " + error.Message });
                    Console.WriteLine("H264 comparison unavailable: " + error.GetType().Name + ": " + error.Message);
                }
            }
            await emulator.SetDiagnosticModeAsync(PerformanceDiagnosticMode.Normal, token);
            await Task.Delay(1000, token);
            var latest = emulator.CaptureLatestFrame();
            if (latest is null || latest.Age > TimeSpan.FromMilliseconds(700))
                throw new InvalidOperationException("Resume did not provide a fresh complete frame.");
            using (var screenshot = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96)))
            {
                screenshot.Render((Control)window.Content!);
                screenshot.Save(Path.ChangeExtension(Program.Output, ".window.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            if (Program.ObserveFgo)
            {
                await emulator.SetDiagnosticModeAsync(PerformanceDiagnosticMode.PauseRecognition, token);
                animation.Phase = "done";
                await CloseOwnedPageAsync();
                const string package = "com.bilibili.fatego";
                var component = (await AdbAsync("shell", "cmd", "package", "resolve-activity", "--brief", package))
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(line => line.StartsWith(package + "/", StringComparison.Ordinal));
                if (component is null) fgoObservation = new { available = false, reason = "Installed launcher was not found; game not launched." };
                else
                {
                    await AdbAsync("shell", "am", "start", "-n", component);
                    await Task.Delay(12000, token);
                    var pid = (await AdbAsync("shell", "ps", "-A", "-o", "PID,NAME"))
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Where(line => line.TrimEnd().EndsWith(" " + package, StringComparison.Ordinal))
                        .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).FirstOrDefault();
                    var current = Foreground(await AdbAsync("shell", "dumpsys", "activity", "activities"));
                    var layers = (await AdbAsync("shell", "dumpsys", "SurfaceFlinger", "--list"))
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Where(line => line.Contains(package, StringComparison.Ordinal)).ToArray();
                    var timings = new List<object>();
                    foreach (var layer in layers.Where(line => line.Contains("SurfaceView", StringComparison.Ordinal)).Take(3))
                    {
                        // System layer timestamps only; no game input, account login,
                        // package installation, root access or data modifications.
                        // adb shell joins arguments; quote the complete layer name
                        // because SurfaceView names can contain spaces and parentheses.
                        var shellLayer = "'" + layer.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
                        var output = await AdbAsync("shell", "dumpsys SurfaceFlinger --latency " + shellLayer);
                        timings.Add(new { layer, rawLatencyNanoseconds = output });
                    }
                    var gameFrame = emulator.CaptureLatestFrame();
                    if (gameFrame is not null)
                        await File.WriteAllBytesAsync(Path.ChangeExtension(Program.Output, ".fgo.png"), gameFrame.EncodePng(), token);
                    fgoObservation = new { available = true, package, pid, foreground = current, timings,
                        restriction = "12-second cold-start observation only; no login/terms/download prompts handled, no battle-FPS claim." };
                }
            }
        }
        catch (Exception error)
        {
            failure = error.GetType().Name + ": " + error.Message;
            throw;
        }
        finally
        {
            matchingStop.Cancel();
            if (matching is not null)
                try { await matching; } catch (OperationCanceledException) when (matchingStop.IsCancellationRequested) { }
                catch (Exception error) { failure ??= "Read-only recognition: " + error.GetType().Name + ": " + error.Message; }
            if (preview is not null) preview.FrameRendered -= onDraw;
            if (vm.IsEmulatorRunning)
            {
                if (failure is not null)
                {
                    try
                    {
                        var frame = emulator.CaptureLatestFrame();
                        if (frame is not null)
                            await File.WriteAllBytesAsync(Path.ChangeExtension(Program.Output, ".png"), frame.EncodePng());
                        var current = Foreground(await AdbAsync("shell", "dumpsys", "activity", "activities"));
                        Console.WriteLine("Invalid-run foreground component: " + current);
                    }
                    catch { }
                }
                animation.Phase = "done";
                // Only the exact loopback URL from this process may be closed.
                // No other Chrome tab details are written to output or artifacts.
                await CloseOwnedPageAsync();
                try
                {
                    if (!string.IsNullOrEmpty(foreground) && !foreground.EndsWith("/.FallbackHome", StringComparison.Ordinal))
                        await AdbAsync("shell", "am", "start", "-n", foreground);
                    else await AdbAsync("shell", "am", "start", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.HOME");
                }
                catch { /* The managed shutdown still runs if foreground restore fails. */ }
                await vm.StopEmulatorCommand.ExecuteAsync(null);
            }
            qemu?.Dispose();
            // Restore choices, retaining metadata for the actually launched mode
            // so that a later renderer change still cold boots safely.
            emulator.SetPerformanceMode(originalPerformanceMode);
            emulator.SetOpenGlMode(originalOpenGlMode);
            var report = new
            {
                utc = DateTimeOffset.UtcNow,
                serverGC = GCSettings.IsServerGC,
                gcConfiguration = GC.GetConfigurationVariables(),
                gcLatency = GCSettings.LatencyMode.ToString(),
                animationPort = animation.Port,
                sourceWarmupReports,
                measurement,
                fgoObservation,
                browser = animation.Reports.ToArray(),
                failure,
                note = "Source FPS is Chromium requestAnimationFrame; submitted FPS is MainWindow DrawImage submission, not display scan-out or a game benchmark.",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Program.Output)!);
            await File.WriteAllTextAsync(Program.Output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Report: " + Program.Output);
        }

        async Task<string> AdbAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(sdk, "platform-tools", "adb.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = sdk,
            };
            start.ArgumentList.Add("-s"); start.ArgumentList.Add("emulator-5554");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            var result = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("ADB diagnostic command failed: " + error);
            return result;
        }

        async Task CloseOwnedPageAsync()
        {
            int forwardPort = 0;
            try
            {
                if (!int.TryParse((await AdbAsync("forward", "tcp:0", "localabstract:chrome_devtools_remote")).Trim(), out forwardPort)) return;
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
                var endpoint = $"http://127.0.0.1:{forwardPort}";
                using var tabs = JsonDocument.Parse(await http.GetStringAsync(endpoint + "/json/list"));
                var ownedUrl = $"http://10.0.2.2:{animation.Port}/";
                var owned = tabs.RootElement.EnumerateArray().Where(tab =>
                    tab.TryGetProperty("url", out var url) && url.GetString() == ownedUrl).ToArray();
                var closed = 0;
                foreach (var tab in owned)
                {
                    if (!tab.TryGetProperty("id", out var id)) continue;
                    using var response = await http.GetAsync(endpoint + "/json/close/" + Uri.EscapeDataString(id.GetString()!));
                    if (response.IsSuccessStatusCode) closed++;
                }
                Console.WriteLine($"Closed owned diagnostic tabs: {closed}");
            }
            catch { Console.WriteLine("Exact diagnostic-tab close unavailable; requested page stop and restoring Android foreground."); }
            finally
            {
                if (forwardPort != 0) try { await AdbAsync("forward", "--remove", $"tcp:{forwardPort}"); } catch { }
            }
        }
    }

    private static T PrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("Diagnostic field missing: " + name));

    private static string? Foreground(string activities)
    {
        var match = Regex.Match(activities, @"(?:mResumedActivity|topResumedActivity)[:=].*?\s([A-Za-z0-9_.]+/[A-Za-z0-9_.$]+)");
        return match.Success ? match.Groups[1].Value : null;
    }
}

