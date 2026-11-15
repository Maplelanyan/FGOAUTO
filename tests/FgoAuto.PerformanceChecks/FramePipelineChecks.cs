using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using FgoAuto.Models;
using FgoAuto.Services;
using OpenCvSharp;


internal static class FramePipelineChecks
{
    public static void Run()
    {
        var checks = new (string Name, Action Run)[]
        {
    ("RGB slice offset, orientation, grayscale and PNG colours", CheckOrientation),
    ("720p stream matches existing 1080p template in native coordinates", CheckNativeCoordinates),
    ("720p repeated cards and lists retain native coordinates and region filtering", CheckRepeatedTargets),
    ("Scene guard retains identical scenes and rejects transitions", CheckSceneGuard),
    ("Stream statistics retain long gaps and handle source sequence resets", CheckStatistics),
    ("gRPC protobuf decoding preserves timing, native mapping and zero-copy pixels", CheckGrpcDecode),
    ("Game profiles map touch coordinates and update only scoped AVD settings", CheckGameProfiles),
        };

        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed.");
        if (failures != 0) throw new InvalidOperationException($"{failures} frame pipeline checks failed.");
    }

    static void CheckOrientation()
    {
        const int width = 24, height = 18, offset = 37;
        var storage = Enumerable.Repeat((byte)0xEC, offset + width * height * 3 + 29).ToArray();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = offset + (y * width + x) * 3;
                var colour = y < height / 2 ? (R: (byte)255, G: (byte)0, B: (byte)0)
                    : (R: (byte)0, G: (byte)0, B: (byte)255);
                if (x < 4) colour = (0, 255, 0);
                storage[at] = colour.R; storage[at + 1] = colour.G; storage[at + 2] = colour.B;
            }

        var snapshot = storage.AsSpan().ToArray();
        var frame = new GameFrame(width, height, storage.AsMemory(offset, width * height * 3), 1, Stopwatch.GetTimestamp());
        using var gray = frame.CreateGrayMat();
        Assert(gray.Width == width && gray.Height == height, "Grayscale dimensions changed.");
        AssertNear(gray.At<byte>(2, 8), 76, 1, "Top red row was moved or read with the wrong channel order.");
        AssertNear(gray.At<byte>(height - 2, 8), 29, 1, "Bottom blue row was moved or read with the wrong channel order.");
        AssertNear(gray.At<byte>(4, 1), 150, 1, "Left green marker was moved or read at the wrong offset.");

