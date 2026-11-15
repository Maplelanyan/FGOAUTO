using System.Diagnostics;
using System.Security;

internal static class RecognitionComparison
{
    public static int Run()
    {
        var root = FindRoot(Environment.CurrentDirectory) ?? FindRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Run this comparison from the FgoAuto checkout.");
        var output = Path.Combine(root, ".build-tools", "recognition-ab");
        Directory.CreateDirectory(output);
        var gitInfo = StartInfo("git", root, ["show", "a5d4937:Services/ImageMatchService.cs"]);
        gitInfo.RedirectStandardOutput = gitInfo.RedirectStandardError = true;
        using var git = Process.Start(gitInfo) ?? throw new IOException("Cannot read the baseline matcher.");
        var stdout = git.StandardOutput.ReadToEndAsync();
        var stderr = git.StandardError.ReadToEndAsync();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new IOException("Baseline a5d4937 is unavailable: " + stderr.GetAwaiter().GetResult());
        var baseline = stdout.GetAwaiter().GetResult()
            .Replace("namespace FgoAuto.Services;", "namespace FgoAuto.Services.Before;", StringComparison.Ordinal)
            .Replace("public sealed class ImageMatchService", "public sealed class ImageMatchService : IDisposable", StringComparison.Ordinal);
        // Only an interface adapter for the common benchmark fixture; the old
        // matching algorithm remains exactly as stored in the baseline commit.
        baseline = baseline.Insert(baseline.LastIndexOf('}'), "public void Dispose() { }" + Environment.NewLine);
        File.WriteAllText(Path.Combine(output, "BeforeMatcher.cs"), baseline);
        var fixturePath = Path.Combine(root, "tests", "FgoAuto.PerformanceChecks", "RecognitionChecks.cs");
        var fixture = File.ReadAllText(fixturePath)
            .Replace("internal static class RecognitionChecks", "internal static class BeforeRecognitionChecks", StringComparison.Ordinal)
            .Replace("using FgoAuto.Services;", "using FgoAuto.Services.Before;", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(output, "BeforeChecks.cs"), fixture);
        var project = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><ProjectReference Include="{SecurityElement.Escape(Path.Combine(root, "FgoAuto.csproj"))}" /><Compile Include="{SecurityElement.Escape(fixturePath)}" Link="AfterChecks.cs" /></ItemGroup>
            </Project>
            """;
        var projectPath = Path.Combine(output, "Comparison.csproj");
        File.WriteAllText(projectPath, project);
        File.WriteAllText(Path.Combine(output, "Program.cs"), """
            OpenCvSharp.Cv2.SetNumThreads(Math.Min(2, Environment.ProcessorCount));
            Console.WriteLine("BEFORE:"); BeforeRecognitionChecks.RunBenchmark();
            Console.WriteLine("AFTER:"); RecognitionChecks.RunBenchmark();
            Console.WriteLine("AFTER REPEAT:"); RecognitionChecks.RunBenchmark();
            Console.WriteLine("BEFORE REPEAT:"); BeforeRecognitionChecks.RunBenchmark();
            """);
        var comparisonInfo = StartInfo("dotnet", root,
            ["run", "--project", projectPath, "-c", "Release", "--artifacts-path", Path.Combine(root, ".build-tools", "recognition-ab-artifacts")]);
        comparisonInfo.RedirectStandardOutput = comparisonInfo.RedirectStandardError = true;
        using var comparison = Process.Start(comparisonInfo)
            ?? throw new IOException("Cannot start the isolated recognition comparison.");
        var outputTask = ForwardAsync(comparison.StandardOutput, Console.Out);
        var errorTask = ForwardAsync(comparison.StandardError, Console.Error);
        comparison.WaitForExit();
        Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
        return comparison.ExitCode;
    }

    private static ProcessStartInfo StartInfo(string executable, string directory, string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task ForwardAsync(StreamReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static string? FindRoot(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FgoAuto.csproj"))) return directory.FullName;
        return null;
    }
}
