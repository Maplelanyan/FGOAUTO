using System.Buffers;

namespace FgoAuto.Models;

/// <summary>One BGRA frame; ownership transfers from the decoder to the UI.</summary>
public sealed class VideoPreviewFrame(int width, int height, byte[] pixels, long sequence, int nativeWidth = 0, int nativeHeight = 0) : IDisposable
{
    private byte[]? _pixels = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int RowBytes => Width * 4;
    public long Sequence { get; } = sequence;
    public int NativeWidth { get; } = nativeWidth > 0 ? nativeWidth : width;
    public int NativeHeight { get; } = nativeHeight > 0 ? nativeHeight : height;
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(VideoPreviewFrame));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pixels, null) is { } buffer)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
