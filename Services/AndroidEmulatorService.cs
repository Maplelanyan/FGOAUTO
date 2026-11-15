using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using FgoAuto.Models;

namespace FgoAuto.Services;

/// <summary>Owns the single Android 14 Google Play AVD used by FgoAuto.</summary>
public sealed class AndroidEmulatorService : IDisposable, IAsyncDisposable
{
    private const string AvdName = "FgoAuto_API_34";
    private const string AndroidCliUrl = "https://dl.google.com/android/cli/latest/windows_x86_64/android.exe";
    private const string AndroidCliPayloadUrl = "https://dl.google.com/android/cli/latest/windows_x86_64/android-cli.exe";
    private const string PlatformToolsPackage = "platform-tools";
    private const string EmulatorPackage = "emulator@37.2.13";
    private const string SystemImagePackage = "system-images/android-34/google_apis_playstore/x86_64@14";
    private const int ConsolePort = 5554;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly string _runtimeRoot;
    private readonly string _sdkRoot;
    private readonly string _cliPath;
    private readonly string _avdHome;
    private readonly string _userHome;
    private readonly string _avdDirectory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _outputLock = new();
    private Process? _emulatorProcess;
    private EmulatorGrpcClient? _grpc;
    private CancellationTokenSource? _streamCancellation;
    private Task? _streamTask;
    private Task? _previewTask;
    private readonly SemaphoreSlim _previewSignal = new(0, 1);
    private VideoPreviewFrame? _pendingPreview;
    private GameFrame? _latestFrame;
    private readonly string _videoSettingsPath;
    private TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private string _status = "尚未准备 Android 模拟器";

    public AndroidEmulatorService(string localDataDirectory)
    {
        _runtimeRoot = Path.Combine(Path.GetFullPath(localDataDirectory), "android-runtime");
        _sdkRoot = Path.Combine(_runtimeRoot, "sdk");
        _cliPath = Path.Combine(_runtimeRoot, "android-cli", "android.exe");
        _avdHome = Path.Combine(_runtimeRoot, "avd");
        _userHome = Path.Combine(_runtimeRoot, "android-home");
        _avdDirectory = Path.Combine(_avdHome, $"{AvdName}.avd");
        _videoSettingsPath = Path.Combine(_runtimeRoot, "video-settings.json");
        try
        {
            if (File.Exists(_videoSettingsPath))
                UsePerformanceMode = JsonSerializer.Deserialize<VideoSettings>(File.ReadAllText(_videoSettingsPath))?.UsePerformanceMode ?? false;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public event Action? FrameAvailable;
    public event Action<string>? StatusChanged;
    public event Action<string>? LogReceived;

    public string Status { get => _status; private set { _status = value; StatusChanged?.Invoke(value); } }
    public bool IsRunning => _emulatorProcess is { HasExited: false } && _grpc is not null;
    public bool HasLiveScreenshotStream => _streamCancellation is { IsCancellationRequested: false }
        && _streamTask is { IsCompleted: false } && _previewTask is { IsCompleted: false };
    public bool HasPendingPreviewFrame => Volatile.Read(ref _pendingPreview) is not null;
    public bool UsePerformanceMode { get; private set; }
    public FrameStatistics FrameStatistics { get; } = new();
    public bool IsPrepared => File.Exists(Path.Combine(_sdkRoot, "emulator", "emulator.exe"))
        && File.Exists(Path.Combine(_sdkRoot, "platform-tools", "adb.exe"))
        && File.Exists(Path.Combine(_sdkRoot, "system-images", "android-34", "google_apis_playstore", "x86_64", "system.img"))
        && File.Exists(Path.Combine(_avdDirectory, "config.ini"));

    public VideoPreviewFrame? TakeLatestPreviewFrame() => Interlocked.Exchange(ref _pendingPreview, null);

    public void SetPerformanceMode(bool enabled)
    {
        Directory.CreateDirectory(_runtimeRoot);
        File.WriteAllText(_videoSettingsPath, JsonSerializer.Serialize(new VideoSettings(enabled)));
        UsePerformanceMode = enabled;
    }

    public async Task PrepareAsync(bool userAcceptedRequiredLicenses, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!userAcceptedRequiredLicenses)
            throw new InvalidOperationException("请先阅读并接受 Android SDK 与 Google Play 相关许可，再准备模拟器。");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        cancellationToken = linkedCancellation.Token;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning) throw new InvalidOperationException("请先停止模拟器，再安装或修复组件。");
            Directory.CreateDirectory(_runtimeRoot);
            Directory.CreateDirectory(_sdkRoot);
            Directory.CreateDirectory(_avdHome);
            Directory.CreateDirectory(_userHome);
            EnsureFreeSpace();

            if (!File.Exists(_cliPath))
            {
                SetStatus("下载 Android SDK 管理工具…");
                progress?.Report(Status);
                await DownloadAndroidCliAsync(progress, cancellationToken).ConfigureAwait(false);
            }

            await InstallSdkPackageAsync(PlatformToolsPackage, progress, cancellationToken).ConfigureAwait(false);
            await InstallSdkPackageAsync(EmulatorPackage, progress, cancellationToken).ConfigureAwait(false);
            await InstallSdkPackageAsync(SystemImagePackage, progress, cancellationToken).ConfigureAwait(false);
            CreateOrUpdateAvd();
            await WriteRuntimeManifestAsync(cancellationToken).ConfigureAwait(false);
            SetStatus("Android 14 模拟器已准备好；可以启动并在预览里完成 FGO 安装与登录。");
            progress?.Report(Status);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StartAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        cancellationToken = linkedCancellation.Token;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning) return;
            if (!IsPrepared) throw new InvalidOperationException("Android 模拟器尚未准备。先阅读并接受许可，再点“准备模拟器”。");

