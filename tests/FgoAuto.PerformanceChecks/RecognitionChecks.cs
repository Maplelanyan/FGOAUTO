using System.Diagnostics;
using System.Runtime.InteropServices;
using FgoAuto.Models;
using FgoAuto.Services;
using OpenCvSharp;

internal static class RecognitionChecks
{
    public static void Run()
    {
        using var fixture = new Fixture();
        CheckOrdinaryMatches(fixture);
        CheckRepeatedRegions(fixture);
        CheckOddDimensions(fixture);
        CheckNativeCoordinatesAndVerification(fixture);
        CheckDispose(fixture);
        CheckStatistics();
        Console.WriteLine("PASS recognition: unique/ambiguous, thresholds, lists/cards/NP regions, odd sizes, native mapping, verification, disposal, statistics");
    }

    public static void RunBenchmark()
    {
        using var fixture = new Fixture();
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(25, 38, 49));
        using var repeatedPattern = Pattern(144, 120, 63817);
        using var uniquePattern = Pattern(180, 144, 9147);
        var repeatedPath = fixture.Save("benchmark-repeated", repeatedPattern);
        var uniquePath = fixture.Save("benchmark-unique", uniquePattern);
        foreach (var bounds in RepeatedBounds()) Paste(screen, repeatedPattern, bounds.X, bounds.Y);
        Paste(screen, uniquePattern, 1704, 372);
        var queries = Enumerable.Range(0, 32).Select(index => (index % 4) switch
        {
            0 => new ImageMatchQuery($"button-{index}", uniquePath, 0.82),
            1 => new ImageMatchQuery($"list-{index}", repeatedPath, 0.82, PreferTopmostMatch: true),
            2 => new ImageMatchQuery($"card-{index}", repeatedPath, 0.82, FindMultiple: true),
            _ => new ImageMatchQuery($"np-{index}", repeatedPath, 0.82, FindMultiple: true, IsNoblePhantasm: true),
        }).ToArray();
        using var matcher = new ImageMatchService();
        using var reduced = new Mat();
        Cv2.Resize(screen, reduced, new Size(1280, 720), 0, 0, InterpolationFlags.Area);
        using var process = Process.GetCurrentProcess();
        foreach (var pixels in new[] { screen, reduced })
        {
            var frame = Frame(pixels, 1920, 1080);
            for (var warmup = 0; warmup < 4; warmup++) matcher.FindAll(frame, queries);
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var samples = new double[20];
            for (var iteration = 0; iteration < samples.Length; iteration++)
            {
                var startedAt = Stopwatch.GetTimestamp();
                matcher.FindAll(frame, queries);
                samples[iteration] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            }
            process.Refresh();
            var cpuPerRound = (process.TotalProcessorTime - cpu).TotalMilliseconds / samples.Length;
            var allocatedPerRound = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / samples.Length;
            Array.Sort(samples);
            Console.WriteLine($"BENCH recognition synthetic {pixels.Width}x{pixels.Height}, {queries.Length} queries, " +
                $"{samples.Length} rounds: mean={samples.Average():F3}ms p95={samples[(int)Math.Ceiling(samples.Length * .95) - 1]:F3}ms " +
                $"CPU={cpuPerRound:F3}ms/round managed={allocatedPerRound}B/round OpenCV threads={Cv2.GetNumThreads()}");
        }
    }

    private static void CheckOrdinaryMatches(Fixture fixture)
    {
        // These sizes take the full-resolution, half-size and quarter-size branches respectively.
        foreach (var (width, height) in new[] { (24, 21), (54, 42), (96, 78) })
        {
            using var pattern = Pattern(width, height, 9341 + width);
            var path = fixture.Save($"ordinary-{width}", pattern);
            using var single = new Mat(540, 1008, MatType.CV_8UC3, new Scalar(24, 38, 49));
            Paste(single, pattern, 108, 84);
            var frame = Frame(single);
            using var matcher = new ImageMatchService();
            var query = new ImageMatchQuery("ordinary", path, 0.82);
            var result = matcher.FindAll(frame, [query]).Single();
            Require(result is { IsAmbiguous: false, Hit: not null }, $"Unique {width}px template was not accepted: {result.BestScore:F4}.");
            Near(result.Hit!.Left, 108, 1, "Unique match moved horizontally.");
            Near(result.Hit.Top, 84, 1, "Unique match moved vertically.");
            Require(result.Hit.Width == width && result.Hit.Height == height, "Template dimensions changed.");

            // A threshold just beyond the measured maximum cannot create a hit or ambiguity.
            var rejected = matcher.FindAll(frame, [query with { Threshold = result.BestScore + .001 }]).Single();
            Require(rejected.Hit is null && !rejected.IsAmbiguous, "A below-threshold candidate was accepted.");
            Near(rejected.BestScore, result.BestScore, .00001, "Changing only the threshold changed matching scores.");

            using var duplicate = single.Clone();
            Paste(duplicate, pattern, 612, 336);
            var ambiguous = matcher.FindAll(Frame(duplicate), [query]).Single();
            Require(ambiguous.IsAmbiguous && ambiguous.Hit is null, $"Two identical {width}px candidates bypassed uniqueness.");
            Cv2.ImEncode(".png", duplicate, out var png);
            var findRejectedAmbiguity = false;
            try { matcher.Find(png, path, 0.82); }
            catch (InvalidOperationException) { findRejectedAmbiguity = true; }
            Require(findRejectedAmbiguity, "Single Find API no longer rejects ambiguous matches.");
        }
    }

    private static void CheckRepeatedRegions(Fixture fixture)
    {
        using var pattern = Pattern(144, 120, 63817);
        var path = fixture.Save("repeated", pattern);
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(25, 38, 49));
        foreach (var bounds in RepeatedBounds()) Paste(screen, pattern, bounds.X, bounds.Y);
        var queries = new[]
        {
            new ImageMatchQuery("list", path, 0.82, PreferTopmostMatch: true),
            new ImageMatchQuery("cards", path, 0.82, FindMultiple: true),
            new ImageMatchQuery("np", path, 0.82, FindMultiple: true, IsNoblePhantasm: true),
        };
        using var reduced = new Mat();
        Cv2.Resize(screen, reduced, new Size(1280, 720), 0, 0, InterpolationFlags.Area);
        using var matcher = new ImageMatchService();
        foreach (var pixels in new[] { screen, reduced })
        {
            var frame = Frame(pixels, 1920, 1080);
            var results = matcher.FindAll(frame, queries, out var nativeWidth, out var nativeHeight);
            Require(nativeWidth == 1920 && nativeHeight == 1080, "Reduced matching returned transport dimensions.");
            Require(results[0].MultipleHits is { Count: 8 }, $"List lost positions: {results[0].MultipleHits?.Count}.");
            Require(results[1].MultipleHits is { Count: 5 }, $"Card query lost lower-row positions: {results[1].MultipleHits?.Count}.");
            Require(results[2].MultipleHits is { Count: 3 }, $"NP query lost upper-row positions: {results[2].MultipleHits?.Count}.");
            Near(results[0].Hit!.Top, 168, 3, "List no longer chooses the uppermost row.");
            Near(results[0].Hit!.Left, 360, 3, "Equal-height list candidates no longer choose the leftmost.");
            Require(results[1].MultipleHits!.All(hit => hit.Top >= 540), "Card query included an upper-row target.");
            Require(results[2].MultipleHits!.All(hit => hit.Top < 540), "NP query included a lower-row target.");

            // Multiple workers request the exact same frame/ROI/scale at once.
            var duplicateQueries = Enumerable.Range(0, 18)
                .Select(index => queries[index % queries.Length] with { Id = $"duplicate-{index}" }).ToArray();
            var concurrentResults = matcher.FindAll(frame, duplicateQueries);
            for (var index = 0; index < concurrentResults.Count; index++)
                SameResult(results[index % results.Count], concurrentResults[index]);
        }
    }

    private static void CheckOddDimensions(Fixture fixture)
    {
        // Half-rows of odd-size images need their own Resize; slicing a resized full frame changes sampling.
        using var pattern = Pattern(72, 68, 2582);
        var path = fixture.Save("odd-size", pattern);
        using var screen = new Mat(563, 1001, MatType.CV_8UC3, new Scalar(28, 36, 47));
        Paste(screen, pattern, 108, 72);
        Paste(screen, pattern, 612, 384);
        using var matcher = new ImageMatchService();
        var queries = new[]
        {
            new ImageMatchQuery("odd-card", path, 0.82, FindMultiple: true),
            new ImageMatchQuery("odd-np", path, 0.82, FindMultiple: true, IsNoblePhantasm: true),
            new ImageMatchQuery("odd-list", path, 0.82, PreferTopmostMatch: true),
        };
        var results = matcher.FindAll(Frame(screen), queries);
        Require(results[0].MultipleHits is { Count: 1 } && results[1].MultipleHits is { Count: 1 }
            && results[2].MultipleHits is { Count: 2 }, "Odd-size ROI matching lost or added a target.");
        Near(results[0].Hit!.Left, 612, 1, "Odd-size lower ROI X moved.");
        Near(results[0].Hit!.Top, 384, 1, "Odd-size lower ROI Y moved.");
        Near(results[1].Hit!.Top, 72, 1, "Odd-size upper ROI moved.");
        Near(results[2].Hit!.Top, 72, 1, "Odd-size list chose the wrong row.");
    }

    private static void CheckNativeCoordinatesAndVerification(Fixture fixture)
    {
        using var pattern = Pattern(180, 144, 9147);
        var path = fixture.Save("verification", pattern);
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(25, 38, 49));
        Paste(screen, pattern, 630, 312);
        using var reduced = new Mat();
        Cv2.Resize(screen, reduced, new Size(1280, 720), 0, 0, InterpolationFlags.Area);
        using var matcher = new ImageMatchService();
        var frame = Frame(reduced, 1920, 1080);
        var hit = matcher.FindAll(frame, [new ImageMatchQuery("verification", path, 0.82)]).Single().Hit;
        Require(hit is not null, "720p template did not match in native space.");
        Near(hit!.Left, 630, 3, "720p X was not mapped to native coordinates.");
        Near(hit.Top, 312, 3, "720p Y was not mapped to native coordinates.");
        Require(hit.Width == pattern.Width && hit.Height == pattern.Height, "720p template extent changed.");
        var near = matcher.FindNear(frame, path, 0.82, hit);
        Require(near is not null, "Unchanged target failed click verification.");
        Near(near!.CenterX, hit.CenterX, 3, "Click verification X changed.");
        Near(near.CenterY, hit.CenterY, 3, "Click verification Y changed.");
        Require(matcher.FindNear(frame, path, near.Score + .001, hit) is null, "Click verification ignored its threshold.");

        using var gone = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(25, 38, 49));
        using var goneReduced = new Mat();
        Cv2.Resize(gone, goneReduced, reduced.Size(), 0, 0, InterpolationFlags.Area);
        Require(matcher.FindNear(Frame(goneReduced, 1920, 1080), path, 0.82, hit) is null,
            "Removed target passed click verification.");
        Paste(gone, pattern, 1254, 720);
        Cv2.Resize(gone, goneReduced, reduced.Size(), 0, 0, InterpolationFlags.Area);
        Require(matcher.FindNear(Frame(goneReduced, 1920, 1080), path, 0.82, hit) is null,
            "Distant moved target reused the previous click position.");
    }

    private static void CheckDispose(Fixture fixture)
    {
        using var pattern = Pattern(96, 78, 6267);
        var path = fixture.Save("dispose", pattern);
        using var screen = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(25, 38, 49));
        Paste(screen, pattern, 216, 168);
        var frame = Frame(screen);
        var queries = Enumerable.Range(0, 16).Select(index => new ImageMatchQuery($"dispose-{index}", path, .82)).ToArray();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var matcher = new ImageMatchService();
            matcher.FindAll(frame, queries);
            using var starting = new ManualResetEventSlim();
            var work = Task.Run(() =>
            {
                starting.Set();
                try { matcher.FindAll(frame, queries); }
                catch (ObjectDisposedException) { /* Disposal may win before this operation acquires its lease. */ }
            });
            Require(starting.Wait(TimeSpan.FromSeconds(5)), "Background matching did not start.");
            matcher.Dispose();
            Require(work.Wait(TimeSpan.FromSeconds(10)), "Matcher disposal deadlocked an active match.");
            matcher.Dispose();
            ThrowsDisposed(() => matcher.FindAll(frame, queries));
            ThrowsDisposed(() => matcher.FindNear(frame, path, .82, new ImageMatchHit(216, 168, 96, 78, 1)));
        }
    }

    private static void CheckStatistics()
    {
        static long At(double milliseconds) => 100_000_000L + (long)(milliseconds * Stopwatch.Frequency / 1000);
        var statistics = new RecognitionStatistics();
        statistics.Record(At(0), At(20));
        statistics.Record(At(100), At(140));
        var recent = statistics.GetSnapshot(At(140));
        Require(recent.TotalRounds == 2 && recent.WindowRounds == 2, "Recognition round counts are incorrect.");
        Near(recent.MeanMs, 30, .001, "Recognition mean is incorrect.");
        Near(recent.P95Ms, 40, .001, "Recognition P95 is incorrect.");
        Near(recent.DutyRatio, .012, .000001, "Recognition duty did not use a fixed five-second denominator.");
        var idle = statistics.GetSnapshot(At(5141));
        Require(idle.TotalRounds == 2 && idle.WindowRounds == 0 && idle.MeanMs == 0 && idle.P95Ms == 0 && idle.DutyRatio == 0,
            "Old recognition samples survived the time window or cumulative round counts were lost.");
        statistics.Reset();
        var reset = statistics.GetSnapshot(At(5141));
        Require(reset.TotalRounds == 0 && reset.WindowRounds == 0, "Recognition Reset retained rounds.");
        statistics.Record(At(0), At(1000));
        var overlap = statistics.GetSnapshot(At(5500));
        Near(overlap.DutyRatio, .1, .000001, "Recognition duty counted work outside the observation window.");
        Near(overlap.MeanMs, 1000, .001, "Recognition duration should include the entire round ending in the window.");
        statistics.Reset();
        for (var index = 0; index < 2200; index++) statistics.Record(At(index), At(index + .5));
        var bounded = statistics.GetSnapshot(At(2200));
        Require(bounded.TotalRounds == 2200 && bounded.WindowRounds is > 0 and <= 2048,
            "Recognition timing grew without bounds or lost session round counts.");
    }

    private static Rect[] RepeatedBounds() => Enumerable.Range(0, 5)
        .Select(index => new Rect(48 + index * 384, 720, 144, 120))
        .Concat(Enumerable.Range(0, 3).Select(index => new Rect(360 + index * 480, 168, 144, 120))).ToArray();

    private static Mat Pattern(int width, int height, int seed)
    {
        var pattern = new Mat(height, width, MatType.CV_8UC3);
        var random = new Random(seed);
        for (var y = 0; y < height; y += 6)
            for (var x = 0; x < width; x += 6)
                Cv2.Rectangle(pattern, new Rect(x, y, Math.Min(6, width - x), Math.Min(6, height - y)),
                    new Scalar(random.Next(20, 240), random.Next(20, 240), random.Next(20, 240)), -1);
        Cv2.Rectangle(pattern, new Rect(0, 0, width, height), new Scalar(250, 250, 250), 2);
        Cv2.Line(pattern, new Point(width / 5, height / 5), new Point(width * 4 / 5, height * 4 / 5),
            new Scalar(5, 5, 5), Math.Max(2, width / 30));
        return pattern;
    }

    private static void Paste(Mat screen, Mat pattern, int left, int top)
    {
        using var area = new Mat(screen, new Rect(left, top, pattern.Width, pattern.Height));
        pattern.CopyTo(area);
    }

    private static GameFrame Frame(Mat bgr, int nativeWidth = 0, int nativeHeight = 0)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
        var pixels = new byte[checked(rgb.Width * rgb.Height * 3)];
        Marshal.Copy(rgb.Data, pixels, 0, pixels.Length);
        return new(rgb.Width, rgb.Height, pixels, 1, Stopwatch.GetTimestamp(), nativeWidth, nativeHeight);
    }

    private static void SameResult(ImageMatchEvaluation expected, ImageMatchEvaluation actual)
    {
        Require(expected.IsAmbiguous == actual.IsAmbiguous && (expected.Hit is null) == (actual.Hit is null),
            "Shared frame work changed the matching decision.");
        Near(actual.BestScore, expected.BestScore, .00001, "Shared frame work changed the best score.");
        var expectedHits = expected.MultipleHits ?? (expected.Hit is null ? [] : new[] { expected.Hit });
        var actualHits = actual.MultipleHits ?? (actual.Hit is null ? [] : new[] { actual.Hit });
        Require(expectedHits.Count == actualHits.Count, "Shared frame work changed repeated hit count.");
        for (var index = 0; index < expectedHits.Count; index++)
        {
            var first = expectedHits[index];
            var second = actualHits[index];
            Require(first.Left == second.Left && first.Top == second.Top && first.Width == second.Width && first.Height == second.Height,
                "Shared frame work changed hit order or coordinates.");
            Near(second.Score, first.Score, .00001, "Shared frame work changed hit score.");
        }
    }

    private static void ThrowsDisposed(Action action)
    {
        try { action(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Disposed matcher accepted another matching operation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(double actual, double expected, double tolerance, string message) =>
        Require(Math.Abs(actual - expected) <= tolerance, $"{message} Actual={actual:F6}, expected={expected:F6}.");

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "FgoAuto-RecognitionChecks-" + Guid.NewGuid().ToString("N"));

        public Fixture() => Directory.CreateDirectory(_directory);

        public string Save(string name, Mat pattern)
        {
            var path = Path.Combine(_directory, name + ".png");
            if (!Cv2.ImWrite(path, pattern)) throw new InvalidOperationException("Could not write synthetic recognition fixture.");
            return path;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
