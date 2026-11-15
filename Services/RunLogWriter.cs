using System.Text;
using System.Threading.Channels;

namespace FgoAuto.Services;

/// <summary>Write diagnostic messages off the UI thread; a disk failure never stops automation.</summary>
public sealed class RunLogWriter : IDisposable
{
    private readonly Channel<string> _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writer;
    public string FilePath { get; }

    public RunLogWriter(string directory)
    {
        FilePath = Path.Combine(directory, "logs", $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        _writer = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await foreach (var message in _messages.Reader.ReadAllAsync())
                {
                    await writer.WriteLineAsync(message);
                    await writer.FlushAsync();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        });
    }

    public void Write(string message) => _messages.Writer.TryWrite(message);
    public void Dispose()
    {
        _messages.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
    }
}
