// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Fail-closed per-session rate limit: blocks once more than <c>maxRuns</c> runs occur within a
/// <b>fixed/tumbling</b> <c>window</c> for a session. (A fixed window can admit up to ~2× <c>maxRuns</c>
/// across a boundary; use a small window if that matters.)
/// <para><b>SEC-04:</b> the authoritative counter is kept in-process (keyed by session identity via a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>, guarded by a per-session lock) rather than in the session
/// <c>StateBag</c> — the StateBag has no atomic increment, so a read-modify-write counter there would lose
/// updates under concurrent/shared sessions and become a <em>bypass</em> of the cap it implements.</para>
/// <para>⚠️ <b>Object-identity keying.</b> The counter is keyed by the <see cref="AgentSession"/> <i>object</i>.
/// This enforces correctly for a long-lived in-memory session, but a deployment that reloads a persisted session
/// into a <b>fresh</b> object each turn will reset the counter (a bypass). For persisted-session deployments,
/// enforce rate limits at a layer that has a stable logical session id, or supply one out of band.</para>
/// <para>Time comes from an injectable <see cref="TimeProvider"/> so tests are deterministic (no wall-clock flake).</para>
/// </summary>
public sealed class RateLimitGate : SessionContextGate
{
    private readonly int _maxRuns;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly ConditionalWeakTable<AgentSession, Cell> _cells = new();

    /// <inheritdoc/>
    public override string PolicyName => "RateLimitGate";

    /// <summary>Creates the gate: at most <paramref name="maxRuns"/> runs per <paramref name="window"/> per session.</summary>
    public RateLimitGate(int maxRuns, TimeSpan window, TimeProvider? timeProvider = null)
    {
        if (maxRuns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRuns), maxRuns, "maxRuns must be at least 1.");
        }

        _maxRuns = maxRuns;
        _window = window;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    protected override ValueTask<GateVerdict> CheckSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var cell = _cells.GetOrCreateValue(session);
        var now = _time.GetUtcNow();

        bool over;
        lock (cell)   // serialize the read-modify-write for this session (no atomic StateBag primitive exists)
        {
            if (now - cell.WindowStart >= _window)
            {
                cell.WindowStart = now;
                cell.Count = 0;
            }

            cell.Count++;
            over = cell.Count > _maxRuns;
        }

        return new ValueTask<GateVerdict>(over
            ? GateVerdict.Block(PolicyName, $"rate limit exceeded ({_maxRuns} run(s) per {_window.TotalSeconds:F0}s)")
            : GateVerdict.Allow(PolicyName));
    }

    private sealed class Cell
    {
        public DateTimeOffset WindowStart;
        public int Count;
    }
}