            var emulatorExe = Path.Combine(_sdkRoot, "emulator", "emulator.exe");
            SetStatus("检查 Windows 虚拟机加速…");
            progress?.Report(Status);
            var accel = await RunProcessCaptureAsync(emulatorExe, ["-accel-check"], _sdkRoot, cancellationToken).ConfigureAwait(false);
            if (accel.ExitCode != 0 || !accel.Output.Contains("usable", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Android Emulator 检测不到可用的硬件虚拟化加速。请在 Windows 中检查 WHPX/VT-x 后重试；FgoAuto 没有修改系统设置。{Environment.NewLine}{accel.Output.Trim()}");

            Directory.CreateDirectory(_userHome);
            lock (_outputLock) _lastRuntimeOutput = string.Empty;
            var profile = EmulatorGameProfile.Create(UsePerformanceMode);
            var profileChanged = profile.ApplyToConfig(Path.Combine(_avdDirectory, "config.ini"));
            var startArguments = new List<string>
            {
                "-avd", AvdName, "-no-window", "-port", ConsolePort.ToString(), "-grpc", "localhost:8554", "-grpc-use-token",
                "-gpu", "host", "-no-boot-anim", "-no-metrics"
            };
            // A saved VM snapshot retains the old display hardware. Changing the
            // profile must cold boot once; installed apps and user data are retained.
            if (profileChanged) startArguments.Add("-no-snapshot-load");
            LogReceived?.Invoke($"游戏运行配置：Android 实际渲染 {profile.Width} × {profile.Height} @ {profile.Density} dpi；主机 GPU；关闭摄像头、GPS、麦克风；模板逻辑坐标仍为 1920 × 1080。" );
            var startInfo = CreateProcessStartInfo(emulatorExe, startArguments);
            _emulatorProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _emulatorProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendRuntimeOutput(e.Data); };
            _emulatorProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendRuntimeOutput(e.Data); };
            if (!_emulatorProcess.Start()) throw new InvalidOperationException("无法启动 Android Emulator。");
            _emulatorProcess.BeginOutputReadLine();
            _emulatorProcess.BeginErrorReadLine();
            SetStatus("模拟器启动中，等待 gRPC 控制端点…");
            progress?.Report(Status);

