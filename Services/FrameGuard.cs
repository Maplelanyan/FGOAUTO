using OpenCvSharp;
using FgoAuto.Models;

namespace FgoAuto.Services;

public static class FrameGuard
{
    public static bool IsSameScene(GameFrame reference, GameFrame current)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(current);
        if (reference.NativeWidth != current.NativeWidth || reference.NativeHeight != current.NativeHeight)
            return false;

        using var first = reference.CreateGrayMat(96, 54);
        using var second = current.CreateGrayMat(96, 54);
        return CompareThumbnails(first, second);
    }

    public static bool IsSameScene(byte[] reference, byte[] current)
    {
        using var first = Cv2.ImDecode(reference, ImreadModes.Grayscale);
        using var second = Cv2.ImDecode(current, ImreadModes.Grayscale);
        if (first.Empty() || second.Empty() || first.Size() != second.Size())
            return false;
        using var a = new Mat();
        using var b = new Mat();
        Cv2.Resize(first, a, new Size(96, 54), 0, 0, InterpolationFlags.Area);
        Cv2.Resize(second, b, a.Size(), 0, 0, InterpolationFlags.Area);
        return CompareThumbnails(a, b);
    }

    private static bool CompareThumbnails(Mat a, Mat b)
    {
        using var difference = new Mat();
        using var changed = new Mat();
        Cv2.Absdiff(a, b, difference);
        Cv2.Threshold(difference, changed, 45, 255, ThresholdTypes.Binary);
        return Cv2.Mean(difference).Val0 <= 24 && Cv2.CountNonZero(changed) / (double)changed.Total() <= 0.35;
    }
}
