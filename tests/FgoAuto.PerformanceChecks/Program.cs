using OpenCvSharp;

// Local, deterministic checks: no emulator, game input or user configuration.
Cv2.SetNumThreads(Math.Min(2, Environment.ProcessorCount));
try
{
    FramePipelineChecks.Run();
    FrameStatisticsChecks.Run();
    VideoSettingsChecks.Run();
    await TouchDispatchChecks.RunAsync();
    Console.WriteLine("All performance regression checks passed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
