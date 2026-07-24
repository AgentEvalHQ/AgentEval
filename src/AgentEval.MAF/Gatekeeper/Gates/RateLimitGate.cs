// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
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
/// <para>⚠️ <b>Object-identity keying (default), and how to survive a reload.</b> By default the counter is keyed
/// by the <see cref="AgentSession"/> <i>object</i>. This enforces correctly for a long-lived in-memory session,
/// but a deployment that reloads a persisted session into a <b>fresh</b> object each turn (or load-balances a
/// logical session across workers) would reset the counter — a bypass. Supply a <c>sessionKeySelector</c> that
/// returns a <b>stable logical session id</b> (e.g. off the session's <c>StateBag</c>, a claim, or a store key)
/// and the counter is instead kept in a process-wide table keyed by that id, so the cap survives a reload. A
/// selector that returns <see langword="null"/>/empty for a given session falls back to object identity for that
/// session (so a partial deployment degrades gracefully rather than throwing).</para>
/// <para>Time comes from an injectable <see cref="TimeProvider"/> so tests are deterministic (no wall-clock flake).</para>
/// </summary>
public sealed class RateLimitGate : SessionContextGate
{
    private readonly int _maxRuns;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Func<AgentSession, string?>? _sessionKeySelector;
    private readonly ConditionalWeakTable<AgentSession, Cell> _cells = new();                             // object-identity path (GC-evicted)
    private readonly ConcurrentDictionary<string, Cell> _keyedCells = new(StringComparer.Ordinal);        // stable-id path (survives reload)

    /// <inheritdoc/>
    public override string PolicyName => "RateLimitGate";

    /// <summary>Creates the gate: at most <paramref name="maxRuns"/> runs per <paramref name="window"/> per session.
    /// Pass <paramref name="sessionKeySelector"/> to key the counter on a stable logical session id (surviving a
    /// persisted-session reload) instead of the session object's identity — see the class remarks. The keyed table
    /// is bounded by the number of distinct logical session ids the process observes.</summary>
    public RateLimitGate(int maxRuns, TimeSpan window, TimeProvider? timeProvider = null, Func<AgentSession, string?>? sessionKeySelector = null)
    {
        if (maxRuns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRuns), maxRuns, "maxRuns must be at least 1.");
        }

        if (window <= TimeSpan.Zero)
        {
            // A non-positive window would reset the counter every call — silently disabling the limit (fail-open).
            throw new ArgumentOutOfRangeException(nameof(window), window, "window must be positive.");
        }

        _maxRuns = maxRuns;
        _window = window;
        _time = timeProvider ?? TimeProvider.System;
        _sessionKeySelector = sessionKeySelector;
    }

    /// <inheritdoc/>
    protected override ValueTask<GateVerdict> CheckSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var cell = ResolveCell(session);
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

    // Stable-id path when a selector is supplied and yields a non-empty id (counter survives a session reload);
    // otherwise the GC-evicted object-identity table, preserving the exact default behavior.
    private Cell ResolveCell(AgentSession session)
    {
        if (_sessionKeySelector is not null)
        {
            var key = _sessionKeySelector(session);
            if (!string.IsNullOrEmpty(key))
            {
                return _keyedCells.GetOrAdd(key, static _ => new Cell());
            }
        }

        return _cells.GetOrCreateValue(session);
    }

    private sealed class Cell
    {
        public DateTimeOffset WindowStart;
        public int Count;
    }
}
