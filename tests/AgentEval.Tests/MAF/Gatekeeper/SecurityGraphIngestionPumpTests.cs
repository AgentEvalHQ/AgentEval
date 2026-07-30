// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class SecurityGraphIngestionPumpTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key =
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly SecurityGraphNode Agent =
        new(SecurityGraphNodeKind.Agent, "agent-a");
    private static readonly SecurityGraphNode Tool =
        new(SecurityGraphNodeKind.Tool, "tool-a");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "agenteval-security-pump-" + Guid.NewGuid().ToString("N"));
    private int _storeNumber;

    [Fact]
    public async Task TryEnqueue_FullQueueCreatesDurableCoverageGap()
    {
        using var durable = CreateStore();
        using var blocking = new BlockingStore(durable);
        await using var pump = new SecurityGraphIngestionPump(
            blocking,
            queueCapacity: 1);

        Assert.True(pump.TryEnqueue(Request("event-1")));
        await blocking.FirstAppendStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.True(pump.TryEnqueue(Request("event-2")));
        Assert.False(pump.TryEnqueue(Request("event-3")));
        blocking.ReleaseFirstAppend();

        Assert.True(await pump.CompleteAndDrainAsync());

        Assert.Equal(2, pump.AcceptedCount);
        Assert.Equal(1, pump.DroppedCount);
        Assert.Equal(2, pump.AppliedCount);
        Assert.True(pump.DrainCompleted);
        var snapshot = durable.Read(TimeSpan.FromHours(1));
        Assert.Equal(
            SecurityGraphCoverageState.Incomplete,
            snapshot.Coverage);
        Assert.Equal(2, snapshot.Observations.Count);
        var gap = Assert.Single(snapshot.CoverageGaps);
        Assert.Equal("queue_full", gap.ReasonCode);
        Assert.Equal(1, gap.Count);
    }

    [Fact]
    public async Task ConcurrentProducers_AccountForEveryAttempt()
    {
        using var durable = CreateStore();
        using var blocking = new BlockingStore(durable);
        await using var pump = new SecurityGraphIngestionPump(
            blocking,
            queueCapacity: 8);
        Assert.True(pump.TryEnqueue(Request("event-0")));
        await blocking.FirstAppendStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        const int additionalAttempts = 128;
        await Task.WhenAll(
            Enumerable.Range(1, additionalAttempts)
                .Select(index => Task.Run(
                    () => pump.TryEnqueue(Request($"event-{index}")))));
        blocking.ReleaseFirstAppend();
        Assert.True(await pump.CompleteAndDrainAsync());

        Assert.Equal(
            additionalAttempts + 1,
            pump.AcceptedCount + pump.DroppedCount);
        Assert.Equal(pump.AcceptedCount, pump.AppliedCount);
        Assert.Equal(
            pump.DroppedCount,
            durable.Read(TimeSpan.FromHours(1))
                .CoverageGaps
                .Where(gap => gap.ReasonCode == "queue_full")
                .Sum(gap => (long)gap.Count));
    }

    [Fact]
    public async Task Conflict_CreatesCoverageGapWithoutLeakingRequest()
    {
        using var durable = CreateStore();
        await using var pump = new SecurityGraphIngestionPump(durable);
        pump.TryEnqueue(
            Request("event-1", evidenceReference: "evidence:first"));
        pump.TryEnqueue(
            Request("event-1", evidenceReference: "evidence:second"));

        Assert.True(await pump.CompleteAndDrainAsync());

        Assert.Equal(1, pump.AppliedCount);
        Assert.Equal(1, pump.ConflictCount);
        var snapshot = durable.Read(TimeSpan.FromHours(1));
        Assert.Single(snapshot.Observations);
        Assert.Contains(
            snapshot.CoverageGaps,
            gap => gap.ReasonCode == "event_id_conflict");
    }

    [Fact]
    public async Task StoreCapacityRejection_DoesNotCreateDuplicatePumpGap()
    {
        using var durable = CreateStore(
            new JsonFileSecurityGraphStoreOptions
            {
                BootstrapIfMissing = true,
                Retention = TimeSpan.FromHours(2),
                MaxObservations = 1,
            });
        await using var pump = new SecurityGraphIngestionPump(durable);
        pump.TryEnqueue(Request("event-1"));
        pump.TryEnqueue(Request("event-2"));

        Assert.True(await pump.CompleteAndDrainAsync());

        Assert.Equal(1, pump.AppliedCount);
        Assert.Equal(1, pump.RejectedCount);
        var gap = Assert.Single(
            durable.Read(TimeSpan.FromHours(1)).CoverageGaps);
        Assert.Equal("capacity_exceeded", gap.ReasonCode);
        Assert.Equal(1, gap.Count);
    }

    [Fact]
    public async Task ThrowingAppend_RecordsGapReportsContentFreeFailureAndContinues()
    {
        const string secret = "raw-secret-session";
        using var store = new ThrowOnceStore();
        var failures = new List<SecurityGraphPumpFailure>();
        await using var pump = new SecurityGraphIngestionPump(
            store,
            failure => failures.Add(failure));
        pump.TryEnqueue(Request("event-1", secret));
        pump.TryEnqueue(Request("event-2", "session-b"));

        Assert.True(await pump.CompleteAndDrainAsync());

        Assert.Equal(1, pump.AppliedCount);
        Assert.Contains(
            store.Gaps,
            gap => gap.ReasonCode == "ingestion_failed");
        var failure = Assert.Single(
            failures,
            value => value.Kind == SecurityGraphPumpFailureKind.Append);
        Assert.Equal("ingestion_failed", failure.ReasonCode);
        Assert.DoesNotContain(
            secret,
            failure.ReasonCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndeterminateStore_IsSurfacedAndNotConvertedToSuccess()
    {
        using var store = new IndeterminateStore();
        var failures = new List<SecurityGraphPumpFailure>();
        await using var pump = new SecurityGraphIngestionPump(
            store,
            failure => failures.Add(failure));
        pump.TryEnqueue(Request("event-1"));

        Assert.True(await pump.CompleteAndDrainAsync());

        Assert.Equal(1, pump.IndeterminateCount);
        Assert.Equal(0, pump.AppliedCount);
        var failure = Assert.Single(failures);
        Assert.Equal(
            SecurityGraphPumpFailureKind.Append,
            failure.Kind);
        Assert.Equal("store_unavailable", failure.ReasonCode);
    }

    [Fact]
    public async Task FailureSinkException_DoesNotKillConsumer()
    {
        using var store = new ThrowOnceStore();
        await using var pump = new SecurityGraphIngestionPump(
            store,
            _ => throw new InvalidOperationException("sink failed"));
        pump.TryEnqueue(Request("event-1"));
        pump.TryEnqueue(Request("event-2"));

        Assert.True(await pump.CompleteAndDrainAsync());
        Assert.Equal(1, pump.AppliedCount);
    }

    [Fact]
    public async Task CompleteAndDrain_IsIdempotentAndClosesProducer()
    {
        using var durable = CreateStore();
        await using var pump = new SecurityGraphIngestionPump(durable);
        pump.TryEnqueue(Request("event-1"));

        var first = pump.CompleteAndDrainAsync();
        var second = pump.CompleteAndDrainAsync();

        Assert.Same(first, second);
        Assert.True(await first);
        Assert.Throws<InvalidOperationException>(
            () => pump.TryEnqueue(Request("event-2")));
    }

    [Fact]
    public async Task HungStore_DrainIsBoundedAndOutcomeIsExplicit()
    {
        using var store = new HangingStore();
        var failures = new List<SecurityGraphPumpFailure>();
        await using var pump = new SecurityGraphIngestionPump(
            store,
            failure => failures.Add(failure),
            drainTimeout: TimeSpan.FromMilliseconds(25));
        pump.TryEnqueue(Request("event-1"));
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drained = await pump.CompleteAndDrainAsync();

        Assert.False(drained);
        Assert.False(pump.DrainCompleted);
        Assert.Contains(
            failures,
            failure =>
                failure.Kind == SecurityGraphPumpFailureKind.Lifecycle &&
                failure.ReasonCode == "drain_timeout");
    }

    [Fact]
    public async Task Consumer_IsStrictlySerialAndPreservesFifoOrder()
    {
        using var store = new RecordingStore();
        await using var pump = new SecurityGraphIngestionPump(store);
        pump.TryEnqueue(Request("event-1"));
        pump.TryEnqueue(Request("event-2"));
        pump.TryEnqueue(Request("event-3"));

        Assert.True(await pump.CompleteAndDrainAsync());

        Assert.Equal(
            ["event-1", "event-2", "event-3"],
            store.EventIds);
        Assert.Equal(1, store.MaximumConcurrency);
    }

    [Fact]
    public async Task DurableIngestion_PreservesStableSameSessionDigest()
    {
        using var durable = CreateStore();
        await using var pump = new SecurityGraphIngestionPump(durable);
        pump.TryEnqueue(Request("event-1", "same-session"));
        pump.TryEnqueue(Request("event-2", "same-session"));

        Assert.True(await pump.CompleteAndDrainAsync());

        var observations = durable.Read(TimeSpan.FromHours(1)).Observations;
        Assert.Equal(2, observations.Count);
        Assert.Equal(
            observations[0].SessionDigest,
            observations[1].SessionDigest);
    }
    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        using var durable = CreateStore();
        var pump = new SecurityGraphIngestionPump(durable);
        pump.TryEnqueue(Request("event-1"));

        await pump.DisposeAsync();
        await pump.DisposeAsync();

        Assert.True(pump.DrainCompleted);
        Assert.Single(
            durable.Read(TimeSpan.FromHours(1)).Observations);
    }

    [Fact]
    public void Constructor_RejectsUnboundedQueueAndDrainOptions()
    {
        using var durable = CreateStore();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SecurityGraphIngestionPump(
                durable,
                queueCapacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SecurityGraphIngestionPump(
                durable,
                queueCapacity: 65_537));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SecurityGraphIngestionPump(
                durable,
                drainTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SecurityGraphIngestionPump(
                durable,
                drainTimeout: TimeSpan.FromMinutes(6)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonFileSecurityGraphStore CreateStore(
        JsonFileSecurityGraphStoreOptions? options = null)
        => new(
            Path.Combine(
                _directory,
                $"graph-{Interlocked.Increment(ref _storeNumber)}.json"),
            "tenant-a",
            "key-a",
            Key,
            options ?? new JsonFileSecurityGraphStoreOptions
            {
                BootstrapIfMissing = true,
                Retention = TimeSpan.FromHours(2),
            },
            new FixedClock(Now));

    private static SecurityGraphObservationRequest Request(
        string eventId,
        string sessionIdentifier = "session-a",
        string? evidenceReference = "evidence:incident-1")
        => new(
            eventId,
            Agent,
            Tool,
            SecurityGraphSignalKind.CallBlocked,
            sessionIdentifier,
            evidenceReference);

    private sealed class RecordingStore : ISecurityGraphStore
    {
        private readonly List<string> _eventIds = [];
        private int _active;
        private int _maximumConcurrency;

        public IReadOnlyList<string> EventIds => _eventIds;

        public int MaximumConcurrency => Volatile.Read(
            ref _maximumConcurrency);

        public SecurityGraphTenantSnapshot Read(TimeSpan window)
            => SecurityGraphTenantSnapshot.Determinate(
                "tenant-a",
                window,
                Now,
                observations: []);

        public async ValueTask<SecurityGraphMutationResult> AppendAsync(
            SecurityGraphObservationRequest observation,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                await Task.Yield();
                _eventIds.Add(observation.EventId);
                return new SecurityGraphMutationResult(
                    SecurityGraphMutationDisposition.Applied,
                    "applied");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask<SecurityGraphMutationResult> MarkCoverageGapAsync(
            SecurityGraphCoverageGap gap,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                new SecurityGraphMutationResult(
                    SecurityGraphMutationDisposition.Applied,
                    "gap_recorded"));

        public void Dispose()
        {
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (candidate <= current ||
                    Interlocked.CompareExchange(
                        ref _maximumConcurrency,
                        candidate,
                        current) == current)
                {
                    return;
                }
            }
        }
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingStore(ISecurityGraphStore inner)
        : ISecurityGraphStore
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _appendCount;

        public TaskCompletionSource FirstAppendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SecurityGraphTenantSnapshot Read(TimeSpan window)
            => inner.Read(window);

        public async ValueTask<SecurityGraphMutationResult> AppendAsync(
            SecurityGraphObservationRequest observation,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _appendCount) == 1)
            {
                FirstAppendStarted.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await inner.AppendAsync(
                observation,
                cancellationToken);
        }

        public ValueTask<SecurityGraphMutationResult> MarkCoverageGapAsync(
            SecurityGraphCoverageGap gap,
            CancellationToken cancellationToken = default)
            => inner.MarkCoverageGapAsync(gap, cancellationToken);

        public void ReleaseFirstAppend() => _release.TrySetResult();

        public void Dispose()
        {
            _release.TrySetResult();
        }
    }

    private sealed class ThrowOnceStore : ISecurityGraphStore
    {
        private int _appendCount;

        public List<SecurityGraphCoverageGap> Gaps { get; } = [];

        public SecurityGraphTenantSnapshot Read(TimeSpan window)
            => SecurityGraphTenantSnapshot.Determinate(
                "tenant-a",
                window,
                Now,
                observations: [],
                Gaps.Select(gap =>
                    SecurityGraphCoverageGap.Accepted(
                        Now,
                        gap.ReasonCode,
                        gap.Count)));

        public ValueTask<SecurityGraphMutationResult> AppendAsync(
            SecurityGraphObservationRequest observation,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _appendCount) == 1)
            {
                throw new InvalidOperationException(
                    $"simulated failure {observation.SessionIdentifier}");
            }

            return ValueTask.FromResult(
                new SecurityGraphMutationResult(
                    SecurityGraphMutationDisposition.Applied,
                    "applied"));
        }

        public ValueTask<SecurityGraphMutationResult> MarkCoverageGapAsync(
            SecurityGraphCoverageGap gap,
            CancellationToken cancellationToken = default)
        {
            Gaps.Add(gap);
            return ValueTask.FromResult(
                new SecurityGraphMutationResult(
                    SecurityGraphMutationDisposition.Applied,
                    "gap_recorded"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class IndeterminateStore : ISecurityGraphStore
    {
        public SecurityGraphTenantSnapshot Read(TimeSpan window)
            => SecurityGraphTenantSnapshot.Indeterminate(
                "tenant-a",
                window,
                Now,
                "store_unavailable");

        public ValueTask<SecurityGraphMutationResult> AppendAsync(
            SecurityGraphObservationRequest observation,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                new SecurityGraphMutationResult(
                    SecurityGraphMutationDisposition.Indeterminate,
                    "store_unavailable"));

        public ValueTask<SecurityGraphMutationResult> MarkCoverageGapAsync(
            SecurityGraphCoverageGap gap,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                new SecurityGraphMutationResult(
                    SecurityGraphMutationDisposition.Indeterminate,
                    "store_unavailable"));

        public void Dispose()
        {
        }
    }

    private sealed class HangingStore : ISecurityGraphStore
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SecurityGraphTenantSnapshot Read(TimeSpan window)
            => SecurityGraphTenantSnapshot.Indeterminate(
                "tenant-a",
                window,
                Now,
                "store_unavailable");

        public async ValueTask<SecurityGraphMutationResult> AppendAsync(
            SecurityGraphObservationRequest observation,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return new SecurityGraphMutationResult(
                SecurityGraphMutationDisposition.Indeterminate,
                "unreachable");
        }

        public ValueTask<SecurityGraphMutationResult> MarkCoverageGapAsync(
            SecurityGraphCoverageGap gap,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                new SecurityGraphMutationResult(
                    SecurityGraphMutationDisposition.Applied,
                    "gap_recorded"));

        public void Dispose()
        {
        }
    }
}
