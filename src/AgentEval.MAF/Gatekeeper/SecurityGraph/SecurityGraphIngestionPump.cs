// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Threading.Channels;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Closed category for a content-free security-graph ingestion failure.</summary>
public enum SecurityGraphPumpFailureKind
{
    /// <summary>An observation append threw or returned an indeterminate outcome.</summary>
    Append,

    /// <summary>A required durable coverage-gap write threw or returned an indeterminate outcome.</summary>
    CoverageGap,

    /// <summary>The bounded drain timed out or the consumer ended unexpectedly.</summary>
    Lifecycle,
}

/// <summary>Content-free background ingestion failure; no observation payload is retained.</summary>
public sealed class SecurityGraphPumpFailure
{
    /// <summary>Creates a validated failure notification.</summary>
    public SecurityGraphPumpFailure(
        SecurityGraphPumpFailureKind kind,
        string reasonCode)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ReasonCode = ContainmentValidation.Token(
            reasonCode,
            nameof(reasonCode),
            ContainmentValidation.MaxReasonCodeLength);
    }

    /// <summary>The closed failure category.</summary>
    public SecurityGraphPumpFailureKind Kind { get; }

    /// <summary>A bounded content-free reason.</summary>
    public string ReasonCode { get; }
}

/// <summary>
/// Caller-owned bounded background writer for <see cref="ISecurityGraphStore"/>. Producers never wait for
/// storage I/O; one serial consumer preserves store ordering and durably marks queue drops as coverage gaps.
/// The caller must dispose this pump before disposing the caller-owned store.
/// </summary>
public sealed class SecurityGraphIngestionPump : IAsyncDisposable
{
    private const int DefaultQueueCapacity = 128;
    private const int MaximumQueueCapacity = 65_536;
    private static readonly TimeSpan DefaultDrainTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumDrainTimeout =
        TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaximumDrainTimeout =
        TimeSpan.FromMinutes(5);

    private readonly ISecurityGraphStore _store;
    private readonly Action<SecurityGraphPumpFailure>? _onFailure;
    private readonly Channel<SecurityGraphObservationRequest> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private readonly TimeSpan _drainTimeout;
    private readonly object _writerState = new();
    private Task<bool>? _drainTask;
    private bool _completed;
    private int _disposed;
    private int _drainCompleted;
    private long _accepted;
    private long _dropped;
    private long _applied;
    private long _unchanged;
    private long _conflicts;
    private long _rejected;
    private long _indeterminate;
    private long _pendingQueueDrops;