            var credential = await WaitForGrpcCredentialsAsync(_emulatorProcess, cancellationToken).ConfigureAwait(false);
            _grpc = new EmulatorGrpcClient(credential.Port, credential.Token,
                maxWidth: UsePerformanceMode ? 1280 : 0, maxHeight: UsePerformanceMode ? 1280 : 0,
                deviceWidth: profile.Width, deviceHeight: profile.Height);
            SetStatus("Android 系统启动中…");
            progress?.Report(Status);
            var booted = false;
            var bootDeadline = Stopwatch.StartNew();
            while (bootDeadline.Elapsed < TimeSpan.FromMinutes(4))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_emulatorProcess.HasExited)
                    throw new InvalidOperationException($"Android Emulator 已退出（代码 {_emulatorProcess.ExitCode}）。请查看运行记录。{Environment.NewLine}{LastRuntimeOutput}");
                try
                {
                    if (LastRuntimeOutput.Contains("Boot completed", StringComparison.OrdinalIgnoreCase)
                        || await _grpc.IsBootedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        booted = true;
                        break;
                    }
                }
                catch (RpcException) { }
                await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            }
            if (!booted) throw new TimeoutException("Android 系统在 4 分钟内没有完成启动。请停止模拟器后重试。" + Environment.NewLine + LastRuntimeOutput);

            _streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            FrameStatistics.Reset();
            var streamToken = _streamCancellation.Token;
            var client = _grpc;
            _previewTask = Task.Run(() => RunPreviewConversionAsync(streamToken), streamToken);
            _streamTask = Task.Run(() => client.RunScreenshotStreamAsync(ReceiveFrame, streamToken), streamToken);
            _ = ObserveScreenshotStreamAsync(_streamTask, _streamCancellation.Token);
            await _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            SetStatus("Android 模拟器已就绪；点击右侧预览可操作 Play 商店并安装 FGO。");
            progress?.Report(Status);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public GameFrame? CaptureLatestFrame()
    {
        return Volatile.Read(ref _latestFrame);
    }

    public async Task<GameFrame> CaptureFreshFrameAsync(CancellationToken cancellationToken = default)
    {
        var client = _grpc ?? throw new InvalidOperationException("Android 模拟器尚未启动。");
        var previous = Volatile.Read(ref _latestFrame);
        var deadline = Stopwatch.StartNew();
        while (HasLiveScreenshotStream && deadline.Elapsed < TimeSpan.FromMilliseconds(80))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = Volatile.Read(ref _latestFrame);
            if (latest is not null && latest.FrameId != previous?.FrameId && latest.Age < TimeSpan.FromMilliseconds(150))
                return latest;
            await Task.Delay(4, cancellationToken).ConfigureAwait(false);
        }
        // A static Android display may not publish a new streaming frame. A unary
        // capture then confirms that the screen is still current before touching it.
        return await client.CaptureFrameAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SendTouchAsync(int x, int y, int pressure, int identifier = 1, CancellationToken cancellationToken = default)
    {
        var client = _grpc ?? throw new InvalidOperationException("Android 模拟器尚未启动。");
        return client.SendTouchAsync(x, y, pressure, identifier, cancellationToken);
    }

    private async Task InstallSdkPackageAsync(string package, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        SetStatus($"正在下载并安装 Android SDK 组件：{package} …");
        progress?.Report(Status);
        var args = new List<string> { $"--sdk={_sdkRoot}", "--no-metrics", "sdk", "install", package };
        var payloadLength = await GetAndroidCliPayloadLengthAsync(cancellationToken).ConfigureAwait(false);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitorTask = MonitorAndroidCliPayloadDownloadAsync(payloadLength, progress, monitorCancellation.Token);
        ProcessResult result;
        try
        {
            result = await RunAndroidCliAsync(package, args, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            monitorCancellation.Cancel();
            try { await monitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { }
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"安装 Android SDK 组件“{package}”失败。{Environment.NewLine}{result.Output}");
        foreach (var line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).TakeLast(8))
            LogReceived?.Invoke(line);
    }

    private async Task<long?> GetAndroidCliPayloadLengthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Head, AndroidCliPayloadUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return GetDownloadLength(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task MonitorAndroidCliPayloadDownloadAsync(long? totalLength, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var binDirectory = Path.Combine(_userHome, "bin");
        var partialPath = Path.Combine(binDirectory, "android-cli.part");
        var completedPaths = new[]
        {
            Path.Combine(binDirectory, "android-cli.exe"),
            Path.Combine(binDirectory, "android-cli")
        };
        var previousLength = -1L;
        var previousReport = Stopwatch.StartNew();
        var previousReportLength = 0L;

        while (!cancellationToken.IsCancellationRequested)
        {
            var downloadedLength = TryGetFileLength(partialPath);
            if (downloadedLength is null)
                downloadedLength = completedPaths.Select(TryGetFileLength).FirstOrDefault(length => length.HasValue);
            if (downloadedLength is { } bytes && (bytes != previousLength || previousReport.Elapsed >= TimeSpan.FromSeconds(1)))
            {
                var elapsedSeconds = Math.Max(previousReport.Elapsed.TotalSeconds, 0.001);
                var speedMb = Math.Max(0, bytes - previousReportLength) / elapsedSeconds / (1024d * 1024d);
                var receivedMb = bytes / (1024d * 1024d);
                var message = totalLength is > 0
                    ? $"下载 Android CLI 核心组件：{receivedMb:F1} / {totalLength.Value / (1024d * 1024d):F1} MB ({Math.Clamp(bytes * 100d / totalLength.Value, 0, 100):F0}%) · {speedMb:F1} MB/s"
                    : $"下载 Android CLI 核心组件：已接收 {receivedMb:F1} MB · {speedMb:F1} MB/s";
                progress?.Report(message);
                previousLength = bytes;
                previousReportLength = bytes;
                previousReport.Restart();
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long? TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static long? GetDownloadLength(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength is > 0)
            return response.Content.Headers.ContentLength;
        if (response.Headers.TryGetValues("X-Identity-Content-Length", out var headerValues)
            && long.TryParse(headerValues.FirstOrDefault(), out var identityLength)
            && identityLength > 0)
            return identityLength;
        if (response.Content.Headers.TryGetValues("X-Identity-Content-Length", out var contentValues)
            && long.TryParse(contentValues.FirstOrDefault(), out identityLength)
            && identityLength > 0)
            return identityLength;
        return null;
    }

    private async Task DownloadAndroidCliAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cliPath)!;
        Directory.CreateDirectory(directory);
        var temporary = _cliPath + ".download";
        try
        {
            using var response = await Http.GetAsync(AndroidCliUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = GetDownloadLength(response);
            if (length is < 1_000_000 or > 500_000_000)
                throw new InvalidDataException($"Android CLI 下载大小异常：{length} 字节。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            long downloadedLength;
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[256 * 1024];
                var lastReport = Stopwatch.StartNew();
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (lastReport.ElapsedMilliseconds < 200 && length.HasValue && target.Length < length.Value)
                        continue;
                    var downloadedMb = target.Length / (1024d * 1024d);
                    var message = length is > 0
                        ? $"下载 Android SDK 管理工具：{downloadedMb:F1} / {length.Value / (1024d * 1024d):F1} MB ({target.Length * 100d / length.Value:F0}%)"
                        : $"下载 Android SDK 管理工具：已接收 {downloadedMb:F1} MB";
                    progress?.Report(message);
                    lastReport.Restart();
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                downloadedLength = target.Length;
            }
            if (length.HasValue && downloadedLength != length.Value)
                throw new EndOfStreamException("Android CLI 文件下载不完整。");
            File.Move(temporary, _cliPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<ProcessResult> RunAndroidCliAsync(string package, IReadOnlyList<string> args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var info = CreateProcessStartInfo(_cliPath, args);
        info.RedirectStandardInput = true;
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Android SDK 管理工具。");
        var outputLock = new object();
        var output = new StringBuilder();
        void ReceiveOutput(object? _, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data)) return;
            lock (outputLock) output.AppendLine(eventArgs.Data);
            LogReceived?.Invoke(eventArgs.Data);
            progress?.Report($"安装 Android SDK 组件：{package} · {eventArgs.Data}");
        }
        process.OutputDataReceived += ReceiveOutput;
        process.ErrorDataReceived += ReceiveOutput;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // The user has explicitly accepted the Android SDK license in FgoAuto.
        // The package set uses android-sdk-license; answer its interactive review prompts.
        try
        {
            await process.StandardInput.WriteAsync("y\ny\ny\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            throw;
        }
        process.WaitForExit();
        string capturedOutput;
        lock (outputLock) capturedOutput = output.ToString();
        return new ProcessResult(process.ExitCode, capturedOutput);
    }

    private async Task<ProcessResult> RunProcessCaptureAsync(string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        info.Environment["ANDROID_HOME"] = _sdkRoot;
        info.Environment["ANDROID_SDK_ROOT"] = _sdkRoot;
        info.Environment["ANDROID_AVD_HOME"] = _avdHome;
        info.Environment["ANDROID_USER_HOME"] = _userHome;
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            throw;
        }
        var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
        return new ProcessResult(process.ExitCode, output);
    }

    private void CreateOrUpdateAvd()
    {
        var systemImage = Path.Combine(_sdkRoot, "system-images", "android-34", "google_apis_playstore", "x86_64");
        if (!File.Exists(Path.Combine(systemImage, "system.img")))
            throw new FileNotFoundException("Android 14 Google Play 系统镜像未完整安装。", systemImage);
        Directory.CreateDirectory(_avdDirectory);
        File.WriteAllText(Path.Combine(_avdHome, $"{AvdName}.ini"),
            $"avd.ini.encoding=UTF-8{Environment.NewLine}path={_avdDirectory}{Environment.NewLine}path.rel={AvdName}.avd{Environment.NewLine}", Encoding.UTF8);
        var config = string.Join(Environment.NewLine,
        [
            "AvdId=FgoAuto_API_34", "avd.ini.displayname=FgoAuto Android 14", "abi.type=x86_64",
            "hw.cpu.arch=x86_64", "hw.cpu.ncore=4", "hw.ramSize=4096", "vm.heapSize=512",
            "hw.lcd.width=1920", "hw.lcd.height=1080", "hw.lcd.density=280",
            "hw.gpu.enabled=yes", "hw.gpu.mode=host", "hw.keyboard=yes", "hw.mainKeys=no",
            "hw.sensors.orientation=yes", "hw.accelerometer=yes", "hw.audioInput=no", "hw.audioOutput=yes",
            "disk.dataPartition.size=8G", "PlayStore.enabled=true", "tag.id=google_apis_playstore",
            "tag.display=Google Play", "target=android-34", "image.sysdir.1=system-images/android-34/google_apis_playstore/x86_64/",
            "fastboot.forceColdBoot=no", "fastboot.forceFastBoot=yes", "fastboot.chosenSnapshotFile=default_boot"
        ]) + Environment.NewLine;
        File.WriteAllText(Path.Combine(_avdDirectory, "config.ini"), config, Encoding.UTF8);
        EmulatorGameProfile.Create(UsePerformanceMode).ApplyToConfig(Path.Combine(_avdDirectory, "config.ini"));
    }

    private async Task WriteRuntimeManifestAsync(CancellationToken cancellationToken)
    {
        var emulatorProperties = Path.Combine(_sdkRoot, "emulator", "source.properties");
        var platformToolsProperties = Path.Combine(_sdkRoot, "platform-tools", "source.properties");
        var imageProperties = Path.Combine(_sdkRoot, "system-images", "android-34", "google_apis_playstore", "x86_64", "source.properties");
        var manifest = new
        {
            avd = AvdName,
            androidApi = 34,
            sdkManager = "Google Android CLI",
            platformToolsRevision = ReadProperty(platformToolsProperties, "Pkg.Revision"),
            emulatorRevision = ReadProperty(emulatorProperties, "Pkg.Revision"),
            systemImageRevision = ReadProperty(imageProperties, "Pkg.Revision"),
            preparedUtc = DateTime.UtcNow,
            display = UsePerformanceMode ? "1280x720@187" : "1920x1080@280",
            templateCoordinates = "1920x1080",
            gameProfileVersion = 1
        };
        Directory.CreateDirectory(_runtimeRoot);
        await File.WriteAllTextAsync(Path.Combine(_runtimeRoot, "runtime-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
    }

    private static string ReadProperty(string path, string key)
    {
        if (!File.Exists(path)) return "unknown";
        return File.ReadLines(path).Select(line => line.Split('=', 2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))?[1] ?? "unknown";
    }

    private void EnsureFreeSpace()
    {
        var drive = new DriveInfo(Path.GetPathRoot(_runtimeRoot)!);
        if (drive.AvailableFreeSpace < 8L * 1024 * 1024 * 1024)
            throw new IOException("准备 Android 14 模拟器至少需要约 8 GB 可用空间。");
    }

    private ProcessStartInfo CreateProcessStartInfo(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Directory.Exists(_sdkRoot) ? _sdkRoot : _runtimeRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        info.Environment["ANDROID_HOME"] = _sdkRoot;
        info.Environment["ANDROID_SDK_ROOT"] = _sdkRoot;
        info.Environment["ANDROID_AVD_HOME"] = _avdHome;
        info.Environment["ANDROID_USER_HOME"] = _userHome;
        info.Environment["ANDROID_EMULATOR_HOME"] = _userHome;
        return info;
    }

    private async Task<(int Port, string Token)> WaitForGrpcCredentialsAsync(Process process, CancellationToken cancellationToken)
    {
        var directories = new[]
        {
            Path.Combine(Path.GetTempPath(), "avd", "running"),
            Path.Combine(_userHome, "avd", "running"),
            Path.Combine(_userHome, "running"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd", "running")
        };
        var deadline = Stopwatch.StartNew();
        var processStartedAt = process.StartTime.ToUniversalTime();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"Android Emulator 已退出（代码 {process.ExitCode}）。{Environment.NewLine}{LastRuntimeOutput}");
            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory)) continue;
                // emulator.exe launches a qemu-system child; the discovery file uses the
                // child's PID, not the launcher PID. Ignore stale files and only accept a
                // live QEMU process started after this launch.
                foreach (var path in Directory.EnumerateFiles(directory, "pid_*.ini"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    if (!fileName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)
                        || !int.TryParse(fileName.AsSpan(4), out var emulatorPid)
                        || File.GetLastWriteTimeUtc(path) < processStartedAt)
                        continue;

                    try
                    {
                        using var emulatorProcess = Process.GetProcessById(emulatorPid);
                        if (!emulatorProcess.ProcessName.StartsWith("qemu-system-x86_64", StringComparison.OrdinalIgnoreCase)
                            || emulatorProcess.StartTime.ToUniversalTime() < processStartedAt)
                            continue;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        continue;
                    }

                    var values = File.ReadLines(path).Select(line => line.Split('=', 2))
                        .Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
                    if (!values.TryGetValue("port.serial", out var serial) || serial != ConsolePort.ToString()) continue;
                    if (!values.TryGetValue("avd.id", out var avdId) || !avdId.Equals(AvdName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!values.TryGetValue("avd.dir", out var avdDirectory)
                        || !Path.GetFullPath(avdDirectory).Equals(Path.GetFullPath(_avdDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                    if (int.TryParse(values.GetValueOrDefault("grpc.port"), out var port)
                        && values.TryGetValue("grpc.token", out var token) && !string.IsNullOrWhiteSpace(token))
                        return (port, token);
                }
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("没有找到 Android Emulator 的本地 gRPC 端点。" + Environment.NewLine + LastRuntimeOutput);
    }

    private string LastRuntimeOutput
    {
        get { lock (_outputLock) return _lastRuntimeOutput; }
    }
    private string _lastRuntimeOutput = string.Empty;

    private void AppendRuntimeOutput(string line)
    {
        lock (_outputLock)
        {
            _lastRuntimeOutput = (_lastRuntimeOutput + line + Environment.NewLine)[^Math.Min(12_000, _lastRuntimeOutput.Length + line.Length + Environment.NewLine.Length)..];
        }
        LogReceived?.Invoke(line);
    }

    private void ReceiveFrame(GameFrame frame, long sourceSequence)
    {
        Volatile.Write(ref _latestFrame, frame);
        FrameStatistics.Record(sourceSequence, frame.ReceivedAtTimestamp);
        try { _previewSignal.Release(); }
        catch (SemaphoreFullException) { } // Only a single latest-frame notification is needed.
    }

    private async Task RunPreviewConversionAsync(CancellationToken cancellationToken)
    {
        long lastConverted = -1;
        try
        {
            while (true)
            {
                await _previewSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                var frame = Volatile.Read(ref _latestFrame);
                if (frame is null || frame.FrameId == lastConverted) continue;
                var preview = EmulatorGrpcClient.ToPreviewFrame(frame);
                lastConverted = frame.FrameId;
                Interlocked.Exchange(ref _pendingPreview, preview)?.Dispose();
                _firstFrame.TrySetResult();
                FrameAvailable?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _firstFrame.TrySetException(error);
            _streamCancellation?.Cancel();
            Interlocked.Exchange(ref _pendingPreview, null)?.Dispose();
            SetStatus($"模拟器截图流预览转换失败：{error.Message}。请重启模拟器。");
        }
    }

    private async Task ObserveScreenshotStreamAsync(Task streamTask, CancellationToken cancellationToken)
    {
        try
        {
            await streamTask.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                SetStatus("模拟器截图流意外结束。请重启模拟器以恢复预览和自动识别。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _firstFrame.TrySetException(new InvalidOperationException("模拟器截图流在首帧前中断。", exception));
                _streamCancellation?.Cancel();
                SetStatus($"模拟器截图流中断：{_grpc?.SanitizeError(exception) ?? exception.GetType().Name}。请重启模拟器恢复。");
            }
        }
    }

    private async Task StopCoreAsync()
    {
        var process = _emulatorProcess;
        if (process is null)
        {
            _grpc?.Dispose();
            _grpc = null;
            return;
        }

        SetStatus("正在安全关闭 Android 模拟器…");
        var grpc = _grpc;
        try
        {
            if (grpc is not null)
                await grpc.StopGuestAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (Exception error) { LogReceived?.Invoke($"模拟器正常关机请求失败：{grpc?.SanitizeError(error) ?? error.GetType().Name}"); }

        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            LogReceived?.Invoke("模拟器没有在关机期限内退出，正在结束其进程。");
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        }

        _streamCancellation?.Cancel();
        if (_streamTask is not null)
        {
            try { await _streamTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        if (_previewTask is not null)
        {
            try { await _previewTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _previewTask = null;
        _streamCancellation?.Dispose();
        _streamCancellation = null;
        _streamTask = null;
        _grpc?.Dispose();
        _grpc = null;
        _emulatorProcess = null;
        process.Dispose();
        Interlocked.Exchange(ref _pendingPreview, null)?.Dispose();
        Volatile.Write(ref _latestFrame, null);
        SetStatus("Android 模拟器已停止。");
    }

    private void SetStatus(string status) { Status = status; LogReceived?.Invoke(status); }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(AndroidEmulatorService)); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _lifecycle.Dispose();
            _previewSignal.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed record VideoSettings(bool UsePerformanceMode);
}
