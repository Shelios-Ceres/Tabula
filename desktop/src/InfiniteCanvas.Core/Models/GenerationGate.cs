namespace InfiniteCanvas.Core.Models;

/// <summary>
/// In-flight image requests per channel, shared by every canvas. A request waits until its weight fits under the channel's limit;
/// waiters are served strictly first come first served, so a 4K request is never starved by small ones.
/// A null limit never waits. Weight is capped at the limit so a 2-slot request still runs on a 1-slot channel.
/// </summary>
public static class GenerationGate
{
    private sealed class Channel
    {
        public int InFlight;
        public int? Limit;
        public readonly LinkedList<Waiter> Queue = new();
    }

    private sealed class Waiter(int weight, Action<int?>? queued)
    {
        public int Weight { get; } = weight;
        public Action<int?>? Queued { get; } = queued;
        public TaskCompletionSource<IDisposable> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }

    private sealed class Slot(Channel channel, int weight) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Release(channel, weight);
        }
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Channel> Channels = [];

    /// <summary>
    /// Wait for a slot. queued reports how many requests are ahead of this one (0 = next), and null once it runs.
    /// Cancelling the token while waiting removes the request from the queue and throws OperationCanceledException.
    /// Disposing the result releases the slot; disposing twice releases once.
    /// </summary>
    public static Task<IDisposable> EnterAsync(string channelId, int? limit, int weight, Action<int?>? queued, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waiter? waiter = null;
        Task<IDisposable>? granted = null;
        Channel channel;
        List<(Waiter Waiter, IDisposable Slot)> ready;
        List<(Action<int?> Callback, int? Ahead)> reports;
        lock (Lock)
        {
            if (!Channels.TryGetValue(channelId, out channel!)) Channels[channelId] = channel = new Channel();
            channel.Limit = limit is { } n ? Math.Max(1, n) : null;
            if (channel.Queue.Count == 0 && Fits(channel, weight))
            {
                channel.InFlight += Taken(channel, weight);
                granted = Task.FromResult<IDisposable>(new Slot(channel, Taken(channel, weight)));
            }
            else channel.Queue.AddLast(waiter = new Waiter(weight, queued));
            // A raised limit may also let earlier waiters through.
            (ready, reports) = Pump(channel);
        }
        Notify(ready, reports);
        if (granted is not null) return granted;
        // Disposed with the waiter once it has its slot, outside the lock: disposing waits for a running callback, which takes the lock.
        waiter!.Registration = ct.Register(() => Cancel(channel, waiter, ct));
        return waiter.Result.Task;
    }

    /// <summary>Weight of one image request: a 4K tier takes 2 slots, everything else 1.</summary>
    public static int Weight(string? tier) => string.Equals(tier, "4K", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private static int Taken(Channel channel, int weight) => channel.Limit is { } limit ? Math.Min(weight, limit) : weight;

    private static bool Fits(Channel channel, int weight) => channel.Limit is not { } limit || channel.InFlight + Math.Min(weight, limit) <= limit;

    private static void Release(Channel channel, int weight)
    {
        List<(Waiter Waiter, IDisposable Slot)> ready;
        List<(Action<int?> Callback, int? Ahead)> reports;
        lock (Lock)
        {
            channel.InFlight -= weight;
            (ready, reports) = Pump(channel);
        }
        Notify(ready, reports);
    }

    private static void Cancel(Channel channel, Waiter waiter, CancellationToken ct)
    {
        List<(Waiter Waiter, IDisposable Slot)> ready;
        List<(Action<int?> Callback, int? Ahead)> reports;
        lock (Lock)
        {
            // Already granted: the caller got its slot and cancels the work itself.
            if (!channel.Queue.Remove(waiter)) return;
            (ready, reports) = Pump(channel);
        }
        waiter.Result.TrySetCanceled(ct);
        Notify(ready, reports);
    }

    /// <summary>From the head of the queue, let in what fits and stop at the first that does not; what each remaining waiter should be told. Call under the lock.</summary>
    private static (List<(Waiter, IDisposable)> Ready, List<(Action<int?>, int?)> Reports) Pump(Channel channel)
    {
        var ready = new List<(Waiter, IDisposable)>();
        while (channel.Queue.First is { } head && Fits(channel, head.Value.Weight))
        {
            channel.Queue.RemoveFirst();
            var taken = Taken(channel, head.Value.Weight);
            channel.InFlight += taken;
            ready.Add((head.Value, new Slot(channel, taken)));
        }
        var reports = new List<(Action<int?>, int?)>();
        var ahead = 0;
        foreach (var waiter in channel.Queue)
            if (waiter.Queued is { } callback) reports.Add((callback, ahead++));
            else ahead++;
        foreach (var (waiter, _) in ready)
            if (waiter.Queued is { } callback) reports.Add((callback, null));
        return (ready, reports);
    }

    /// <summary>Callbacks and completions run outside the lock, on the thread that released or cancelled.</summary>
    private static void Notify(List<(Waiter Waiter, IDisposable Slot)> ready, List<(Action<int?> Callback, int? Ahead)> reports)
    {
        foreach (var (callback, ahead) in reports) callback(ahead);
        foreach (var (waiter, slot) in ready)
        {
            waiter.Registration.Dispose();
            waiter.Result.TrySetResult(slot);
        }
    }
}
