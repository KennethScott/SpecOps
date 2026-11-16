namespace SpecOps.Core.Execution;

/// <summary>
/// First-in, first-out admission gate for runs. The limit is read on every admission, so changing
/// MaxConcurrentRuns in configuration takes effect immediately (call <see cref="Pump"/> after a change).
/// </summary>
internal sealed class RunGate(Func<int> limit)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = [];
    private int _active;

    private int Limit => Math.Max(1, limit());

    /// <summary>True when a new run would start immediately.</summary>
    public bool HasFreeSlot
    {
        get
        {
            lock (_lock)
            {
                return _waiting.Count == 0 && _active < Limit;
            }
        }
    }

    public int Active
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>Waits for a slot. Dispose the result to give the slot back.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_waiting.Count == 0 && _active < Limit)
            {
                _active++;
                return new Slot(this);
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiting.AddLast(waiter);
        }

        await using (
            cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    // Only cancel if we're still queued; if we were just admitted, the slot is ours.
                    if (node.List is not null)
                    {
                        _waiting.Remove(node);
                        waiter.TrySetCanceled(cancellationToken);
                    }
                }
            })
        )
        {
            await waiter.Task.ConfigureAwait(false);
        }

        return new Slot(this);
    }

    /// <summary>Admits queued runs while there's room (e.g. after the limit was raised).</summary>
    public void Pump()
    {
        lock (_lock)
        {
            while (_waiting.First is { } next && _active < Limit)
            {
                _waiting.RemoveFirst();
                _active++;
                next.Value.TrySetResult();
            }
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            _active--;
        }

        Pump();
    }

    private sealed class Slot(RunGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
