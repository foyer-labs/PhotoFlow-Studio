// SPDX-License-Identifier: Apache-2.0

namespace PhotoFlow.App.Services;

/// <summary>
/// Turns on the GPU (phase 5b, P16): the per-photo analyses share it, a level D pass needs it alone. A pass waits for
/// the analyses already running and holds back the new ones until it ends; then they go on. One pass at a time.
/// </summary>
public sealed class GpuTurns : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _lock = new();
    private int _shared;
    private TaskCompletionSource _idle = Completed();

    /// <summary>A per-photo analysis; waits while a pass holds the GPU.</summary>
    public async Task<IDisposable> EnterSharedAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_shared++ == 0)
                {
                    _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
        finally
        {
            _turn.Release();
        }

        return new Turn(ExitShared);
    }

    /// <summary>The GPU alone: no analysis running, none starting until the turn is disposed.</summary>
    public async Task<IDisposable> EnterExclusiveAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task idle;
            lock (_lock)
            {
                idle = _idle.Task;
            }

            await idle.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _turn.Release();
            throw;
        }

        return new Turn(() => _turn.Release());
    }

    public void Dispose() => _turn.Dispose();

    private void ExitShared()
    {
        lock (_lock)
        {
            if (--_shared == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource Completed()
    {
        var idle = new TaskCompletionSource();
        idle.SetResult();
        return idle;
    }

    private sealed class Turn(Action exit) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                exit();
            }
        }
    }
}
