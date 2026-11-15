using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Grpc.Core;
using Grpc.Net.Client;
using OpenCvSharp;
using FgoAuto.Models;

namespace FgoAuto.Services;

/// <summary>
/// Small wire-compatible client for the screenshot and input RPCs exposed by
/// the Android Emulator. Message fields follow emulator_controller.proto.
/// The endpoint is loopback-only and uses the per-process discovery token.
/// </summary>
internal sealed class EmulatorGrpcClient : IDisposable
{
    private const string ServiceName = "android.emulation.control.EmulatorController";
    private static readonly Marshaller<byte[]> BytesMarshaller = Marshallers.Create<byte[]>(bytes => bytes, bytes => bytes);
    private static readonly Method<byte[], byte[]> ScreenshotMethod = new(
        MethodType.ServerStreaming, ServiceName, "streamScreenshot", BytesMarshaller, BytesMarshaller);
    private static readonly Method<byte[], byte[]> GetScreenshotMethod = new(
        MethodType.Unary, ServiceName, "getScreenshot", BytesMarshaller, BytesMarshaller);
    private static readonly Method<byte[], byte[]> GetStatusMethod = new(
        MethodType.Unary, ServiceName, "getStatus", BytesMarshaller, BytesMarshaller);
    private static readonly Method<byte[], byte[]> SendTouchMethod = new(
        MethodType.Unary, ServiceName, "sendTouch", BytesMarshaller, BytesMarshaller);
    private static readonly Method<byte[], byte[]> SetVmStateMethod = new(
        MethodType.Unary, ServiceName, "setVmState", BytesMarshaller, BytesMarshaller);

    private readonly GrpcChannel _channel;
    private readonly CallInvoker _invoker;
    private readonly string _token;
    private readonly int _maxFrameBytes;
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _deviceWidth;
    private readonly int _deviceHeight;
    private int _portraitFrame;
    private long _frameNumber;

