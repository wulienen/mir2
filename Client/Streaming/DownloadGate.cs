namespace Client.Streaming;

/// <summary>
/// Concurrency limiter for streaming HTTP requests with two lanes.
///
/// A single fair semaphore was not enough. Entering a map queues hundreds of image spans, and the one
/// request that actually gates the game - the map pack, without which <c>GameScene.IsCellLoaded</c> keeps
/// refusing to let the player walk - was admitted in arrival order behind all of them. On a loopback that
/// is invisible; on a real line with per-request latency it turns into a stall of seconds while the pack
/// waits for a permit it could have had immediately.
///
/// So permits are handed out by lane instead of by arrival: waiters in the priority lane are always served
/// first, and bulk traffic can never hold more than <see cref="_bulkLimit"/> permits, which keeps a few
/// reserved for latency-critical requests even while the bulk lane is saturated. Total in-flight requests
/// stay bounded by the configured concurrency exactly as before.
/// </summary>
public sealed class DownloadGate
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource<bool>> _priorityWaiters = new();
    private readonly Queue<TaskCompletionSource<bool>> _bulkWaiters = new();
    private readonly int _permits;
    private readonly int _bulkLimit;
    private int _held;
    private int _bulkHeld;

    public DownloadGate(int permits)
    {
        _permits = Math.Max(1, permits);
        // A quarter of the budget, at least one permit, is off limits to bulk traffic. With one permit
        // total there is nothing to reserve and the gate degrades to a plain mutex.
        _bulkLimit = Math.Max(1, _permits - Math.Max(1, _permits / 4));
    }

    public int Permits => _permits;
    public int BulkLimit => _bulkLimit;

    /// <summary>
    /// Acquires one permit. Completes synchronously when the lane has room, otherwise queues. Priority
    /// waiters jump ahead of every bulk waiter regardless of arrival order.
    /// </summary>
    public Task WaitAsync(bool priority)
    {
        lock (_sync)
        {
            if (CanEnter(priority))
            {
                Take(priority);
                return Task.CompletedTask;
            }

            TaskCompletionSource<bool> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            (priority ? _priorityWaiters : _bulkWaiters).Enqueue(waiter);
            return waiter.Task;
        }
    }

    /// <summary>
    /// Returns a permit. Must be called with the same lane the permit was taken in, or the bulk limit
    /// drifts; every call site pairs the two in a <c>finally</c>.
    /// </summary>
    public void Release(bool priority)
    {
        lock (_sync)
        {
            if (_held > 0) _held--;
            if (!priority && _bulkHeld > 0) _bulkHeld--;

            // One release can admit more than one waiter: a bulk permit coming back may be claimed by a
            // priority waiter, which then leaves the bulk lane under its own limit again.
            while (true)
            {
                if (_priorityWaiters.Count > 0 && CanEnter(true))
                {
                    Take(true);
                    // RunContinuationsAsynchronously keeps the woken continuation off this thread, so
                    // completing inside the lock cannot run foreign code while holding it.
                    _priorityWaiters.Dequeue().SetResult(true);
                    continue;
                }
                if (_bulkWaiters.Count > 0 && CanEnter(false))
                {
                    Take(false);
                    _bulkWaiters.Dequeue().SetResult(true);
                    continue;
                }
                break;
            }
        }
    }

    private bool CanEnter(bool priority) => _held < _permits && (priority || _bulkHeld < _bulkLimit);

    private void Take(bool priority)
    {
        _held++;
        if (!priority) _bulkHeld++;
    }
}
