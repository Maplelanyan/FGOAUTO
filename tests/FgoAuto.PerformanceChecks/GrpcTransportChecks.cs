using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using Grpc.Core;
using FgoAuto.Models;

internal static class GrpcTransportChecks
{
    private static readonly Type ClientType = typeof(GameFrame).Assembly.GetType(
        "FgoAuto.Services.EmulatorGrpcClient", throwOnError: true)!;
    private static readonly Marshaller<byte[]> ProductionMarshaller = (Marshaller<byte[]>)ClientType
        .GetField("BytesMarshaller", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    public static void Run()
    {
        // Both contiguous and segmented contexts occur in supported gRPC APIs.
        // The response must be fully copied before gRPC recycles either storage.
        var storage = new byte[1280 * 720 * 3 + 37];
        new Random(99143).NextBytes(storage);
        var expected = storage.AsSpan(19, storage.Length - 37).ToArray();
        var sliced = new ReadOnlySequence<byte>(storage.AsMemory(19, storage.Length - 37));
        var owned = ProductionMarshaller.ContextualDeserializer(new Context(sliced));
        Require(owned.AsSpan().SequenceEqual(expected), "Contiguous payload was not completely copied.");
        Require(!ReferenceEquals(owned, storage), "Response retained recyclable gRPC storage.");

        var first = new Segment(storage.AsMemory(19, 1));
        var last = first.Append(storage.AsMemory(20, 131071)).Append(
            storage.AsMemory(131091, storage.Length - 37 - 131072));
        var segmented = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        var second = ProductionMarshaller.ContextualDeserializer(new Context(segmented));
        Require(second.AsSpan().SequenceEqual(expected), "Segmented payload was not completely copied.");
        Array.Fill(storage, (byte)0xC7);
        Require(owned.AsSpan().SequenceEqual(expected) && second.AsSpan().SequenceEqual(expected),
            "Published pixels changed after the receive buffer was reused.");
        Require(!ReferenceEquals(owned, second), "Two responses shared writable storage.");
        Require(ProductionMarshaller.ContextualDeserializer(new Context(ReadOnlySequence<byte>.Empty)).Length == 0,
            "Empty unary response failed.");
        ThrowsInvalidData(() => ProductionMarshaller.ContextualDeserializer(new Context(sliced, -1)));
        ThrowsInvalidData(() => ProductionMarshaller.ContextualDeserializer(new Context(sliced, checked((int)sliced.Length - 1))));

        var serialization = new OutputContext();
        ProductionMarshaller.ContextualSerializer(expected, serialization);
        Require(ReferenceEquals(expected, serialization.Payload), "Request marshalling introduced an extra copy.");

        Console.WriteLine("PASS gRPC owned payload: complete segmented copies, buffer lifetime, lengths and request compatibility");
    }

    /// <summary>
    /// Optional local microbenchmark; no emulator or HTTP/2 transfer. Uses the
    /// exact previous byte[] marshaller and the actual production replacement.
    /// Alternating blocks reduce fixed-order warmup/GC bias. No time threshold
    /// is asserted: this measures copying, not game or end-to-end preview FPS.
    /// </summary>
    public static void RunBenchmark()
    {
        Console.WriteLine($"TRANSPORT_COPY environment: .NET {Environment.Version}; "
            + $"ServerGC={System.Runtime.GCSettings.IsServerGC}; processors={Environment.ProcessorCount}; "
            + "local marshalling only, excludes emulator/readback/HTTP2/display");
        var baseline = Marshallers.Create<byte[]>(static bytes => bytes, static bytes => bytes);
        foreach (var (width, height) in new[] { (1280, 720), (1920, 1080) })
        {
            var source = new byte[width * height * 3 + 36];
            new Random(727).NextBytes(source);
            var context = new Context(new ReadOnlySequence<byte>(source), allowLegacyCopy: true);
            var oldSamples = new List<double>(640);
            var newSamples = new List<double>(640);
            long oldBytes = 0, newBytes = 0;
            byte[]? retained = null;
            for (var i = 0; i < 12; i++)
            {
                retained = baseline.ContextualDeserializer(context);
                retained = ProductionMarshaller.ContextualDeserializer(context);
            }
            for (var block = 0; block < 40; block++)
            {
                if ((block & 1) == 0) { Measure(baseline, oldSamples, ref oldBytes); Measure(ProductionMarshaller, newSamples, ref newBytes); }
                else { Measure(ProductionMarshaller, newSamples, ref newBytes); Measure(baseline, oldSamples, ref oldBytes); }
            }
            Print("previous zeroed-array copy", oldSamples, oldBytes);
            Print("owned uninitialized-array copy", newSamples, newBytes);
            GC.KeepAlive(retained);

            void Measure(Marshaller<byte[]> marshaller, List<double> samples, ref long bytes)
            {
                for (var i = 0; i < 16; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    retained = marshaller.ContextualDeserializer(context);
                    samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    if (retained.Length != source.Length || retained[^1] != source[^1])
                        throw new InvalidOperationException("Copy benchmark returned an incomplete payload.");
                }
            }

            void Print(string name, List<double> samples, long bytes)
            {
                samples.Sort();
                Console.WriteLine($"TRANSPORT_COPY {width}x{height} {name}; n={samples.Count}; "
                    + $"mean={samples.Average():F4}ms; P95={samples[(int)Math.Ceiling(samples.Count * .95) - 1]:F4}ms; "
                    + $"allocated={bytes / samples.Count}B/copy");
            }
        }
    }

    private static void ThrowsInvalidData(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A mismatched payload length was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Context(ReadOnlySequence<byte> payload, int? reportedLength = null,
        bool allowLegacyCopy = false) : DeserializationContext
    {
        public override int PayloadLength => reportedLength ?? checked((int)payload.Length);
        public override ReadOnlySequence<byte> PayloadAsReadOnlySequence() => payload;
        public override byte[] PayloadAsNewBuffer() => allowLegacyCopy ? payload.ToArray()
            : throw new InvalidOperationException("Production deserialization fell back to a zeroed full-frame copy.");
    }

    private sealed class OutputContext : SerializationContext
    {
        public byte[]? Payload { get; private set; }
        public override void Complete(byte[] payload) => Payload = payload;
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
