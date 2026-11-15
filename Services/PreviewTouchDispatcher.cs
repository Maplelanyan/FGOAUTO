using System.Diagnostics;

namespace FgoAuto.Services;

/// <summary>
/// Serializes single-pointer gestures without building a queue of obsolete moves.
/// Down/up transitions stay paired; a gesture which has not started may be dropped
/// when the transport is backed up, rather than replaying an old click later.
/// </summary>
public sealed class PreviewTouchDispatcher : IDisposable
{
    private const int MaximumPendingGestures = 4;
    private static readonly TimeSpan MaximumQueuedGestureAge = TimeSpan.FromMilliseconds(250);
    private readonly object _sync = new();
    private readonly Queue<Gesture> _pending = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<int, int, int, CancellationToken, Task> _send;
    private readonly Action<Exception>? _reportError;
    private readonly Queue<double> _rpcDurations = new(120);
    private Gesture? _inputGesture, _active;
    private bool _pointerHeld, _workerRunning, _disposed;
    private Task _worker = Task.CompletedTask;
    private long _sentCommands, _coalescedMoves, _discardedGestures;
    private double _lastRpcMs;

    public PreviewTouchDispatcher(Func<int, int, int, CancellationToken, Task> send,
        Action<Exception>? reportError = null)
    {
        _send = send;
        _reportError = reportError;
    }

    public bool Submit(int x, int y, int pressure)
    {
        if (pressure is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(pressure));
        lock (_sync)
        {
            if (_disposed) return false;
            var point = new TouchPoint(x, y);
            if (pressure == 1 && !_pointerHeld)
            {
                _pointerHeld = true;
                if (_pending.Count >= MaximumPendingGestures)
                {
                    _discardedGestures++;
                    return false;
                }
                _inputGesture = new Gesture(point);
                _pending.Enqueue(_inputGesture);
            }
            else if (_inputGesture is { } gesture)
            {
                if (gesture.Latest != point)
                {
                    if (gesture.HasPendingMove) _coalescedMoves++;
                    gesture.Latest = point;
                    gesture.HasPendingMove = true;
                }
                if (pressure == 0)
                {
                    gesture.Released = true;
                    _inputGesture = null;
                }
            }
            else
            {
                // A rejected/cancelled down must not become a new down on its next move.
                if (pressure == 0) _pointerHeld = false;
                return false;
            }

            if (pressure == 0) _pointerHeld = false;
            Signal();
            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(ProcessAsync);
            }
            return true;
        }
    }

    /// <summary>Discards unsent gestures and releases the currently sent pointer.</summary>
    public Task CancelPendingAsync(bool pointerReleased = false)
    {
        lock (_sync)
        {
            if (_disposed) return _worker;
            _discardedGestures += _pending.Count;
            _pending.Clear();
            _inputGesture = null;
            if (pointerReleased) _pointerHeld = false;
            if (_active is not null) _active.Cancelled = true;
            // Keep _pointerHeld until the physical up arrives. This prevents a late
            // move from creating a new down after cancellation or focus loss.
            Signal();
            return _worker;
        }
    }

    public TouchDispatchSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var durations = _rpcDurations.ToArray();
            Array.Sort(durations);
            var p95 = durations.Length == 0 ? 0 : durations[(int)Math.Ceiling(durations.Length * .95) - 1];
            return new TouchDispatchSnapshot(_sentCommands, _coalescedMoves, _discardedGestures,
                _pending.Count + (_active is null ? 0 : 1), _lastRpcMs, p95);
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Gesture gesture;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _active = null;
                    _workerRunning = false;
                    return;
                }
                gesture = _pending.Dequeue();
                if (_disposed || Stopwatch.GetElapsedTime(gesture.QueuedAt) > MaximumQueuedGestureAge)
                {
                    if (ReferenceEquals(_inputGesture, gesture)) _inputGesture = null;
                    _discardedGestures++;
                    continue;
                }
                _active = gesture;
            }

            var sentPoint = gesture.Start;
            var mayBePressed = false;
            try
            {
                mayBePressed = true; // A failed RPC can still have reached Android.
                await SendAsync(sentPoint, 1, _lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    TouchPoint next;
                    bool release;
                    bool move;
                    lock (_sync)
                    {
                        release = gesture.Cancelled || _disposed;
                        next = release ? sentPoint : gesture.Latest;
                        move = next != sentPoint;
                        release |= gesture.Released && !move;
                        if (move) gesture.HasPendingMove = false;
                    }
                    if (release)
                    {
                        await SendAsync(sentPoint, 0, CancellationToken.None).ConfigureAwait(false);
                        mayBePressed = false;
                        break;
                    }
                    if (move)
                    {
                        sentPoint = next;
                        await SendAsync(sentPoint, 1, _lifetime.Token).ConfigureAwait(false);
                    }
                    else
                        await _changed.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException || !_lifetime.IsCancellationRequested)
                    ReportError(error);
                lock (_sync)
                {
                    _discardedGestures += _pending.Count;
                    _pending.Clear();
                    _inputGesture = null;
                }
            }
            finally
            {
                if (mayBePressed)
                {
                    try { await SendAsync(sentPoint, 0, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { ReportError(error); }
                }
                lock (_sync) _active = null;
            }
        }
    }

    private async Task SendAsync(TouchPoint point, int pressure, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try { await _send(point.X, point.Y, pressure, cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                _sentCommands++;
                _lastRpcMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (_rpcDurations.Count == 120) _rpcDurations.Dequeue();
                _rpcDurations.Enqueue(_lastRpcMs);
            }
        }
    }

    private void ReportError(Exception error)
    {
        try { _reportError?.Invoke(error); }
        catch { /* Reporting must not prevent a pointer from being released. */ }
    }

    private void Signal()
    {
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    public void Dispose()
    {
        Task worker;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _inputGesture = null;
            if (_active is not null) _active.Cancelled = true;
            _lifetime.Cancel();
            Signal();
            worker = _worker;
        }
        // The worker never awaits the UI thread. The caller shuts down the
        // emulator only after the final best-effort up has completed.
        worker.GetAwaiter().GetResult();
        _lifetime.Dispose();
        _changed.Dispose();
    }

    private readonly record struct TouchPoint(int X, int Y);

    private sealed class Gesture(TouchPoint start)
    {
        public TouchPoint Start { get; } = start;
        public TouchPoint Latest { get; set; } = start;
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();
        public bool HasPendingMove { get; set; }
        public bool Released { get; set; }
        public bool Cancelled { get; set; }
    }
}

public readonly record struct TouchDispatchSnapshot(long SentCommands, long CoalescedMoves,
    long DiscardedGestures, int PendingGestures, double LastRpcMs, double RpcP95Ms);
