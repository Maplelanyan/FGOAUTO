using FgoAuto.Services;

internal static class TouchDispatchChecks
{
    public static async Task RunAsync()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("Slow RPC coalesces a burst of moves and preserves the final up", CoalescesMovesAsync),
            ("Quick taps retain down/up order", PreservesTapOrderAsync),
            ("Cancellation drops pending gestures instead of replaying them", DiscardsPendingGesturesAsync),
            ("Cancellation ignores late moves until physical up", IgnoresCancelledMovesAsync),
            ("Dispose releases a possibly pressed pointer and tolerates late up", DisposesSafelyAsync),
            ("Failed down is released before another gesture", RecoversFromFailureAsync),
            ("Backpressure bounds the gesture queue", BoundsGestureQueueAsync),
        };
        foreach (var check in checks)
        {
            await check.Run().WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine($"PASS {check.Name}");
        }
    }

    private static async Task CoalescesMovesAsync()
    {
        var sender = new Sender();
        var blocked = NewSignal();
        var entered = NewSignal();
        sender.Handler = async (command, token) =>
        {
            if (command == new Command(10, 20, 1))
            {
                entered.TrySetResult();
                await blocked.Task.WaitAsync(token);
            }
        };
        using var dispatcher = new PreviewTouchDispatcher(sender.SendAsync);
        dispatcher.Submit(10, 20, 1);
        await entered.Task;
        for (var x = 11; x <= 1010; x++) dispatcher.Submit(x, 20, 1);
        dispatcher.Submit(1010, 20, 0);
        blocked.SetResult();
        await sender.WaitForCountAsync(3);
        await dispatcher.CancelPendingAsync();
        AssertCommands(sender, new(10, 20, 1), new(1010, 20, 1), new(1010, 20, 0));
        Assert(dispatcher.GetSnapshot().CoalescedMoves >= 999, "Moves accumulated instead of being replaced.");
    }

    private static async Task PreservesTapOrderAsync()
    {
        var sender = new Sender();
        var blocked = NewSignal();
        var entered = NewSignal();
        sender.Handler = async (command, token) =>
        {
            if (command == new Command(1, 2, 1))
            {
                entered.TrySetResult();
                await blocked.Task.WaitAsync(token);
            }
        };
        using var dispatcher = new PreviewTouchDispatcher(sender.SendAsync);
        dispatcher.Submit(1, 2, 1);
        await entered.Task;
        dispatcher.Submit(1, 2, 0);
        dispatcher.Submit(3, 4, 1);
        dispatcher.Submit(3, 4, 0);
        blocked.SetResult();
        await sender.WaitForCountAsync(4);
        await dispatcher.CancelPendingAsync();
        AssertCommands(sender, new(1, 2, 1), new(1, 2, 0), new(3, 4, 1), new(3, 4, 0));
    }

    private static async Task DiscardsPendingGesturesAsync()
    {
        var sender = new Sender();
        var blocked = NewSignal();
        var entered = NewSignal();
        sender.Handler = async (command, token) =>
        {
            if (command.Pressure == 1)
            {
                entered.TrySetResult();
                await blocked.Task.WaitAsync(token);
            }
        };
        using var dispatcher = new PreviewTouchDispatcher(sender.SendAsync);
        dispatcher.Submit(1, 2, 1);
        await entered.Task;
        dispatcher.Submit(1, 2, 0);
        dispatcher.Submit(3, 4, 1);
        dispatcher.Submit(3, 4, 0);
        var cancelled = dispatcher.CancelPendingAsync();
        blocked.SetResult();
        await cancelled;
        AssertCommands(sender, new(1, 2, 1), new(1, 2, 0));
    }

    private static async Task IgnoresCancelledMovesAsync()
    {
        var sender = new Sender();
        var blocked = NewSignal();
        var entered = NewSignal();
        sender.Handler = async (command, token) =>
        {
            if (command == new Command(1, 2, 1))
            {
                entered.TrySetResult();
                await blocked.Task.WaitAsync(token);
            }
        };
        using var dispatcher = new PreviewTouchDispatcher(sender.SendAsync);
        dispatcher.Submit(1, 2, 1);
        await entered.Task;
        dispatcher.Submit(30, 40, 1);
        var cancelled = dispatcher.CancelPendingAsync();
        Assert(!dispatcher.Submit(50, 60, 1), "A late move became a new press after cancellation.");
        dispatcher.Submit(50, 60, 0);
        blocked.SetResult();
        await cancelled;
        AssertCommands(sender, new(1, 2, 1), new(1, 2, 0));
        dispatcher.Submit(7, 8, 1);
        dispatcher.Submit(7, 8, 0);
        await sender.WaitForCountAsync(4);
        await dispatcher.CancelPendingAsync();
        AssertCommands(sender, new(1, 2, 1), new(1, 2, 0), new(7, 8, 1), new(7, 8, 0));
    }

    private static async Task DisposesSafelyAsync()
    {
        var sender = new Sender();
        var entered = NewSignal();
        sender.Handler = async (command, token) =>
        {
            if (command.Pressure == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var dispatcher = new PreviewTouchDispatcher(sender.SendAsync);
        dispatcher.Submit(1, 2, 1);
        await entered.Task;
        await Task.Run(dispatcher.Dispose);
        AssertCommands(sender, new(1, 2, 1), new(1, 2, 0));
        Assert(!dispatcher.Submit(30, 40, 0), "Disposed dispatcher accepted late up.");
        await dispatcher.CancelPendingAsync(pointerReleased: true);
        dispatcher.Dispose();
    }

    private static async Task RecoversFromFailureAsync()
    {
        var sender = new Sender();
        var reported = NewSignal();
        sender.Handler = (command, _) => command == new Command(1, 2, 1)
            ? Task.FromException(new InvalidOperationException("Simulated transport failure")) : Task.CompletedTask;
        using var dispatcher = new PreviewTouchDispatcher(sender.SendAsync, _ => reported.TrySetResult());
        dispatcher.Submit(1, 2, 1);
        await reported.Task;
        await sender.WaitForCountAsync(2);
        await dispatcher.CancelPendingAsync();
        Assert(!dispatcher.Submit(30, 40, 1), "Failure allowed a late move to press again.");
        dispatcher.Submit(30, 40, 0);
        dispatcher.Submit(7, 8, 1);
        dispatcher.Submit(7, 8, 0);
        await sender.WaitForCountAsync(4);
        await dispatcher.CancelPendingAsync();
        AssertCommands(sender, new(1, 2, 1), new(1, 2, 0), new(7, 8, 1), new(7, 8, 0));
    }

    private static async Task BoundsGestureQueueAsync()
    {
        var sender = new Sender();
        var blocked = NewSignal();
        var entered = NewSignal();
        sender.Handler = async (command, token) =>
        {
            if (command.Pressure == 1)
            {
                entered.TrySetResult();
                await blocked.Task.WaitAsync(token);
            }
        };
        using var dispatcher = new PreviewTouchDispatcher(sender.SendAsync);
        dispatcher.Submit(1, 2, 1);
        await entered.Task;
        dispatcher.Submit(1, 2, 0);
        for (var x = 10; x < 110; x++)
        {
            dispatcher.Submit(x, 20, 1);
            dispatcher.Submit(x, 20, 0);
        }
        var snapshot = dispatcher.GetSnapshot();
        Assert(snapshot.PendingGestures <= 5 && snapshot.DiscardedGestures > 0,
            "Gesture queue was unbounded when the transport stalled.");
        var cancelled = dispatcher.CancelPendingAsync();
        blocked.SetResult();
        await cancelled;
        AssertCommands(sender, new(1, 2, 1), new(1, 2, 0));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertCommands(Sender sender, params Command[] expected) =>
        Assert(sender.Commands.SequenceEqual(expected),
            $"Unexpected touch order: {string.Join(", ", sender.Commands)}; expected {string.Join(", ", expected)}.");

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private readonly record struct Command(int X, int Y, int Pressure);

    private sealed class Sender
    {
        private readonly object _sync = new();
        private readonly List<Command> _commands = [];
        private readonly SemaphoreSlim _sent = new(0);
        public Func<Command, CancellationToken, Task>? Handler { get; set; }
        public Command[] Commands { get { lock (_sync) return _commands.ToArray(); } }

        public async Task SendAsync(int x, int y, int pressure, CancellationToken token)
        {
            var command = new Command(x, y, pressure);
            lock (_sync) _commands.Add(command);
            _sent.Release();
            if (Handler is not null) await Handler(command, token);
        }

        public async Task WaitForCountAsync(int count)
        {
            while (Commands.Length < count) await _sent.WaitAsync();
        }
    }
}
