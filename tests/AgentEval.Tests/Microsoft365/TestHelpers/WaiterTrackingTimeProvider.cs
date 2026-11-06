// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.Time.Testing;

namespace AgentEval.Tests.Microsoft365.TestHelpers;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that knows whether anything is waiting on it. Every timer it hands out
/// (<c>Task.Delay(…, clock, …)</c>, <c>new CancellationTokenSource(timeout, clock)</c>, a raw
/// <see cref="TimeProvider.CreateTimer"/>) is tracked from the moment it is armed until it fires, is re-armed with an
/// infinite due time, or is disposed. A clock pump can then advance the clock only while the code under test is
/// actually sleeping on it, and never run ahead of a continuation that is still travelling through the thread pool.
/// </summary>
internal sealed class WaiterTrackingTimeProvider : FakeTimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<TrackedTimer> _armed = [];

    public WaiterTrackingTimeProvider(DateTimeOffset startDateTime)
        : base(startDateTime)
    {
    }

    /// <summary>The number of timers waiting to fire.</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_gate)
            {
                return _armed.Count;
            }
        }
    }

    /// <summary>The fake time at which the earliest armed timer fires, or <see langword="null"/> when nothing waits on the clock.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset? next = null;
                foreach (var timer in _armed)
                {
                    if (next is null || timer.Due < next.Value)
                    {
                        next = timer.Due;
                    }
                }

                return next;
            }
        }
    }

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var tracked = new TrackedTimer(this, callback);

        // Register before the base timer exists: a zero due time fires synchronously inside the base call.
        tracked.Schedule(dueTime, period);
        try
        {
            tracked.Attach(base.CreateTimer(tracked.Fire, state, dueTime, period));
        }
        catch
        {
            tracked.Forget();
            throw;
        }

        return tracked;
    }

    private void Arm(TrackedTimer timer)
    {
        lock (_gate)
        {
            _armed.Add(timer);
        }
    }

    private void Disarm(TrackedTimer timer)
    {
        lock (_gate)
        {
            _armed.Remove(timer);
        }
    }

    /// <summary>
    /// The timer handed to the caller: bookkeeping around the base timer. The gate is never held while the base
    /// timer is called, so a callback firing on another thread cannot deadlock against a concurrent change or dispose.
    /// </summary>
    private sealed class TrackedTimer : ITimer
    {
        private readonly WaiterTrackingTimeProvider _owner;
        private readonly TimerCallback _callback;
        private ITimer? _inner;
        private TimeSpan _period;
        private volatile bool _disposed;

        internal TrackedTimer(WaiterTrackingTimeProvider owner, TimerCallback callback)
        {
            _owner = owner;
            _callback = callback;
        }

        /// <summary>The fake time at which the timer fires next (meaningful only while armed).</summary>
        internal DateTimeOffset Due { get; private set; }

        internal void Attach(ITimer inner) => _inner = inner;

        internal void Forget() => _owner.Disarm(this);

        /// <summary>Records the next due time and the period; arms the timer unless the due time is infinite.</summary>
        internal void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return;
            }

            // A period of zero or Timeout.InfiniteTimeSpan is a one-shot timer, as for System.Threading.Timer.
            _period = period > TimeSpan.Zero ? period : TimeSpan.Zero;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                _owner.Disarm(this);
                return;
            }

            Due = _owner.GetUtcNow() + dueTime;
            _owner.Arm(this);
        }

        /// <summary>The base timer's callback: a one-shot timer is disarmed before the caller's callback runs; a periodic one moves on by its period.</summary>
        internal void Fire(object? state)
        {
            if (_period > TimeSpan.Zero)
            {
                lock (_owner._gate)
                {
                    Due += _period;
                }
            }
            else
            {
                _owner.Disarm(this);
            }

            _callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime, period);
            return _inner?.Change(dueTime, period) ?? false;
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.Disarm(this);
            _inner?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
