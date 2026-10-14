// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AgentEval.MissionControl.Core;

/// <summary>Reasons to refresh authoritative queries; these are not evidence or evaluation verdicts.</summary>
public enum EvidenceChangeReason { Connected, Changed, Overflow, Reconcile }
/// <summary>A process-local invalidation. Epoch and sequence do not identify an evidence snapshot.</summary>
public sealed record EvidenceChange(string Epoch, long Sequence, EvidenceChangeReason Reason);
/// <summary>A bounded, single-consumer subscription. Dispose it to release its subscriber slot.</summary>
public interface IEvidenceChangeSubscription : IDisposable
{
    /// <summary>Reads notifications until cancellation, subscription disposal or feed shutdown.</summary>
    IAsyncEnumerable<EvidenceChange> ReadAllAsync(CancellationToken cancellationToken = default);
}
/// <summary>Shared notification boundary for desktop consumers and streaming transports.</summary>
public interface IEvidenceChangeFeed
{
    /// <summary>Returns null at the subscriber limit. Every accepted connection starts with a resync notification.</summary>
    IEvidenceChangeSubscription? Subscribe();
}
/// <summary>Host-owned invalidation producer; consumers must query again rather than treating hints as facts.</summary>
public interface IEvidenceChangePublisher
{
    /// <summary>Publishes a change or periodic reconciliation hint without waiting for slow subscribers.</summary>
    void Invalidate(EvidenceChangeReason reason = EvidenceChangeReason.Changed);
}
/// <summary>Finite fan-out with explicit overflow resync, no replay log and no transport dependencies.</summary>
public sealed class EvidenceChangeFeed : IEvidenceChangeFeed, IEvidenceChangePublisher, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private readonly int _capacity;
    private readonly int _maximumSubscribers;
    private string _epoch = Guid.NewGuid().ToString("N");
    private long _sequence;
    private bool _disposed;

    /// <summary>Sets finite limits: 1..256 subscribers and 1..1024 buffered notifications per subscriber.</summary>
    public EvidenceChangeFeed(int maximumSubscribers = 32, int capacity = 16)
    {
        if (maximumSubscribers is < 1 or > 256 || capacity is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumSubscribers), "Expected: subscribers 1..256 and capacity 1..1024. Actual: invalid feed limits. Suggestions: configure finite limits in range. Because: slow consumers must not grow memory without bound.");
        _maximumSubscribers = maximumSubscribers; _capacity = capacity;
    }

    /// <inheritdoc />
    public IEvidenceChangeSubscription? Subscribe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscriptions.Count == _maximumSubscribers) return null;
            var subscription = new Subscription(this, _capacity);
            _subscriptions.Add(subscription);
            subscription.Channel.Writer.TryWrite(new(_epoch, _sequence, EvidenceChangeReason.Connected));
            return subscription;
        }
    }

    /// <inheritdoc />
    public void Invalidate(EvidenceChangeReason reason = EvidenceChangeReason.Changed)
    {
        if (reason is not (EvidenceChangeReason.Changed or EvidenceChangeReason.Reconcile))
            throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sequence == long.MaxValue) { _epoch = Guid.NewGuid().ToString("N"); _sequence = 0; reason = EvidenceChangeReason.Reconcile; }
            var change = new EvidenceChange(_epoch, ++_sequence, reason);
            foreach (var subscription in _subscriptions)
            {
                if (subscription.Channel.Writer.TryWrite(change)) continue;
                // Only the producer writes, and disposal shares this lock. Consumers can only free capacity.
                while (subscription.Channel.Reader.TryRead(out _)) { }
                subscription.Channel.Writer.TryWrite(change with { Reason = EvidenceChangeReason.Overflow });
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
            subscription.Close();
        }
    }

    /// <summary>Stops all subscribers and releases queued notifications.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var subscription in _subscriptions) subscription.Close();
            _subscriptions.Clear();
        }
    }

    private sealed class Subscription(EvidenceChangeFeed owner, int capacity) : IEvidenceChangeSubscription
    {
        internal Channel<EvidenceChange> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<EvidenceChange>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false,
                AllowSynchronousContinuations = false });
        private int _reading;
        public async IAsyncEnumerable<EvidenceChange> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _reading, 1) != 0)
                throw new InvalidOperationException("Expected: one reader per subscription. Actual: repeated read. Suggestions: create another subscription. Because: competing readers would lose notifications.");
            try
            {
                await foreach (var change in Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    yield return change;
            }
            finally { Dispose(); }
        }
        public void Dispose() => owner.Remove(this);
        internal void Close()
        {
            Channel.Writer.TryComplete();
            while (Channel.Reader.TryRead(out _)) { }
        }
    }
}
