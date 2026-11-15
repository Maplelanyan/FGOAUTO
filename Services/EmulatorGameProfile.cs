using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FgoAuto.Services;

/// <summary>
/// The guest display size used for rendering. Rules and previews continue to use
/// the existing 1920x1080 logical coordinate space independently of this size.
/// </summary>
public sealed class EmulatorGameProfile
{
    private EmulatorGameProfile(int width, int height, int density)
    {
        Width = width;
        Height = height;
        Density = density;
    }

    public int Width { get; }
    public int Height { get; }
    public int Density { get; }

    public static EmulatorGameProfile Create(bool performance) => performance
        ? new(1280, 720, 187)
        : new(1920, 1080, 280);

    /// <summary>Maps an existing rule coordinate to the current guest display.</summary>
    public (int X, int Y) MapTouch(int x, int y, bool landscape) => MapTouchToDisplay(x, y, landscape, Width, Height);

    public static (int X, int Y) MapTouchToDisplay(int x, int y, bool landscape, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var logicalWidth = landscape ? 1920 : 1080;
        var logicalHeight = landscape ? 1080 : 1920;
        var displayWidth = landscape ? width : height;
        var displayHeight = landscape ? height : width;
        return (
            (int)((long)Math.Clamp(x, 0, logicalWidth - 1) * displayWidth / logicalWidth),
            (int)((long)Math.Clamp(y, 0, logicalHeight - 1) * displayHeight / logicalHeight));
    }

    /// <summary>
    /// Applies only display and unused-hardware keys. Existing comments, other
    /// values and UTF-8 BOM/newline style are retained. Guest data is untouched.
    /// </summary>
    public bool ApplyToConfig(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        configPath = Path.GetFullPath(configPath);
        var originalBytes = File.ReadAllBytes(configPath);
        var hasBom = originalBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var encoding = new UTF8Encoding(hasBom, throwOnInvalidBytes: true);
        var text = encoding.GetString(originalBytes, hasBom ? 3 : 0, originalBytes.Length - (hasBom ? 3 : 0));
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hw.lcd.width"] = Width.ToString(CultureInfo.InvariantCulture),
            ["hw.lcd.height"] = Height.ToString(CultureInfo.InvariantCulture),
            ["hw.lcd.density"] = Density.ToString(CultureInfo.InvariantCulture),
            ["hw.gpu.enabled"] = "yes",
            ["hw.gpu.mode"] = "host",
            ["hw.camera.back"] = "none",
            ["hw.camera.front"] = "none",
            ["hw.gps"] = "no",
            ["hw.audioInput"] = "no",
        };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder(text.Length + 256);
        var changed = false;
        var newLine = Regex.Match(text, "\\r\\n|\\r|\\n").Value;
        if (newLine.Length == 0) newLine = Environment.NewLine;

        foreach (Match match in Regex.Matches(text, "([^\\r\\n]*)(\\r\\n|\\r|\\n|$)"))
        {
            if (match.Length == 0) continue;
            var line = match.Groups[1].Value;
            var terminator = match.Groups[2].Value;
            var equals = line.IndexOf('=');
            var trimmed = line.AsSpan().TrimStart();
            if (equals >= 0 && !trimmed.StartsWith("#") && !trimmed.StartsWith(";"))
            {
                var key = line[..equals].Trim();
                if (settings.TryGetValue(key, out var expected))
                {
                    found.Add(key);
                    var rawValue = line[(equals + 1)..];
                    var valueEnd = rawValue.Length;
                    for (var i = 0; i < rawValue.Length; i++)
                    {
                        if ((rawValue[i] == '#' || rawValue[i] == ';')
                            && (i == 0 || char.IsWhiteSpace(rawValue[i - 1])))
                        {
                            valueEnd = i;
                            break;
                        }
                    }
                    var valueStart = 0;
                    while (valueStart < valueEnd && char.IsWhiteSpace(rawValue[valueStart])) valueStart++;
                    while (valueEnd > valueStart && char.IsWhiteSpace(rawValue[valueEnd - 1])) valueEnd--;
                    if (!rawValue.AsSpan(valueStart, valueEnd - valueStart).SequenceEqual(expected))
                    {
                        line = line[..(equals + 1)] + rawValue[..valueStart] + expected + rawValue[valueEnd..];
                        changed = true;
                    }
                }
            }
            output.Append(line).Append(terminator);
        }

        foreach (var setting in settings)
        {
            if (found.Contains(setting.Key)) continue;
            if (output.Length > 0 && output[^1] != '\n' && output[^1] != '\r') output.Append(newLine);
            output.Append(setting.Key).Append('=').Append(setting.Value).Append(newLine);
            changed = true;
        }
        if (!changed) return false;

        var temporaryPath = Path.Combine(Path.GetDirectoryName(configPath)!,
            $".{Path.GetFileName(configPath)}.fgoauto-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (hasBom) stream.Write(encoding.GetPreamble());
                stream.Write(encoding.GetBytes(output.ToString()));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return true;
    }
}