    public EmulatorGrpcClient(int port, string token, int maxFrameBytes = 32 * 1024 * 1024, int maxWidth = 0, int maxHeight = 0,
        int deviceWidth = 1920, int deviceHeight = 1080)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("缺少模拟器 gRPC 认证令牌。", nameof(token));
        _token = token;
        _maxFrameBytes = maxFrameBytes;
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        if (deviceWidth <= 0 || deviceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(deviceWidth));
        _deviceWidth = Math.Max(deviceWidth, deviceHeight);
        _deviceHeight = Math.Min(deviceWidth, deviceHeight);
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions
        {
            // A full 1080p RGB frame is over 6 MB. Avoid repeated flow-control
            // stalls on a small initial HTTP/2 receive window on loopback.
            HttpHandler = new System.Net.Http.SocketsHttpHandler
            {
                InitialHttp2StreamWindowSize = 8 * 1024 * 1024,
                EnableMultipleHttp2Connections = true,
                UseProxy = false,
            },
            DisposeHttpClient = true,
            MaxReceiveMessageSize = maxFrameBytes,
            MaxSendMessageSize = 1024 * 1024,
        });
        _invoker = _channel.CreateCallInvoker();
    }

    public async Task RunScreenshotStreamAsync(Action<GameFrame, long> onFrame, CancellationToken cancellationToken)
    {
        var call = _invoker.AsyncServerStreamingCall(ScreenshotMethod, null,
            new CallOptions(headers: CreateHeaders(), cancellationToken: cancellationToken), SerializeRgb888Request());
        using (call)
        {
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var frame = DecodeImage(call.ResponseStream.Current, Stopwatch.GetTimestamp(), out var sourceSequence);
                if (frame is not null)
                    onFrame(frame, sourceSequence);
            }
        }
    }

    public async Task<GameFrame> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        using var call = _invoker.AsyncUnaryCall(GetScreenshotMethod, null,
            CreateCallOptions(TimeSpan.FromSeconds(8), cancellationToken), SerializeRgb888Request());
        var bytes = await call.ResponseAsync.ConfigureAwait(false);
        return DecodeImage(bytes, Stopwatch.GetTimestamp(), out _)
            ?? throw new InvalidOperationException("Android 模拟器尚未生成有效屏幕画面。");
    }

    public async Task<bool> IsBootedAsync(CancellationToken cancellationToken = default)
    {
        using var call = _invoker.AsyncUnaryCall(GetStatusMethod, null,
            CreateCallOptions(TimeSpan.FromSeconds(3), cancellationToken), []);
        var bytes = await call.ResponseAsync.ConfigureAwait(false);
        for (var offset = 0; offset < bytes.Length;)
        {
            var key = ReadVarint(bytes, ref offset);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (field == 3 && wire == 0)
                return ReadVarint(bytes, ref offset) != 0;
            SkipField(bytes, ref offset, wire);
        }
        return false;
    }

    public async Task SendTouchAsync(int x, int y, int pressure, int identifier = 1, CancellationToken cancellationToken = default)
    {
        if (pressure is < 0 or > 1024) throw new ArgumentOutOfRangeException(nameof(pressure));
        var mapped = MapTouchCoordinates(x, y);
        using var call = _invoker.AsyncUnaryCall(SendTouchMethod, null,
            CreateCallOptions(TimeSpan.FromSeconds(3), cancellationToken), SerializeTouch(mapped.X, mapped.Y, pressure, identifier));
        await call.ResponseAsync.ConfigureAwait(false);
    }

    private (int X, int Y) MapTouchCoordinates(int x, int y)
    {
        var portrait = Volatile.Read(ref _portraitFrame) != 0;
        return EmulatorGameProfile.MapTouchToDisplay(x, y, !portrait, _deviceWidth, _deviceHeight);
    }

    public async Task StopGuestAsync(CancellationToken cancellationToken = default)
    {
        using var call = _invoker.AsyncUnaryCall(SetVmStateMethod, null,
            CreateCallOptions(TimeSpan.FromSeconds(3), cancellationToken), [0x08, 0x05]);
        await call.ResponseAsync.ConfigureAwait(false);
    }

    public string SanitizeError(Exception exception)
    {
        var message = exception is RpcException rpc ? rpc.Status.ToString() : exception.Message;
        if (string.IsNullOrEmpty(_token)) return message;
        return message.Replace($"Bearer {_token}", "Bearer [已隐藏]", StringComparison.Ordinal)
            .Replace(_token, "[已隐藏]", StringComparison.Ordinal);
    }

    public static unsafe VideoPreviewFrame ToPreviewFrame(GameFrame frame)
    {
        var length = checked(frame.Width * frame.Height * 4);
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        var destinationHandle = default(GCHandle);
        try
        {
            using var sourceHandle = frame.RgbPixels.Pin();
            destinationHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            using var rgb = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC3,
                (IntPtr)sourceHandle.Pointer, frame.Width * 3L);
            using var bgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4,
                destinationHandle.AddrOfPinnedObject(), frame.Width * 4L);
            Cv2.CvtColor(rgb, bgra, ColorConversionCodes.RGB2BGRA);
            return new VideoPreviewFrame(frame.Width, frame.Height, pixels, frame.FrameId, frame.NativeWidth, frame.NativeHeight,
                frame.ReceivedAtTimestamp, Stopwatch.GetTimestamp());
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
        finally
        {
            if (destinationHandle.IsAllocated) destinationHandle.Free();
        }
    }

    private CallOptions CreateCallOptions(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return new CallOptions(headers: CreateHeaders(), deadline: DateTime.UtcNow.Add(timeout), cancellationToken: cancellationToken);
    }

    private Metadata CreateHeaders() => new() { { "authorization", $"Bearer {_token}" } };

    private byte[] SerializeRgb888Request()
    {
        using var request = new MemoryStream(16);
        WriteInt32(request, 1, 2); // ImageFormat.format = RGB888.
        if (_maxWidth > 0) WriteInt32(request, 3, _maxWidth);
        if (_maxHeight > 0) WriteInt32(request, 4, _maxHeight);
        return request.ToArray();
    }

    private static byte[] SerializeTouch(int x, int y, int pressure, int identifier)
    {
        using var touch = new MemoryStream(24);
        WriteInt32(touch, 1, x);
        WriteInt32(touch, 2, y);
        WriteInt32(touch, 3, identifier);
        WriteInt32(touch, 4, pressure);
        var touchBytes = touch.ToArray();
        using var eventBytes = new MemoryStream(touchBytes.Length + 8);
        WriteLengthDelimited(eventBytes, 1, touchBytes);
        return eventBytes.ToArray();
    }

    private GameFrame? DecodeImage(byte[] message, long receivedAt, out long sourceSequence)
    {
        ReadOnlyMemory<byte> pixels = default;
        byte[]? format = null;
        uint sequence = 0;
        ulong timestampUs = 0;
        for (var offset = 0; offset < message.Length;)
        {
            var key = ReadVarint(message, ref offset);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 2)
            {
                var length = checked((int)ReadVarint(message, ref offset));
                if (length < 0 || length > message.Length - offset) throw new InvalidDataException("gRPC 画面消息长度无效。");
                var payload = message.AsMemory(offset, length);
                offset += length;
                if (field == 1) format = payload.ToArray();
                else if (field == 4) pixels = payload;
            }
            else if (wire == 0)
            {
                var value = ReadVarint(message, ref offset);
                if (field == 5) sequence = checked((uint)value);
                else if (field == 6) timestampUs = value;
            }
            else
                SkipField(message, ref offset, wire);
        }

        sourceSequence = sequence;
        if (pixels.Length == 0 || format is null)
            return null;
        var (width, height, pixelFormat) = DecodeImageFormat(format);
        if (pixelFormat != 2 || width <= 0 || height <= 0 || (long)width * height * 3 != pixels.Length || pixels.Length > _maxFrameBytes)
            throw new InvalidDataException($"模拟器返回了不支持的画面数据（{width} × {height}，{pixelFormat}，{pixels.Length} 字节）。");

        var capturedAt = receivedAt;
        var hasSourceTimestamp = false;
        var nowUs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);
        if (timestampUs > 0 && timestampUs <= (ulong)nowUs)
        {
            var ageUs = nowUs - (long)timestampUs;
            if (ageUs < 86_400_000_000L)
            {
                capturedAt -= (long)(ageUs / 1_000_000d * Stopwatch.Frequency);
                hasSourceTimestamp = true;
            }
        }
        // Templates keep the original 1920x1080 coordinate space even when the
        // Android display itself uses the lower game-rendering resolution.
        Volatile.Write(ref _portraitFrame, width < height ? 1 : 0);
        var nativeWidth = width >= height ? 1920 : 1080;
        var nativeHeight = width >= height ? 1080 : 1920;
        return new GameFrame(width, height, pixels, Interlocked.Increment(ref _frameNumber), capturedAt,
            nativeWidth, nativeHeight, receivedAt, hasSourceTimestamp);
    }

    private static (int Width, int Height, int Format) DecodeImageFormat(byte[] bytes)
    {
        var width = 0;
        var height = 0;
        var format = 0;
        for (var offset = 0; offset < bytes.Length;)
        {
            var key = ReadVarint(bytes, ref offset);
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 0)
            {
                var value = checked((int)ReadVarint(bytes, ref offset));
                if (field == 1) format = value;
                else if (field == 3) width = value;
                else if (field == 4) height = value;
            }
            else
                SkipField(bytes, ref offset, wire);
        }
        return (width, height, format);
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(byte[] bytes, ref int offset)
    {
        var length = checked((int)ReadVarint(bytes, ref offset));
        if (length < 0 || offset + length > bytes.Length) throw new InvalidDataException("gRPC 画面消息长度无效。");
        var result = bytes.AsSpan(offset, length);
        offset += length;
        return result;
    }

    private static ulong ReadVarint(byte[] bytes, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("gRPC 画面消息被截断。");
            var next = bytes[offset++];
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException("gRPC Varint 超长。");
    }

    private static void SkipField(byte[] bytes, ref int offset, int wire)
    {
        switch (wire)
        {
            case 0: _ = ReadVarint(bytes, ref offset); break;
            case 1: offset = checked(offset + 8); break;
            case 2: _ = ReadLengthDelimited(bytes, ref offset); break;
            case 5: offset = checked(offset + 4); break;
            default: throw new InvalidDataException($"不支持的 Protobuf wire type {wire}。");
        }
        if (offset > bytes.Length) throw new InvalidDataException("gRPC 画面消息字段越界。");
    }

    private static void WriteInt32(Stream stream, int field, int value)
    {
        WriteVarint(stream, (ulong)(field << 3));
        WriteVarint(stream, unchecked((uint)value));
    }

    private static void WriteLengthDelimited(Stream stream, int field, byte[] value)
    {
        WriteVarint(stream, (ulong)((field << 3) | 2));
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    public void Dispose() => _channel.Dispose();
}

