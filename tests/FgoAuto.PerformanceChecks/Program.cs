using OpenCvSharp;

// Local, deterministic checks: no emulator, game input or user configuration.
Cv2.SetNumThreads(Math.Min(2, Environment.ProcessorCount));
try
{
    FramePipelineChecks.Run();
    FrameStatisticsChecks.Run();
    VideoSettingsChecks.Run();
    GrpcTransportChecks.Run();
    RecognitionChecks.Run();
    await DiagnosticModeChecks.RunAsync();
    await DiagnosticLifecycleChecks.RunAsync();
    await TouchDispatchChecks.RunAsync();
    await AutomationCompatibilityChecks.RunAsync();
    if (args.Contains("--benchmark-transport")) GrpcTransportChecks.RunBenchmark();
    if (args.Contains("--benchmark-recognition")) RecognitionChecks.RunBenchmark();
    Console.WriteLine("All performance regression checks passed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
