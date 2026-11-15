using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FgoAuto.Models;
using OpenCvSharp;

namespace FgoAuto.PerformanceProbe;

/// <summary>A video-only scrcpy session. No control socket, audio, APK installation or input events.</summary>
public sealed class H264ReadOnlySession : IDisposable, IAsyncDisposable
{
    private const string ServerVersion = "5.0.1";
    private readonly string RemotePath = "/data/local/tmp/fgoauto-perf-" + Guid.NewGuid().ToString("N") + ".jar";
    private readonly string _adbPath;
    private readonly string _serial;
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TcpListener _decoderListener = new(IPAddress.Loopback, 0);
    private Process? _server;
    private TcpClient? _remote;
    private TcpClient? _decoder;
    private Task? _bridgeTask;
    private Task? _decodeTask;
    private VideoPreviewFrame? _pending;
    private Exception? _failure;
    private long _sequence;
    private int _forwardPort;
    private int _disposed;
    private long _encodedBytes;
    private string _serverLog = string.Empty;
    private readonly object _logSync = new();

    private H264ReadOnlySession(string adbPath, string serial, CancellationToken token)
    {
        _adbPath = adbPath;
        _serial = serial;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public long DecodedFrames => Interlocked.Read(ref _sequence);
    public long EncodedBytes => Interlocked.Read(ref _encodedBytes);
    public string ServerLog => Volatile.Read(ref _serverLog);
    public Exception? Failure => Volatile.Read(ref _failure);
    public event Action? FrameAvailable;
    public VideoPreviewFrame? TakeLatestFrame() => Interlocked.Exchange(ref _pending, null);

    // Set before OpenCV loads its native libraries, including in diagnostic/test hosts.
    public static void ConfigureDecoder()
    {
        const string options = "input_format;h264|fflags;nobuffer|flags;low_delay|probesize;32|analyzeduration;1|fpsprobesize;0";
        SetNativeEnvironment("OPENCV_FFMPEG_CAPTURE_OPTIONS", options);
        SetNativeEnvironment("OPENCV_FFMPEG_THREADS", "1");
    }

    private static void SetNativeEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        // OpenCV reads the C runtime environment, which Windows keeps separately from the process block.
        if (OperatingSystem.IsWindows())
        {
            PutEnvironment(name, value);
            // The bundled FFmpeg backend uses the legacy C runtime, while OpenCV uses UCRT.
            PutLegacyEnvironment(name + "=" + value);
        }
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PutEnvironment(string name, string value);

    [DllImport("msvcrt.dll", EntryPoint = "_putenv", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int PutLegacyEnvironment(string assignment);

    public static async Task<H264ReadOnlySession> StartAsync(string adbPath, string serial, CancellationToken token = default)
    {
        var session = new H264ReadOnlySession(adbPath, serial, token);
        try
        {
            ConfigureDecoder();
            var helper = Path.Combine(AppContext.BaseDirectory, "Assets", "scrcpy-server-v5.0.1");
            if (!File.Exists(helper)) throw new FileNotFoundException("缺少视频预览组件。", helper);
            await session.RunAdbAsync(["push", helper, session.RemotePath], token);
            var scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");
            var port = await session.RunAdbAsync(["forward", "tcp:0", "localabstract:scrcpy_" + scid], token);
            session._forwardPort = int.Parse(port.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            session._decoderListener.Start();
            var decoderPort = ((IPEndPoint)session._decoderListener.LocalEndpoint).Port;
            var options = $"CLASSPATH={session.RemotePath} app_process / com.genymobile.scrcpy.Server {ServerVersion} "
                + $"scid={scid} tunnel_forward=true audio=false control=false cleanup=false "
                + "raw_stream=true send_dummy_byte=true max_size=1280 max_fps=60 video_bit_rate=12000000 "
                + "video_codec_options=i-frame-interval=1";
            session._server = session.StartAdb(["shell", "-T", options]);
            _ = session.ReadServerLogAsync(session._server.StandardOutput);
            _ = session.ReadServerLogAsync(session._server.StandardError);
            session._bridgeTask = session.BridgeAsync(session._stop.Token);
            session._decodeTask = Task.Factory.StartNew(() => session.Decode(decoderPort),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await session._ready.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task BridgeAsync(CancellationToken token)
    {
        try
        {
            _decoder = await _decoderListener.AcceptTcpClientAsync(token);
            _decoder.NoDelay = true;
            _decoder.SendBufferSize = 64 * 1024;
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                _remote = new TcpClient { NoDelay = true };
                try
                {
                    await _remote.ConnectAsync(IPAddress.Loopback, _forwardPort, token);
                    var handshake = new byte[1];
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(500);
                    var count = await _remote.GetStream().ReadAsync(handshake, attempt.Token);
                    if (count == 1 && handshake[0] == 0) break;
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                }
                _remote.Dispose();
                if (deadline.Elapsed.TotalSeconds > 6 || _server!.HasExited)
                    throw new IOException("设备端视频连接未能建立。请重试预览。");
                await Task.Delay(50, token);
            }
            var encoded = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int count;
                while ((count = await _remote.GetStream().ReadAsync(encoded, token)) > 0)
                {
                    Interlocked.Add(ref _encodedBytes, count);
                    await _decoder.GetStream().WriteAsync(encoded.AsMemory(0, count), token);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(encoded); }
            if (!token.IsCancellationRequested) throw new IOException("设备端视频连接已断开。");
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) Fail(error);
        }
        finally { _decoder?.Dispose(); _remote?.Dispose(); }
    }

    private void Decode(int port)
    {
        try
        {
            using var capture = new VideoCapture($"tcp://127.0.0.1:{port}", VideoCaptureAPIs.FFMPEG,
                [53, 8000, 54, 2000]); // OpenCV open/read timeouts, in milliseconds.
            if (!capture.IsOpened()) throw new IOException("无法解码设备的视频流。请重试预览。");
            using var bgr = new Mat();
            using var bgra = new Mat();
            while (!_stop.IsCancellationRequested)
            {
                if (!capture.Read(bgr) || bgr.Empty())
                {
                    if (_stop.IsCancellationRequested || Failure is not null) break;
                    // Static screens may stop producing packets. Resume on the next changed frame.
                    Thread.Sleep(10);
                    continue;
                }
                Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
                var length = checked(bgra.Width * bgra.Height * 4);
                var pixels = ArrayPool<byte>.Shared.Rent(length);
                Marshal.Copy(bgra.Data, pixels, 0, length);
                var frame = new VideoPreviewFrame(bgra.Width, bgra.Height, pixels, Interlocked.Increment(ref _sequence));
                Interlocked.Exchange(ref _pending, frame)?.Dispose();
                _ready.TrySetResult();
                FrameAvailable?.Invoke();
            }
        }
        catch (Exception error)
        {
            if (!_stop.IsCancellationRequested) Fail(error);
        }
        finally { Interlocked.Exchange(ref _pending, null)?.Dispose(); }
    }

    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _failure, error, null);
        _ready.TrySetException(error);
        _stop.Cancel();
        _decoder?.Dispose();
    }

    private async Task ReadServerLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                if (line.Contains("encoder", StringComparison.OrdinalIgnoreCase) || line.Contains("ERROR", StringComparison.Ordinal))
                    lock (_logSync)
                    {
                        var log = _serverLog + line + Environment.NewLine;
                        Volatile.Write(ref _serverLog, log[^Math.Min(2000, log.Length)..]);
                    }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private Process StartAdb(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(_adbPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("-s"); info.ArgumentList.Add(_serial);
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("无法启动 ADB 视频连接。");
    }

    private async Task<string> RunAdbAsync(string[] arguments, CancellationToken token)
    {
        using var process = StartAdb(arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var registration = timeout.Token.Register(() => Kill(process));
        await process.WaitForExitAsync(timeout.Token);
        var result = await output;
        if (process.ExitCode != 0) throw new IOException($"ADB 视频连接失败：{await errors}");
        return result;
    }

    private static void Kill(Process? process)
    {
        try { if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ready.TrySetCanceled();
        _decoderListener.Stop();
        _decoder?.Dispose(); _remote?.Dispose();
        Kill(_server);
        _server?.Dispose();
        Interlocked.Exchange(ref _pending, null)?.Dispose();
        var port = Interlocked.Exchange(ref _forwardPort, 0);
        if (port != 0)
        {
            try
            {
                using var remove = StartAdb(["forward", "--remove", "tcp:" + port]);
                if (!remove.WaitForExit(1500)) Kill(remove);
            }
            catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) { }
        }
        _stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        var tasks = new[] { _bridgeTask, _decodeTask }.OfType<Task>().ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        // This per-session, randomly named helper is the only guest file removed.
        try { await RunAdbAsync(["shell", "rm", "-f", RemotePath], CancellationToken.None); }
        catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException) { }
    }
}
