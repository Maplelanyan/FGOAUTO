using OpenCvSharp;
using FgoAuto.Models;
using System.Collections.Concurrent;

namespace FgoAuto.Services;

public sealed class ImageMatchService
{
    private readonly ConcurrentDictionary<string, Lazy<Mat>> _templateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Mat>> _scaledTemplateCache = new(StringComparer.OrdinalIgnoreCase);

    public (int Width, int Height) GetImageSize(string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
            throw new InvalidDataException($"无法读取图片模板：{imagePath}");
        return (image.Width, image.Height);
    }

    public void SaveTemplate(byte[] screenshotPng, int left, int top, int width, int height, string destinationPath)
    {
        using var screenshot = Cv2.ImDecode(screenshotPng, ImreadModes.Color);
        if (screenshot.Empty())
            throw new InvalidDataException("无法解码模拟器截图。请重启模拟器后再框选。");
        if (left < 0 || top < 0 || width < 1 || height < 1 || left + width > screenshot.Width || top + height > screenshot.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "图片选区超出截图范围。");

        using var crop = new Mat(screenshot, new Rect(left, top, width, height));
        if (!Cv2.ImEncode(".png", crop, out var encoded))
            throw new InvalidOperationException("无法保存所选图片区域。");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, encoded);
    }

    public ImageMatchHit? Find(byte[] screenshotPng, string templatePath, double threshold)
    {
        var evaluation = FindAll(screenshotPng, [new ImageMatchQuery("single", templatePath, threshold)])[0];
        if (evaluation.IsAmbiguous)
            throw new InvalidOperationException($"发现多个相似位置（最高匹配度 {evaluation.BestScore:P0}）。请框选带有更多独特边缘或背景的区域。");
        return evaluation.Hit;
    }

    public IReadOnlyList<ImageMatchEvaluation> FindAll(byte[] screenshotPng, IReadOnlyList<ImageMatchQuery> queries)
        => FindAll(screenshotPng, queries, out _, out _);

    public IReadOnlyList<ImageMatchEvaluation> FindAll(byte[] screenshotPng, IReadOnlyList<ImageMatchQuery> queries, out int width, out int height)
    {
        using var sourceGray = Cv2.ImDecode(screenshotPng, ImreadModes.Grayscale);
        if (sourceGray.Empty())
            throw new InvalidDataException("无法解码模拟器截图。");
        return FindAll(sourceGray, queries, out width, out height);
    }

    public IReadOnlyList<ImageMatchEvaluation> FindAll(GameFrame frame, IReadOnlyList<ImageMatchQuery> queries)
        => FindAll(frame, queries, out _, out _);

    public IReadOnlyList<ImageMatchEvaluation> FindAll(GameFrame frame, IReadOnlyList<ImageMatchQuery> queries, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // Match in the pixels actually received. Upscaling a 720p frame to 1080p
        // before every scan defeats the reduction in matching work.
        using var sourceGray = frame.CreateGrayMat(frame.Width, frame.Height);
        return FindAll(sourceGray, queries, out width, out height, frame.NativeWidth, frame.NativeHeight);
    }

    private IReadOnlyList<ImageMatchEvaluation> FindAll(Mat sourceGray, IReadOnlyList<ImageMatchQuery> queries,
        out int width, out int height, int nativeWidth = 0, int nativeHeight = 0)
    {
        width = nativeWidth > 0 ? nativeWidth : sourceGray.Width;
        height = nativeHeight > 0 ? nativeHeight : sourceGray.Height;
        var scale = new MatchScale(sourceGray.Width, sourceGray.Height, width, height);

        using var coarseSourceGray = new Mat();
        Cv2.Resize(sourceGray, coarseSourceGray, new Size(Math.Max(1, sourceGray.Width / 2), Math.Max(1, sourceGray.Height / 2)), 0, 0, InterpolationFlags.Area);
        using var quarterSourceGray = new Mat();
        if (sourceGray.Width >= 960)
            Cv2.Resize(sourceGray, quarterSourceGray, new Size(Math.Max(1, sourceGray.Width / 4), Math.Max(1, sourceGray.Height / 4)), 0, 0, InterpolationFlags.Area);
        var results = new ImageMatchEvaluation[queries.Count];
        // Each worker only reads the frame and cached templates, and owns its score matrices.
        Parallel.For(0, queries.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(2, Environment.ProcessorCount) }, index =>
        {
            var result = EvaluateQuery(sourceGray, coarseSourceGray, quarterSourceGray, queries[index], scale);
            results[index] = scale.IsNativeSize ? result : result with
            {
                Hit = result.Hit is { } hit ? scale.ToNative(hit) : null,
                MultipleHits = result.MultipleHits?.Select(scale.ToNative).ToArray()
            };
        });
        return results;
    }

    private ImageMatchEvaluation EvaluateQuery(Mat sourceGray, Mat coarseSourceGray, Mat quarterSourceGray,
        ImageMatchQuery query, MatchScale scale)
    {
        var originalTemplate = GetTemplateGray(query.ImagePath);
        if (originalTemplate.Width > scale.NativeWidth || originalTemplate.Height > scale.NativeHeight)
            throw new InvalidOperationException($"图片模板“{query.Id}”比当前屏幕还大，无法匹配。");
        var templateGray = GetFrameTemplate(query.ImagePath, originalTemplate, scale);
        if (templateGray.Width > sourceGray.Width || templateGray.Height > sourceGray.Height)
            throw new InvalidOperationException($"图片模板“{query.Id}”比当前屏幕还大，无法匹配。");

        if (query.FindMultiple || query.PreferTopmostMatch || query.IsNoblePhantasm)
            return FindRepeatedTargets(sourceGray, templateGray, query);

        if (templateGray.Width < 32 || templateGray.Height < 32)
            return FindAtFullResolution(sourceGray, templateGray, query);

        var step = sourceGray.Width >= 960 && Math.Min(templateGray.Width, templateGray.Height) >= 64 ? 4 : 2;
        var coarseTemplate = GetCoarseTemplate(query.ImagePath, templateGray, step);
        using var coarseScores = new Mat();
        Cv2.MatchTemplate(step == 4 ? quarterSourceGray : coarseSourceGray, coarseTemplate, coarseScores, TemplateMatchModes.CCoeffNormed, null);
        Cv2.MinMaxLoc(coarseScores, out _, out _, out _, out var coarseBestLocation);

        using var alternativeScores = coarseScores.Clone();
        var suppressLeft = Math.Max(0, coarseBestLocation.X - coarseTemplate.Width / 2);
        var suppressTop = Math.Max(0, coarseBestLocation.Y - coarseTemplate.Height / 2);
        var suppressRight = Math.Min(alternativeScores.Width, coarseBestLocation.X + coarseTemplate.Width + coarseTemplate.Width / 2);
        var suppressBottom = Math.Min(alternativeScores.Height, coarseBestLocation.Y + coarseTemplate.Height + coarseTemplate.Height / 2);
        Cv2.Rectangle(alternativeScores,
            new Rect(suppressLeft, suppressTop, suppressRight - suppressLeft, suppressBottom - suppressTop),
            new Scalar(-1), thickness: -1);
        Cv2.MinMaxLoc(alternativeScores, out _, out var coarseSecondScore, out _, out var coarseSecondLocation);

        var first = RefineCandidate(sourceGray, templateGray, coarseBestLocation, step);
        var second = coarseSecondScore > -0.99
            ? RefineCandidate(sourceGray, templateGray, coarseSecondLocation, step)
            : (X: 0, Y: 0, Score: -1d);
        if (second.Score > first.Score)
            (first, second) = (second, first);

        if (first.Score < query.Threshold)
            return new ImageMatchEvaluation(query.Id, null, first.Score, false);

        var ambiguous = second.Score >= query.Threshold && first.Score - second.Score < 0.035;
        var hit = ambiguous ? null : new ImageMatchHit(first.X, first.Y, templateGray.Width, templateGray.Height, first.Score);
        return new ImageMatchEvaluation(query.Id, hit, first.Score, ambiguous);
    }

    private ImageMatchEvaluation FindRepeatedTargets(Mat sourceGray, Mat templateGray, ImageMatchQuery query)
    {
        // Noble Phantasms use the upper row; ordinary cards use the lower row; lists search the whole frame.
        var top = query.FindMultiple && !query.IsNoblePhantasm ? sourceGray.Height / 2 : 0;
        var bottom = query.IsNoblePhantasm ? sourceGray.Height / 2 : sourceGray.Height;
        if (bottom <= top)
            return new ImageMatchEvaluation(query.Id, null, 0, false, []);
        using var area = new Mat(sourceGray, new Rect(0, top, sourceGray.Width, bottom - top));
        if (templateGray.Width > area.Width || templateGray.Height > area.Height)
            return new ImageMatchEvaluation(query.Id, null, 0, false, []);

        var downsample = templateGray.Width >= 32 && templateGray.Height >= 32;
        var step = sourceGray.Width >= 960 && Math.Min(templateGray.Width, templateGray.Height) >= 64 ? 4 : 2;
        using var search = new Mat();
        using var template = new Mat();
        if (downsample)
        {
            Cv2.Resize(area, search, new Size(Math.Max(1, area.Width / step), Math.Max(1, area.Height / step)), 0, 0, InterpolationFlags.Area);
            GetCoarseTemplate(query.ImagePath, templateGray, step).CopyTo(template);
        }
        else
        {
            area.CopyTo(search);
            templateGray.CopyTo(template);
        }

        using var scores = new Mat();
        Cv2.MatchTemplate(search, template, scores, TemplateMatchModes.CCoeffNormed, null);
        var hits = new List<ImageMatchHit>();
        var bestScore = 0d;
        for (var candidate = 0; candidate < 15; candidate++)
        {
            Cv2.MinMaxLoc(scores, out _, out var coarseScore, out _, out var location);
            if (coarseScore < query.Threshold - 0.15)
                break;
            var refined = downsample
                ? RefineCandidate(area, templateGray, location, step)
                : (X: location.X, Y: location.Y, Score: coarseScore);
            SuppressBest(scores, location, template.Width, template.Height);
            bestScore = Math.Max(bestScore, refined.Score);
            if (refined.Score < query.Threshold)
                continue;
            var hit = new ImageMatchHit(refined.X, top + refined.Y, templateGray.Width, templateGray.Height, refined.Score);
            if (hits.All(existing => Math.Abs(existing.CenterX - hit.CenterX) > templateGray.Width / 2
                || Math.Abs(existing.CenterY - hit.CenterY) > templateGray.Height / 2))
                hits.Add(hit);
        }
        var ordered = query.PreferTopmostMatch && !query.FindMultiple && !query.IsNoblePhantasm
            ? hits.OrderBy(hit => hit.Top).ThenBy(hit => hit.Left).ThenByDescending(hit => hit.Score).ToArray()
            : hits.OrderByDescending(hit => hit.Score).ToArray();
        return new ImageMatchEvaluation(query.Id, ordered.FirstOrDefault(), bestScore, false, ordered);
    }

    private Mat GetTemplateGray(string imagePath) => _templateCache.GetOrAdd(
        Path.GetFullPath(imagePath),
        path => new Lazy<Mat>(() =>
        {
            using var template = Cv2.ImRead(path, ImreadModes.Color);
            if (template.Empty())
                throw new InvalidDataException($"无法读取图片模板：{path}");
            var gray = new Mat();
            Cv2.CvtColor(template, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        })).Value;

    private Mat GetFrameTemplate(string imagePath, Mat original, MatchScale scale)
    {
        var width = Math.Max(1, ScaleCoordinate(original.Width, scale.PixelWidth, scale.NativeWidth));
        var height = Math.Max(1, ScaleCoordinate(original.Height, scale.PixelHeight, scale.NativeHeight));
        if (width == original.Width && height == original.Height)
            return original;
        return _scaledTemplateCache.GetOrAdd(
            Path.GetFullPath(imagePath) + $"|frame:{width}x{height}",
            _ => new Lazy<Mat>(() =>
            {
                var scaled = new Mat();
                Cv2.Resize(original, scaled, new Size(width, height), 0, 0,
                    width < original.Width || height < original.Height ? InterpolationFlags.Area : InterpolationFlags.Linear);
                return scaled;
            })).Value;
    }

    private Mat GetCoarseTemplate(string imagePath, Mat frameTemplate, int step) => _scaledTemplateCache.GetOrAdd(
        Path.GetFullPath(imagePath) + $"|coarse:{frameTemplate.Width}x{frameTemplate.Height}/{step}",
        _ => new Lazy<Mat>(() =>
        {
            var scaled = new Mat();
            Cv2.Resize(frameTemplate, scaled, new Size(Math.Max(1, frameTemplate.Width / step), Math.Max(1, frameTemplate.Height / step)), 0, 0, InterpolationFlags.Area);
            return scaled;
        })).Value;

    public ImageMatchHit? FindNear(byte[] screenshot, string imagePath, double threshold, ImageMatchHit previous)
    {
        using var source = Cv2.ImDecode(screenshot, ImreadModes.Grayscale);
        return FindNear(source, imagePath, threshold, previous);
    }

    public ImageMatchHit? FindNear(GameFrame frame, string imagePath, double threshold, ImageMatchHit previous)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var source = frame.CreateGrayMat(frame.Width, frame.Height);
        var scale = new MatchScale(frame.Width, frame.Height, frame.NativeWidth, frame.NativeHeight);
        var hit = FindNear(source, imagePath, threshold, scale.ToPixels(previous), scale);
        return hit is null ? null : scale.ToNative(hit);
    }

    private ImageMatchHit? FindNear(Mat source, string imagePath, double threshold, ImageMatchHit previous)
        => FindNear(source, imagePath, threshold, previous,
            new MatchScale(source.Width, source.Height, source.Width, source.Height));

    private ImageMatchHit? FindNear(Mat source, string imagePath, double threshold, ImageMatchHit previous, MatchScale scale)
    {
        var original = GetTemplateGray(imagePath);
        if (source.Empty() || original.Width > scale.NativeWidth || original.Height > scale.NativeHeight)
            return null;
        var template = GetFrameTemplate(imagePath, original, scale);
        if (template.Width > source.Width || template.Height > source.Height)
            return null;
        var marginX = Math.Min(source.Width / 20, template.Width / 2 + 4);
        var marginY = Math.Min(source.Height / 20, template.Height / 2 + 4);
        var left = Math.Clamp(previous.Left - marginX, 0, source.Width - template.Width);
        var top = Math.Clamp(previous.Top - marginY, 0, source.Height - template.Height);
        var right = Math.Clamp(previous.Left + marginX, left, source.Width - template.Width);
        var bottom = Math.Clamp(previous.Top + marginY, top, source.Height - template.Height);
        using var area = new Mat(source, new Rect(left, top, right - left + template.Width, bottom - top + template.Height));
        using var scores = new Mat();
        Cv2.MatchTemplate(area, template, scores, TemplateMatchModes.CCoeffNormed, null);
        Cv2.MinMaxLoc(scores, out _, out var score, out _, out var location);
        return score < threshold ? null : new ImageMatchHit(left + location.X, top + location.Y, template.Width, template.Height, score);
    }

    private static int ScaleCoordinate(int value, int targetSize, int sourceSize) =>
        (int)Math.Round(value * (double)targetSize / sourceSize, MidpointRounding.AwayFromZero);

    private readonly record struct MatchScale(int PixelWidth, int PixelHeight, int NativeWidth, int NativeHeight)
    {
        public bool IsNativeSize => PixelWidth == NativeWidth && PixelHeight == NativeHeight;

        public ImageMatchHit ToNative(ImageMatchHit hit) =>
            MapHit(hit, NativeWidth, NativeHeight, PixelWidth, PixelHeight);

        public ImageMatchHit ToPixels(ImageMatchHit hit) =>
            MapHit(hit, PixelWidth, PixelHeight, NativeWidth, NativeHeight);

        private static ImageMatchHit MapHit(ImageMatchHit hit, int targetWidth, int targetHeight, int sourceWidth, int sourceHeight)
        {
            var left = Math.Clamp(ScaleCoordinate(hit.Left, targetWidth, sourceWidth), 0, targetWidth - 1);
            var top = Math.Clamp(ScaleCoordinate(hit.Top, targetHeight, sourceHeight), 0, targetHeight - 1);
            var right = Math.Clamp(ScaleCoordinate(hit.Left + hit.Width, targetWidth, sourceWidth), left + 1, targetWidth);
            var bottom = Math.Clamp(ScaleCoordinate(hit.Top + hit.Height, targetHeight, sourceHeight), top + 1, targetHeight);
            return new ImageMatchHit(left, top, right - left, bottom - top, hit.Score);
        }
    }

    private static ImageMatchEvaluation FindAtFullResolution(Mat sourceGray, Mat templateGray, ImageMatchQuery query)
    {
        using var scores = new Mat();
        Cv2.MatchTemplate(sourceGray, templateGray, scores, TemplateMatchModes.CCoeffNormed, null);
        Cv2.MinMaxLoc(scores, out _, out var bestScore, out _, out var bestLocation);
        if (bestScore < query.Threshold)
            return new ImageMatchEvaluation(query.Id, null, bestScore, false);

        using var alternatives = scores.Clone();
        SuppressBest(alternatives, bestLocation, templateGray.Width, templateGray.Height);
        Cv2.MinMaxLoc(alternatives, out _, out var secondScore, out _, out _);
        var ambiguous = secondScore >= query.Threshold && bestScore - secondScore < 0.035;
        var hit = ambiguous ? null : new ImageMatchHit(bestLocation.X, bestLocation.Y, templateGray.Width, templateGray.Height, bestScore);
        return new ImageMatchEvaluation(query.Id, hit, bestScore, ambiguous);
    }

    private static (int X, int Y, double Score) RefineCandidate(Mat sourceGray, Mat templateGray, Point coarseLocation, int step = 2)
    {
        var radius = Math.Max(6, step * 2 + 2);
        var predictedX = coarseLocation.X * step;
        var predictedY = coarseLocation.Y * step;
        var maxX = sourceGray.Width - templateGray.Width;
        var maxY = sourceGray.Height - templateGray.Height;
        var left = Math.Clamp(predictedX - radius, 0, maxX);
        var top = Math.Clamp(predictedY - radius, 0, maxY);
        var right = Math.Clamp(predictedX + radius, 0, maxX);
        var bottom = Math.Clamp(predictedY + radius, 0, maxY);
        using var searchArea = new Mat(sourceGray, new Rect(left, top, right - left + templateGray.Width, bottom - top + templateGray.Height));
        using var scores = new Mat();
        Cv2.MatchTemplate(searchArea, templateGray, scores, TemplateMatchModes.CCoeffNormed, null);
        Cv2.MinMaxLoc(scores, out _, out var score, out _, out var location);
        return (left + location.X, top + location.Y, score);
    }

    private static void SuppressBest(Mat scores, Point bestLocation, int templateWidth, int templateHeight)
    {
        var left = Math.Max(0, bestLocation.X - templateWidth / 2);
        var top = Math.Max(0, bestLocation.Y - templateHeight / 2);
        var right = Math.Min(scores.Width, bestLocation.X + templateWidth + templateWidth / 2);
        var bottom = Math.Min(scores.Height, bestLocation.Y + templateHeight + templateHeight / 2);
        Cv2.Rectangle(scores, new Rect(left, top, right - left, bottom - top), new Scalar(-1), thickness: -1);
    }

}
