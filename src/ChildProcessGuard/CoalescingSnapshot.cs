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
    private Lazy<T>? _pending;

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
        Lazy<T> next;
        lock (_pendingLock)
        {
            if (_pending == null)
            {
                Lazy<T>? created = null;
                created = new Lazy<T>(() => Take(created!), LazyThreadSafetyMode.ExecutionAndPublication);
                _pending = created;
            }

            next = _pending;
        }

        return next.Value;
    }

    private T Take(Lazy<T> self)
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
}