    /// <summary>Creates and starts a bounded single-consumer graph ingestion pump.</summary>
    public SecurityGraphIngestionPump(
        ISecurityGraphStore store,
        Action<SecurityGraphPumpFailure>? onFailure = null,
        int queueCapacity = DefaultQueueCapacity,
        TimeSpan? drainTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (queueCapacity is < 1 or > MaximumQueueCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }

        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        if (_drainTimeout < MinimumDrainTimeout ||
            _drainTimeout > MaximumDrainTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        }

        _onFailure = onFailure;
        _channel = Channel.CreateBounded<SecurityGraphObservationRequest>(
            new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Items accepted into the bounded queue.</summary>
    public long AcceptedCount => Interlocked.Read(ref _accepted);

    /// <summary>Items rejected because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Durable applied appends.</summary>
    public long AppliedCount => Interlocked.Read(ref _applied);

    /// <summary>Idempotent unchanged appends.</summary>
    public long UnchangedCount => Interlocked.Read(ref _unchanged);

    /// <summary>Conflicting event IDs rejected and converted to coverage gaps.</summary>
    public long ConflictCount => Interlocked.Read(ref _conflicts);

    /// <summary>Store-capacity rejections whose durable gap was recorded by the store.</summary>
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>Appends or gap writes whose durable outcome was indeterminate.</summary>
    public long IndeterminateCount => Interlocked.Read(ref _indeterminate);

    /// <summary>True only when the consumer completed within the bounded drain.</summary>
    public bool DrainCompleted => Volatile.Read(ref _drainCompleted) != 0;

    /// <summary>
    /// Attempts a non-blocking enqueue. A false result means the queue was full and a durable
    /// <c>queue_full</c> coverage gap has been scheduled.
    /// </summary>
    public bool TryEnqueue(SecurityGraphObservationRequest observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_writerState)
        {
            if (_completed || Volatile.Read(ref _disposed) != 0)
            {
                throw new InvalidOperationException(
                    "Security graph ingestion pump is completed.");
            }

            if (_channel.Writer.TryWrite(observation))
            {
                SaturatingIncrement(ref _accepted);
                return true;
            }

            SaturatingIncrement(ref _dropped);
            SaturatingIncrement(ref _pendingQueueDrops);
            return false;
        }
    }

    /// <summary>Completes the writer and returns whether every accepted item drained within the configured bound.</summary>
    public Task<bool> CompleteAndDrainAsync()
    {
        lock (_writerState)
        {
            if (_drainTask is not null)
            {
                return _drainTask;
            }

            _completed = true;
            _channel.Writer.TryComplete();
            _drainTask = DrainCoreAsync();
            return _drainTask;
        }
    }

    private async Task ConsumeAsync()
    {
        var token = _cts.Token;
        await foreach (var observation in
            _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            await FlushPendingQueueDropsAsync(token).ConfigureAwait(false);
            await ProcessAsync(observation, token).ConfigureAwait(false);
        }

        await FlushPendingQueueDropsAsync(token).ConfigureAwait(false);
    }

    private async Task ProcessAsync(
        SecurityGraphObservationRequest observation,
        CancellationToken cancellationToken)
    {
        SecurityGraphMutationResult result;
        try
        {
            result = await _store.AppendAsync(
                observation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Report(
                SecurityGraphPumpFailureKind.Append,
                "ingestion_failed");
            await RecordGapAsync(
                "ingestion_failed",
                count: 1,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (result.Disposition)
        {
            case SecurityGraphMutationDisposition.Applied:
                SaturatingIncrement(ref _applied);
                break;
            case SecurityGraphMutationDisposition.Unchanged:
                SaturatingIncrement(ref _unchanged);
                break;
            case SecurityGraphMutationDisposition.Conflict:
                SaturatingIncrement(ref _conflicts);
                await RecordGapAsync(
                    "event_id_conflict",
                    count: 1,
                    cancellationToken).ConfigureAwait(false);
                break;
            case SecurityGraphMutationDisposition.RejectedWithGap:
                SaturatingIncrement(ref _rejected);
                break;
            case SecurityGraphMutationDisposition.Indeterminate:
                SaturatingIncrement(ref _indeterminate);
                Report(
                    SecurityGraphPumpFailureKind.Append,
                    result.ReasonCode);
                break;
            default:
                SaturatingIncrement(ref _indeterminate);
                Report(
                    SecurityGraphPumpFailureKind.Append,
                    "unknown_disposition");
                break;
        }
    }

    private async Task FlushPendingQueueDropsAsync(
        CancellationToken cancellationToken)
    {
        var dropped = Interlocked.Exchange(
            ref _pendingQueueDrops,
            0);
        while (dropped > 0)
        {
            var chunk = dropped > int.MaxValue
                ? int.MaxValue
                : (int)dropped;
            await RecordGapAsync(
                "queue_full",
                chunk,
                cancellationToken).ConfigureAwait(false);
            dropped -= chunk;
        }
    }

    private async Task RecordGapAsync(
        string reasonCode,
        int count,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.MarkCoverageGapAsync(
                new SecurityGraphCoverageGap(reasonCode, count),
                cancellationToken).ConfigureAwait(false);
            if (result.Disposition is
                SecurityGraphMutationDisposition.Applied or
                SecurityGraphMutationDisposition.Unchanged)
            {
                return;
            }

            SaturatingIncrement(ref _indeterminate);
            Report(
                SecurityGraphPumpFailureKind.CoverageGap,
                result.ReasonCode);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            SaturatingIncrement(ref _indeterminate);
            Report(
                SecurityGraphPumpFailureKind.CoverageGap,
                "gap_write_failed");
        }
    }

    private async Task<bool> DrainCoreAsync()
    {
        try
        {
            await _consumer.WaitAsync(_drainTimeout).ConfigureAwait(false);
            Volatile.Write(ref _drainCompleted, 1);
            return true;
        }
        catch (TimeoutException)
        {
            Report(
                SecurityGraphPumpFailureKind.Lifecycle,
                "drain_timeout");
            _cts.Cancel();
            try
            {
                await _consumer.WaitAsync(_drainTimeout).ConfigureAwait(false);
            }
            catch
            {
                // A store that ignores cancellation cannot be force-stopped.
            }

            return false;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Report(
                SecurityGraphPumpFailureKind.Lifecycle,
                "drain_cancelled");
            return false;
        }
        catch
        {
            Report(
                SecurityGraphPumpFailureKind.Lifecycle,
                "consumer_failed");
            return false;
        }
    }

    private void Report(
        SecurityGraphPumpFailureKind kind,
        string reasonCode)
    {
        try
        {
            _onFailure?.Invoke(
                new SecurityGraphPumpFailure(kind, reasonCode));
        }
        catch
        {
            // A caller failure sink cannot kill ingestion.
        }
    }

    private static void SaturatingIncrement(ref long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref value);
            if (current == long.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                ref value,
                current + 1,
                current) == current)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CompleteAndDrainAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
