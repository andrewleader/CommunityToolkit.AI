using System.Runtime.InteropServices;

namespace CommunityToolkit.AI.Host;

/// <summary>
/// Per-model fair queue. Requests run one at a time in arrival order, except that requests from the app that owns
/// the foreground window jump ahead of background apps.
/// </summary>
internal sealed class FairScheduler
{
    private readonly object _lock = new();
    private readonly SortedSet<Waiter> _waiters = new(Comparer<Waiter>.Create((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Sequence.CompareTo(b.Sequence)));
    private long _sequence;
    private bool _busy;

    public int Pending { get { lock (_lock) { return _waiters.Count + (_busy ? 1 : 0); } } }

    public async Task<IDisposable> EnterAsync(int clientProcessId, CancellationToken ct)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (!_busy)
            {
                _busy = true;
                return new Releaser(this);
            }

            waiter = new Waiter(IsForeground(clientProcessId) ? 0 : 1, ++_sequence);
            _waiters.Add(waiter);
        }

        using (ct.Register(() =>
        {
            lock (_lock)
            {
                if (_waiters.Remove(waiter))
                {
                    waiter.Completion.TrySetCanceled(ct);
                }
            }
        }))
        {
            await waiter.Completion.Task.ConfigureAwait(false);
        }

        return new Releaser(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            if (_waiters.Count == 0)
            {
                _busy = false;
                return;
            }

            var next = _waiters.Min!;
            _waiters.Remove(next);
            next.Completion.TrySetResult();
        }
    }

    private static bool IsForeground(int processId)
    {
        IntPtr window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint pid) != 0 && pid == (uint)processId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private sealed class Waiter(int priority, long sequence)
    {
        public int Priority { get; } = priority;

        public long Sequence { get; } = sequence;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Releaser(FairScheduler owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
