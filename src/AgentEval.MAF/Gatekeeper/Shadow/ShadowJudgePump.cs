// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Threading.Channels;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M4) — the OWNED background pump that runs an <see cref="IShadowJudge"/> off the hot path.
/// A completed run enqueues a snapshot (non-blocking); a single background consumer judges it, and on a
/// compromised verdict ARMS a quarantine flag in the run's session <c>StateBag</c> (read by
/// <see cref="QuarantineGate"/> on a later run) and forwards every verdict to the caller's sink.
/// <para><b>Ownership.</b> This is a real owned object (not a fire-and-forget <c>Task</c>) — create it with a
/// <c>using</c> and pass it to <c>UseAgentEvalShadowJudge</c>; its consumer drains on <see cref="DisposeAsync"/>.
/// The queue is BOUNDED: if it fills (a slow judge under load), new items are dropped and reported via
/// <c>onError</c> — the returning run is NEVER blocked or slowed. Verdicts go to the sink, never the live trace.</para>
/// </summary>
public sealed class ShadowJudgePump : IAsyncDisposable
{
    /// <summary>
    /// The session <c>StateBag</c> key a compromised verdict sets (to the verdict reason — a non-empty value means
    /// quarantined; the StateBag stores reference types, so a reason string doubles as the flag).
    /// <see cref="QuarantineGate"/> reads it.
    /// </summary>
    public const string QuarantineStateKey = "gatekeeper.quarantine";

    private readonly IShadowJudge _judge;
    private readonly Action<ShadowVerdict, ShadowJudgeContext>? _onVerdict;
    private readonly Action<Exception>? _onError;
    private readonly Channel<ShadowJudgeContext> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumer;
    private bool _disposed;

    /// <summary>Creates the pump and starts its background consumer.</summary>
    /// <param name="judge">The (possibly expensive) judge to run off the hot path.</param>
    /// <param name="onVerdict">Sink for EVERY verdict (the output store) — never the live trace. Optional.</param>
    /// <param name="onError">Reports a judge exception or a dropped (queue-full) item. Optional.</param>
    /// <param name="queueCapacity">Bounded queue size; excess items are dropped and reported. Default 128.</param>
    public ShadowJudgePump(
        IShadowJudge judge,
        Action<ShadowVerdict, ShadowJudgeContext>? onVerdict = null,
        Action<Exception>? onError = null,
        int queueCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(judge);
        if (queueCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "queueCapacity must be at least 1.");
        }

        _judge = judge;
        _onVerdict = onVerdict;
        _onError = onError;
        _channel = Channel.CreateBounded<ShadowJudgeContext>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,   // one consumer; TryWrite returns false (does not block) when full
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Enqueue a completed run for shadow judgement. Non-blocking; drops + reports if the queue is full.</summary>
    public void Enqueue(ShadowJudgeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_channel.Writer.TryWrite(context))
        {
            _onError?.Invoke(new InvalidOperationException(
                "Shadow-judge queue is full — dropped a run's verdict. Increase queueCapacity or speed up the judge."));
        }
    }

    /// <summary>Completes the queue and awaits the consumer draining all pending items (deterministic for tests).</summary>
    public async Task CompleteAndDrainAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var context in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var verdict = await _judge.JudgeAsync(context, _cts.Token).ConfigureAwait(false);
                if (verdict.Compromised)
                {
                    ArmQuarantine(context, verdict.Reason);
                }

                _onVerdict?.Invoke(verdict, context);
            }
            catch (Exception ex)
            {
                // A single bad judgement must not kill the pump — log and keep draining.
                _onError?.Invoke(ex);
            }
        }
    }

    private static void ArmQuarantine(ShadowJudgeContext context, string reason)
        => context.Session?.StateBag.SetValue(QuarantineStateKey, string.IsNullOrEmpty(reason) ? "compromised" : reason, JsonSerializerOptions.Default);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            await _consumer.ConfigureAwait(false);   // drain pending items
        }
        catch
        {
            // best effort
        }

        _cts.Cancel();
        _cts.Dispose();
    }
}
