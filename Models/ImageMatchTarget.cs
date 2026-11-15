using Avalonia.Media.Imaging;

namespace FgoAuto.Models;

public sealed class ImageMatchTarget
{
    public string Id { get; }
    public string Name { get; private set; }
    public string ImagePath { get; }
    public int Width { get; }
    public int Height { get; }
    public Bitmap Thumbnail { get; }
    public string Display => $"{Name}  ·  {Width} × {Height}";

    public ImageMatchTarget(string id, string imagePath, int width, int height, Bitmap thumbnail, string? name = null)
    {
        Id = id;
        ImagePath = imagePath;
        Width = width;
        Height = height;
        Thumbnail = thumbnail;
        Name = string.IsNullOrWhiteSpace(name) ? GetDefaultName(id) : name.Trim();
    }

    public void Rename(string name) => Name = name;

    private static string GetDefaultName(string id)
    {
        var suffix = id.StartsWith("target_", StringComparison.OrdinalIgnoreCase) ? id[7..] : id;
        return $"图像目标 {suffix}";
    }
}

public sealed record ImageMatchHit(int Left, int Top, int Width, int Height, double Score)
{
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
}

public sealed record ImageMatchQuery(string Id, string ImagePath, double Threshold, bool FindMultiple = false, bool PreferTopmostMatch = false, bool IsNoblePhantasm = false);

public sealed record ImageMatchEvaluation(string Id, ImageMatchHit? Hit, double BestScore, bool IsAmbiguous,
    IReadOnlyList<ImageMatchHit>? MultipleHits = null);
