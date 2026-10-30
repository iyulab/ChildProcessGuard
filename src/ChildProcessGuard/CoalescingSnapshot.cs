using System.Runtime.ExceptionServices;

namespace ChildProcessGuard;

/// <summary>
/// Shares one expensive snapshot between callers that ask for it at the same time, without ever
/// handing a caller a snapshot that was started before the caller asked.
/// </summary>
/// <remarks>
/// Snapshots are taken one at a time. A caller that arrives while one is being taken waits for the
/// next one, which every caller arriving in the meantime shares. So <c>N</c> concurrent callers cost
/// about two snapshots instead of <c>N</c>, and each result is as fresh as one taken by the caller
/// itself — which the process-tree walks rely on to never act on a process that exited before the
/// call. The result is shared and must not be modified.
/// </remarks>
internal sealed class CoalescingSnapshot<T>
{
    private readonly Func<T> _take;
    private readonly object _pendingLock = new();
    private readonly object _takeLock = new();
    private Pending? _pending;

    public CoalescingSnapshot(Func<T> take)
    {
        _take = take;
    }

    /// <summary>
    /// Gets a snapshot taken after this call began. Rethrows the snapshot's exception to every
    /// caller that shared it; the next call takes a new snapshot.
    /// </summary>
    public T Get()
    {
        Pending next;
        lock (_pendingLock)
        {
            next = _pending ??= new Pending(this);
        }

        return next.GetValue();
    }

    private T Take(Pending self)
    {
        lock (_takeLock)
        {
            // From here on, new callers start the following snapshot: this one may already be stale for them.
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pending, self))
                    _pending = null;
            }

            return _take();
        }
    }

    /// <summary>
    /// One snapshot that is taken by the first caller to ask for its value while the others wait,
    /// and whose outcome — value or exception — every caller sharing it observes.
    /// </summary>
    /// <remarks>
    /// Stands in for <see cref="Lazy{T}"/>, whose <c>T</c> carries a trimming annotation for its
    /// parameterless-constructor path that this class has no use for.
    /// </remarks>
    private sealed class Pending
    {
        private readonly CoalescingSnapshot<T> _owner;
        private bool _taken;
        private T? _value;
        private ExceptionDispatchInfo? _error;

        public Pending(CoalescingSnapshot<T> owner)
        {
            _owner = owner;
        }

        public T GetValue()
        {
            lock (this)
            {
                if (!_taken)
                {
                    try
                    {
                        _value = _owner.Take(this);
                    }
                    catch (Exception ex)
                    {
                        _error = ExceptionDispatchInfo.Capture(ex);
                    }

                    _taken = true;
                }
            }

            _error?.Throw();
            return _value!;
        }
    }
}