        using var png = Cv2.ImDecode(frame.EncodePng(), ImreadModes.Color);
        Assert(png.Width == width && png.Height == height, "PNG dimensions changed.");
        Assert(png.At<Vec3b>(2, 8) == new Vec3b(0, 0, 255), "PNG red marker has wrong channels/orientation.");
        Assert(png.At<Vec3b>(height - 2, 8) == new Vec3b(255, 0, 0), "PNG blue marker has wrong channels/orientation.");
        Assert(png.At<Vec3b>(4, 1) == new Vec3b(0, 255, 0), "PNG left marker moved.");
        Assert(storage.AsSpan().SequenceEqual(snapshot), "Reading or encoding mutated the shared frame buffer.");
    }

    static void CheckNativeCoordinates()
    {
        const int width = 1920, height = 1080;
        var targetBounds = new Rect(630, 315, 210, 156);
        using var screen = CreateMatchScreen(targetBounds);

        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "check-artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var templatePath = Path.Combine(artifactDirectory, "native-template.png");
        using (var template = new Mat(screen, targetBounds)) Cv2.ImWrite(templatePath, template);

        var nativeFrame = FrameFromBgr(screen, width, height);
        using var smaller = new Mat();
        Cv2.Resize(screen, smaller, new Size(1280, 720), 0, 0, InterpolationFlags.Area);
        var performanceFrame = FrameFromBgr(smaller, width, height);
        var matcher = new ImageMatchService();
        var queries = new[] { new ImageMatchQuery("existing-template", templatePath, 0.82) };
        var baseline = matcher.FindAll(nativeFrame, queries, out var nativeWidth, out var nativeHeight).Single();
        var result = matcher.FindAll(performanceFrame, queries, out var mappedWidth, out var mappedHeight).Single();
        Assert(nativeWidth == width && nativeHeight == height && mappedWidth == width && mappedHeight == height,
            "Matcher returned stream dimensions instead of native dimensions.");
        Assert(!baseline.IsAmbiguous && baseline.Hit is not null, "Baseline native template did not match uniquely.");
        Assert(!result.IsAmbiguous && result.Hit is not null,
            $"720p frame did not match uniquely. Best score={result.BestScore:F4}");
        AssertNear(result.Hit!.Left, targetBounds.X, 3, "720p match X coordinate is not native.");
        AssertNear(result.Hit.Top, targetBounds.Y, 3, "720p match Y coordinate is not native.");
        Assert(result.Hit.Width == targetBounds.Width && result.Hit.Height == targetBounds.Height,
            "Template bounds were incorrectly scaled for touch coordinates.");
        var near = matcher.FindNear(performanceFrame, templatePath, 0.82, baseline.Hit!);
        Assert(near is not null, "Click-before-touch verification failed on the 720p frame.");
        AssertNear(near!.CenterX, baseline.Hit!.CenterX, 3, "FindNear click X changed.");
        AssertNear(near.CenterY, baseline.Hit.CenterY, 3, "FindNear click Y changed.");
        using var frozen = Cv2.ImDecode(performanceFrame.EncodePng(), ImreadModes.Color);
        Assert(frozen.Width == width && frozen.Height == height, "Frozen selection uses reduced coordinates.");
        Console.WriteLine($"  native score={baseline.BestScore:F4}, 720p score={result.BestScore:F4}, hit=({result.Hit.Left},{result.Hit.Top})");
    }

    static void CheckSceneGuard()
    {
        using var original = new Mat(360, 640, MatType.CV_8UC3, new Scalar(15, 25, 35));
        Cv2.Rectangle(original, new Rect(70, 45, 200, 170), new Scalar(60, 170, 220), -1);
        var first = FrameFromBgr(original, 1920, 1080);
        var same = FrameFromBgr(original, 1920, 1080);
        Assert(FrameGuard.IsSameScene(first, same), "Identical scenes were rejected.");
        using var reduced = new Mat();
        Cv2.Resize(original, reduced, new Size(320, 180), 0, 0, InterpolationFlags.Area);
        Assert(FrameGuard.IsSameScene(first, FrameFromBgr(reduced, 1920, 1080)),
            "Same scene at a different transport resolution was rejected.");
        using var changed = new Mat(360, 640, MatType.CV_8UC3, new Scalar(245, 245, 245));
        Assert(!FrameGuard.IsSameScene(first, FrameFromBgr(changed, 1920, 1080)),
            "A screen transition was accepted before touch.");
        Assert(!FrameGuard.IsSameScene(first, FrameFromBgr(original, 1080, 1920)),
            "A different native orientation was accepted.");
    }

    static void CheckRepeatedTargets()
    {
        const int nativeWidth = 1920, nativeHeight = 1080;
        var patternBounds = new Rect(630, 315, 210, 156);
        using var patternScreen = CreateMatchScreen(patternBounds);
        using var pattern = new Mat(patternScreen, patternBounds);
        using var screen = new Mat(nativeHeight, nativeWidth, MatType.CV_8UC3, new Scalar(21, 35, 52));
        var ordinaryBounds = Enumerable.Range(0, 5)
            .Select(slot => new Rect(87 + slot * 384, 720, pattern.Width, pattern.Height)).ToArray();
        var phantasmBounds = Enumerable.Range(0, 3)
            .Select(slot => new Rect(375 + slot * 480, 180, pattern.Width, pattern.Height)).ToArray();
        foreach (var bounds in ordinaryBounds.Concat(phantasmBounds))
        {
            using var destination = new Mat(screen, bounds);
            pattern.CopyTo(destination);
        }

        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "check-artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var templatePath = Path.Combine(artifactDirectory, "repeated-card-template.png");
        Cv2.ImWrite(templatePath, pattern);
        using var smaller = new Mat();
        Cv2.Resize(screen, smaller, new Size(1280, 720), 0, 0, InterpolationFlags.Area);
        var frame = FrameFromBgr(smaller, nativeWidth, nativeHeight);
        var queries = new[]
        {
        new ImageMatchQuery("ordinary", templatePath, 0.82, FindMultiple: true),
        new ImageMatchQuery("phantasm", templatePath, 0.82, FindMultiple: true, IsNoblePhantasm: true),
        new ImageMatchQuery("list", templatePath, 0.82, PreferTopmostMatch: true),
    };
        var results = new ImageMatchService().FindAll(frame, queries, out var width, out var height);
        Assert(width == nativeWidth && height == nativeHeight, "Repeated matcher returned reduced screen dimensions.");
        var ordinary = results.Single(result => result.Id == "ordinary");
        var phantasm = results.Single(result => result.Id == "phantasm");
        var list = results.Single(result => result.Id == "list");
        Assert(ordinary.MultipleHits is { Count: 5 }, $"Expected 5 lower-row cards, got {ordinary.MultipleHits?.Count ?? 0}.");
        Assert(phantasm.MultipleHits is { Count: 3 }, $"Expected 3 upper-row Noble Phantasms, got {phantasm.MultipleHits?.Count ?? 0}.");
        Assert(ordinary.MultipleHits!.All(hit => hit.CenterY >= nativeHeight / 2), "Ordinary-card query matched the upper row.");
        Assert(phantasm.MultipleHits!.All(hit => hit.CenterY < nativeHeight / 2), "Noble-Phantasm query matched the lower row.");
        foreach (var (evaluation, expectedBounds) in new[] { (ordinary, ordinaryBounds), (phantasm, phantasmBounds) })
        {
            var ordered = evaluation.MultipleHits!.OrderBy(hit => hit.Left).ToArray();
            for (var index = 0; index < expectedBounds.Length; index++)
            {
                var hit = ordered[index];
                var expected = expectedBounds[index];
                AssertNear(hit.Left, expected.X, 3, $"{evaluation.Id} card {index} X was not mapped to native coordinates.");
                AssertNear(hit.Top, expected.Y, 3, $"{evaluation.Id} card {index} Y was not mapped to native coordinates.");
                AssertNear(hit.Width, expected.Width, 2, $"{evaluation.Id} card {index} width was not mapped to native coordinates.");
                AssertNear(hit.Height, expected.Height, 2, $"{evaluation.Id} card {index} height was not mapped to native coordinates.");
                if (evaluation.Id == "ordinary")
                    Assert(Math.Clamp((int)(hit.CenterX * 5d / width), 0, 4) == index, "Mapped hit selected the wrong FGO card slot.");
            }
        }
        Assert(list.MultipleHits is { Count: 8 } && list.Hit is not null,
            $"Topmost-list query expected all 8 positions, got {list.MultipleHits?.Count ?? 0}.");
        AssertNear(list.Hit!.Top, phantasmBounds[0].Y, 3, "List query did not select the uppermost native position.");
        AssertNear(list.Hit.Left, phantasmBounds[0].X, 3, "List query did not break equal-height matches by leftmost position.");
        Console.WriteLine($"  720p multiple hits: ordinary={ordinary.MultipleHits!.Count}, phantasm={phantasm.MultipleHits!.Count}, list={list.MultipleHits!.Count}");
    }

    static void CheckStatistics()
    {
        static long At(double milliseconds) => 10_000_000L + (long)(milliseconds * Stopwatch.Frequency / 1000d);
        var statistics = new FrameStatistics();
        statistics.Record(0, At(0));
        statistics.Record(1, At(16));
        statistics.Record(4, At(32)); // Two skipped source frames.
        statistics.Record(5, At(432)); // A 400 ms stall/idle interval must remain visible.
        var snapshot = statistics.GetSnapshot(At(432));
        Assert(snapshot.ReceivedFrames == 4, "Received-frame total is incorrect.");
        Assert(snapshot.SourceSkippedFrames == 2, "Source sequence gap was not counted.");
        AssertNear(snapshot.ReceiveIntervalP95Ms, 400, 0.01, "P95 hid a long interval.");
        statistics.Record(0, At(448)); // Source sequence reset must not subtract skipped frames.
        statistics.Record(2, At(464));
        snapshot = statistics.GetSnapshot(At(464));
        Assert(snapshot.ReceivedFrames == 6 && snapshot.SourceSkippedFrames == 3,
            "Source sequence restart corrupted cumulative counts.");
        Assert(snapshot.LastReceivedAtTimestamp == At(464), "Last receive timestamp is incorrect.");
        statistics.Reset();
        snapshot = statistics.GetSnapshot();
        Assert(snapshot.ReceivedFrames == 0 && snapshot.SourceSkippedFrames == 0 && snapshot.LastReceivedAtTimestamp == 0
            && snapshot.ReceiveIntervalP95Ms == 0, "New-session statistics retained previous data.");
    }

    static void CheckGrpcDecode()
    {
        var clientType = typeof(GameFrame).Assembly.GetType("FgoAuto.Services.EmulatorGrpcClient", throwOnError: true)!;
        // Constructing a channel does not send requests. This check invokes only the decoder.
        using var client = (IDisposable)Activator.CreateInstance(clientType,
            [65534, "local-decoder-check-only", 32 * 1024 * 1024, 1280, 1280, 1280, 720])!;
        var decode = clientType.GetMethod("DecodeImage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("gRPC image decoder was not found.");
        var generatedUs = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 250) * 1000;
        var pixels = new byte[1280 * 720 * 3];
        pixels[0] = 0x12; pixels[1] = 0x34; pixels[2] = 0x56;
        pixels[^1] = 0xA7;
        using var format = new MemoryStream();
        WriteInteger(format, 1, 2); // RGB888
        WriteInteger(format, 3, 1280);
        WriteInteger(format, 4, 720);
        using var payload = new MemoryStream();
        WriteBytes(payload, 1, format.ToArray());
        WriteBytes(payload, 4, pixels);
        WriteInteger(payload, 5, 42);
        WriteInteger(payload, 6, generatedUs);
        var wire = payload.ToArray();
        var receivedAt = Stopwatch.GetTimestamp();
        object?[] arguments = [wire, receivedAt, 0L];
        var frame = (GameFrame?)decode.Invoke(client, arguments)
            ?? throw new InvalidOperationException("Valid gRPC screenshot decoded as empty.");
        Assert(frame.Width == 1280 && frame.Height == 720 && frame.NativeWidth == 1920 && frame.NativeHeight == 1080,
            "gRPC decoder lost stream/native coordinate dimensions.");
        Assert((long)arguments[2]! == 42, "gRPC decoder lost the source sequence number.");
        Assert(frame.ReceivedAtTimestamp == receivedAt, "gRPC decoder changed receive time.");
        Assert(frame.HasSourceTimestamp, "Valid emulator generation timing was marked unavailable.");
        var generationDelayMs = Stopwatch.GetElapsedTime(frame.CapturedAtTimestamp, receivedAt).TotalMilliseconds;
        Assert(generationDelayMs is >= 200 and < 1000,
            $"Host generation timestamp was not reflected in frame age: {generationDelayMs:F2} ms.");
        Assert(MemoryMarshal.TryGetArray(frame.RgbPixels, out var segment)
            && ReferenceEquals(segment.Array, wire) && segment.Offset > 0 && segment.Count == pixels.Length,
            "gRPC RGB pixels were copied instead of retaining a slice of the original message.");
        Assert(frame.RgbPixels.Span[0] == 0x12 && frame.RgbPixels.Span[1] == 0x34
            && frame.RgbPixels.Span[2] == 0x56 && frame.RgbPixels.Span[^1] == 0xA7,
            "gRPC decoder selected the wrong payload offset or length.");
        object?[] repeatedArguments = [wire, Stopwatch.GetTimestamp(), 0L];
        var next = (GameFrame?)decode.Invoke(client, repeatedArguments);
        Assert(next is not null && next.FrameId == frame.FrameId + 1 && (long)repeatedArguments[2]! == 42,
            "Local frame IDs did not increase independently of the source sequence.");
        var mapTouch = clientType.GetMethod("MapTouchCoordinates", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("gRPC touch coordinate mapper was not found.");
        Assert(((int, int))mapTouch.Invoke(client, [960, 540])! == (640, 360),
            "720p gRPC landscape touch centre is not mapped to the guest resolution.");
        Assert(((int, int))mapTouch.Invoke(client, [1919, 1079])! == (1279, 719),
            "720p gRPC landscape touch endpoint is outside the guest resolution.");

        using var portraitFormat = new MemoryStream();
        WriteInteger(portraitFormat, 1, 2);
        WriteInteger(portraitFormat, 3, 720);
        WriteInteger(portraitFormat, 4, 1280);
        using var portraitPayload = new MemoryStream();
        WriteBytes(portraitPayload, 1, portraitFormat.ToArray());
        WriteBytes(portraitPayload, 4, pixels);
        object?[] portraitArguments = [portraitPayload.ToArray(), Stopwatch.GetTimestamp(), 0L];
        var portraitFrame = (GameFrame?)decode.Invoke(client, portraitArguments);
        Assert(portraitFrame is not null && portraitFrame.NativeWidth == 1080 && portraitFrame.NativeHeight == 1920,
            "Portrait frame lost the logical coordinate orientation.");
        Assert(!portraitFrame!.HasSourceTimestamp && portraitFrame.CapturedAtTimestamp == portraitFrame.ReceivedAtTimestamp,
            "Missing emulator generation timing was reported as a measured zero transport delay.");
        Assert(((int, int))mapTouch.Invoke(client, [540, 960])! == (360, 640),
            "720p gRPC portrait touch centre is not mapped to the rotated guest resolution.");
        try
        {
            object?[] malformedArguments = [new byte[] { 0x22, 0x0A, 0x01 }, Stopwatch.GetTimestamp(), 0L];
            decode.Invoke(client, malformedArguments);
            throw new InvalidOperationException("An out-of-bounds protobuf payload length was accepted.");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
    }

    static void CheckGameProfiles()
    {
        var performance = EmulatorGameProfile.Create(performance: true);
        var quality = EmulatorGameProfile.Create(performance: false);
        Assert(performance.Width == 1280 && performance.Height == 720 && performance.Density == 187,
            "Performance profile did not reduce the actual guest display resolution.");
        Assert(quality.Width == 1920 && quality.Height == 1080 && quality.Density == 280,
            "Quality profile does not preserve the original display configuration.");
        Assert(performance.MapTouch(960, 540, landscape: true) == (640, 360), "Landscape touch centre is incorrect.");
        Assert(performance.MapTouch(1919, 1079, landscape: true) == (1279, 719), "Landscape endpoint is incorrect.");
        Assert(performance.MapTouch(int.MinValue, int.MinValue, landscape: true) == (0, 0), "Negative touch was not clamped.");
        Assert(performance.MapTouch(int.MaxValue, int.MaxValue, landscape: true) == (1279, 719), "Oversized touch was not clamped.");
        Assert(performance.MapTouch(540, 960, landscape: false) == (360, 640), "Portrait touch centre is incorrect.");
        Assert(performance.MapTouch(1079, 1919, landscape: false) == (719, 1279), "Portrait endpoint is incorrect.");
        Assert(quality.MapTouch(1478, 571, landscape: true) == (1478, 571), "1080p landscape touch is not identity.");
        Assert(quality.MapTouch(548, 1242, landscape: false) == (548, 1242), "1080p portrait touch is not identity.");

        var directory = Path.Combine(Path.GetTempPath(), "FgoAuto-frame-profile-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configPath = Path.Combine(directory, "config.ini");
            const string fixture = "# preserve comment and empty line\r\n\r\n"
                + "AvdId=FgoAuto_API_34\r\n hw.lcd.width = 1920  ; keep display comment\r\n"
                + "hw.lcd.height=1080\r\nhw.lcd.density = 280\r\n"
                + "hw.gpu.mode=auto\r\nHW.GPS = yes\r\nhw.audioInput=yes\r\n"
                + "hw.audioOutput=yes\r\ndisk.dataPartition.path=C:\\Games\\FgoAuto\\userdata-qemu.img\r\n"
                + "; hw.lcd.width=9999 must stay a comment\r\nuser.custom=keep me";
            File.WriteAllText(configPath, fixture, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert(performance.ApplyToConfig(configPath), "First profile update reported no change.");
            var bytes = File.ReadAllBytes(configPath);
            var updated = File.ReadAllText(configPath);
            Assert(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM was lost.");
            Assert(updated.Contains("# preserve comment and empty line\r\n\r\n", StringComparison.Ordinal),
                "Existing comments/empty lines/newline style changed.");
            Assert(updated.Contains(" hw.lcd.width = 1280  ; keep display comment\r\n", StringComparison.Ordinal),
                "Existing display key spacing/comment was not preserved.");
            Assert(updated.Contains("hw.lcd.height=720\r\n", StringComparison.Ordinal)
                && updated.Contains("hw.lcd.density = 187\r\n", StringComparison.Ordinal),
                "Actual render size/density was not updated.");
            Assert(updated.Contains("hw.gpu.enabled=yes\r\n", StringComparison.Ordinal)
                && updated.Contains("hw.gpu.mode=host\r\n", StringComparison.Ordinal)
                && updated.Contains("hw.camera.front=none\r\n", StringComparison.Ordinal)
                && updated.Contains("hw.camera.back=none\r\n", StringComparison.Ordinal)
                && updated.Contains("HW.GPS = no\r\n", StringComparison.Ordinal)
                && updated.Contains("hw.audioInput=no\r\n", StringComparison.Ordinal),
                "Scoped GPU/unused-hardware settings were not applied.");
            Assert(updated.Contains("hw.audioOutput=yes\r\n", StringComparison.Ordinal)
                && updated.Contains("disk.dataPartition.path=C:\\Games\\FgoAuto\\userdata-qemu.img\r\n", StringComparison.Ordinal)
                && updated.Contains("; hw.lcd.width=9999 must stay a comment\r\n", StringComparison.Ordinal)
                && updated.Contains("user.custom=keep me\r\n", StringComparison.Ordinal),
                "Unrelated hardware, guest data paths, comments or custom keys were modified.");
            var originalWriteTime = new DateTime(2020, 3, 8, 10, 20, 30, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(configPath, originalWriteTime);
            Assert(!performance.ApplyToConfig(configPath), "Applying an identical profile was not idempotent.");
            Assert(File.ReadAllBytes(configPath).AsSpan().SequenceEqual(bytes)
                && File.GetLastWriteTimeUtc(configPath) == originalWriteTime,
                "An unchanged profile rewrote the config file.");
            Assert(quality.ApplyToConfig(configPath), "Switching back to 1080p reported no change.");
            var restored = File.ReadAllText(configPath);
            Assert(restored.Contains(" hw.lcd.width = 1920  ; keep display comment\r\n", StringComparison.Ordinal)
                && restored.Contains("hw.lcd.height=1080\r\n", StringComparison.Ordinal)
                && restored.Contains("hw.lcd.density = 280\r\n", StringComparison.Ordinal),
                "Switching back to the original render size failed.");
            Assert(!quality.ApplyToConfig(configPath), "Restored quality profile is not idempotent.");
            Assert(Directory.GetFiles(directory).Length == 1, "Profile update left a temporary config file behind.");

            // New profiles also preserve BOM-free LF files and update duplicate keys consistently.
            var lfPath = Path.Combine(directory, "lf.ini");
            File.WriteAllText(lfPath, "# LF config\nhw.lcd.width=1920\nhw.lcd.width = 1920\nhw.audioOutput=no\n",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert(performance.ApplyToConfig(lfPath), "LF config did not update.");
            var lfBytes = File.ReadAllBytes(lfPath);
            var lfText = File.ReadAllText(lfPath);
            Assert(!lfBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) && !lfText.Contains('\r'),
                "BOM-free LF config acquired a BOM or CRLF newlines.");
            Assert(lfText.Contains("hw.lcd.width=1280\nhw.lcd.width = 1280\n", StringComparison.Ordinal)
                && lfText.Contains("hw.audioOutput=no\n", StringComparison.Ordinal),
                "Duplicate display keys or existing audio output were not preserved correctly.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static void WriteInteger(Stream stream, int field, ulong value)
    {
        WriteVarint(stream, (ulong)(field << 3));
        WriteVarint(stream, value);
    }

    static void WriteBytes(Stream stream, int field, byte[] bytes)
    {
        WriteVarint(stream, (ulong)((field << 3) | 2));
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 128)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }


    static Mat CreateMatchScreen(Rect targetBounds)
    {
        var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(27, 39, 48));
        // A fixed seed and broad textured cells survive down/up scaling while avoiding repeated matches.
        var random = new Random(63214);
        for (var y = 0; y < targetBounds.Height; y += 12)
            for (var x = 0; x < targetBounds.Width; x += 14)
            {
                var cell = new Rect(targetBounds.X + x, targetBounds.Y + y,
                    Math.Min(14, targetBounds.Width - x), Math.Min(12, targetBounds.Height - y));
                Cv2.Rectangle(screen, cell, new Scalar(random.Next(30, 240), random.Next(30, 240), random.Next(30, 240)), -1);
            }
        Cv2.Rectangle(screen, targetBounds, new Scalar(250, 250, 250), 3);
        Cv2.Circle(screen, new Point(targetBounds.X + 47, targetBounds.Y + 53), 24, new Scalar(10, 10, 10), 5);
        Cv2.Line(screen, new Point(targetBounds.X + 116, targetBounds.Y + 15),
            new Point(targetBounds.X + 180, targetBounds.Y + 122), new Scalar(250, 250, 250), 7);
        return screen;
    }

    static GameFrame FrameFromBgr(Mat bgr, int nativeWidth, int nativeHeight)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
        var pixels = new byte[checked(rgb.Width * rgb.Height * 3)];
        Marshal.Copy(rgb.Data, pixels, 0, pixels.Length);
        return new GameFrame(rgb.Width, rgb.Height, pixels, 1, Stopwatch.GetTimestamp(), nativeWidth, nativeHeight);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void AssertNear(double actual, double expected, double tolerance, string message)
        => Assert(Math.Abs(actual - expected) <= tolerance, $"{message} actual={actual:F4}, expected={expected:F4}, tolerance={tolerance:F4}");

}
