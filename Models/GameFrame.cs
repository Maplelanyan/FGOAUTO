using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace FgoAuto.Models;

/// <summary>
/// A complete RGB snapshot. The producer transfers a read-only buffer and must
/// not mutate or reuse it after publishing the frame.
/// </summary>
public sealed class GameFrame
{
    public GameFrame(int width, int height, ReadOnlyMemory<byte> rgbPixels, long frameId,
        long capturedAtTimestamp, int nativeWidth = 0, int nativeHeight = 0, long receivedAtTimestamp = 0,
        bool hasSourceTimestamp = false)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (rgbPixels.Length != checked(width * height * 3))
            throw new ArgumentException("RGB 画面数据大小与尺寸不一致。", nameof(rgbPixels));
        if (nativeWidth < 0) throw new ArgumentOutOfRangeException(nameof(nativeWidth));
        if (nativeHeight < 0) throw new ArgumentOutOfRangeException(nameof(nativeHeight));

        Width = width;
        Height = height;
        NativeWidth = nativeWidth == 0 ? width : nativeWidth;
        NativeHeight = nativeHeight == 0 ? height : nativeHeight;
        RgbPixels = rgbPixels;
        FrameId = frameId;
        CapturedAtTimestamp = capturedAtTimestamp;
        ReceivedAtTimestamp = receivedAtTimestamp == 0 ? capturedAtTimestamp : receivedAtTimestamp;
        HasSourceTimestamp = hasSourceTimestamp;
    }

    public int Width { get; }
    public int Height { get; }
    public int NativeWidth { get; }
    public int NativeHeight { get; }
    public ReadOnlyMemory<byte> RgbPixels { get; }
    public long FrameId { get; }
    public long CapturedAtTimestamp { get; }
    public long ReceivedAtTimestamp { get; }
    /// <summary>True only when the emulator supplied a usable generation-time estimate.</summary>
    public bool HasSourceTimestamp { get; }
    public TimeSpan Age => Stopwatch.GetElapsedTime(CapturedAtTimestamp);

    /// <summary>Returns an independent grayscale image in the native coordinate space.</summary>
    public Mat CreateGrayMat() => CreateGrayMat(NativeWidth, NativeHeight);

    /// <summary>Returns grayscale at a requested size without first upscaling to native resolution.</summary>
    public Mat CreateGrayMat(int outputWidth, int outputHeight)
    {
        if (outputWidth <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputHeight));
        return ConvertRgb(ColorConversionCodes.RGB2GRAY, outputWidth, outputHeight);
    }

    /// <summary>Encodes a native-size PNG only when a manual selection needs a fixed image.</summary>
    public byte[] EncodePng()
    {
        using var bgr = ConvertRgb(ColorConversionCodes.RGB2BGR, NativeWidth, NativeHeight);
        if (!Cv2.ImEncode(".png", bgr, out var image))
            throw new InvalidOperationException("无法编码模拟器截图。");
        return image;
    }

    private Mat ConvertRgb(ColorConversionCodes conversion, int outputWidth, int outputHeight)
    {
        // gRPC messages normally use array-backed memory. Retain that storage while
        // OpenCV reads it; no full-frame copy is needed for the common path.
        var memory = RgbPixels;
        if (!MemoryMarshal.TryGetArray(memory, out var segment))
        {
            memory = memory.ToArray();
            MemoryMarshal.TryGetArray(memory, out segment);
        }

        var handle = GCHandle.Alloc(segment.Array!, GCHandleType.Pinned);
        var converted = new Mat();
        try
        {
            using var rgb = Mat.FromPixelData(Height, Width, MatType.CV_8UC3,
                handle.AddrOfPinnedObject() + segment.Offset, Width * 3L);
            Cv2.CvtColor(rgb, converted, conversion);
            if (outputWidth != Width || outputHeight != Height)
            {
                var resized = new Mat();
                try
                {
                    var interpolation = outputWidth > Width || outputHeight > Height
                        ? InterpolationFlags.Linear : InterpolationFlags.Area;
                    Cv2.Resize(converted, resized, new Size(outputWidth, outputHeight), 0, 0, interpolation);
                    converted.Dispose();
                    return resized;
                }
                catch
                {
                    resized.Dispose();
                    throw;
                }
            }
            return converted;
        }
        catch
        {
            converted.Dispose();
            throw;
        }
        finally { handle.Free(); }
    }
}
