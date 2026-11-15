using System.Text.Json;
using FgoAuto.Services;

internal static class VideoSettingsChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FgoAuto-video-settings-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(directory, "android-runtime");
        Directory.CreateDirectory(runtime);
        var settingsPath = Path.Combine(runtime, "video-settings.json");
        try
        {
            // Existing users have only this field. A new option must not silently
            // switch their renderer or reset their resolution choice.
            File.WriteAllText(settingsPath, "{\"UsePerformanceMode\":true}");
            using (var service = new AndroidEmulatorService(directory))
            {
                Require(service.UsePerformanceMode && !service.UseOpenGlMode, "Legacy settings changed graphics mode.");
                service.SetOpenGlMode(true);
                Require(service.UsePerformanceMode && service.UseOpenGlMode, "Graphics choice reset resolution.");
            }
            using (var reloaded = new AndroidEmulatorService(directory))
            {
                Require(reloaded.UsePerformanceMode && reloaded.UseOpenGlMode, "Graphics choice was not persisted.");
                reloaded.SetPerformanceMode(false);
                Require(!reloaded.UsePerformanceMode && reloaded.UseOpenGlMode, "Resolution choice reset graphics mode.");
                reloaded.SetOpenGlMode(false);
            }
            using (var restored = new AndroidEmulatorService(directory))
                Require(!restored.UsePerformanceMode && !restored.UseOpenGlMode, "Returning to standard mode was not persisted.");
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Require(!json.RootElement.GetProperty("UsePerformanceMode").GetBoolean()
                && !json.RootElement.GetProperty("UseOpenGlMode").GetBoolean(), "Saved settings are inconsistent.");
            Require(Directory.GetFiles(runtime).Length == 1, "Saving settings left temporary files behind.");
            Console.WriteLine("PASS video settings: legacy compatibility, independent choices and persisted rollback");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
