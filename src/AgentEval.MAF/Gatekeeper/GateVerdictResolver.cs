// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A bounded, TTL'd, in-memory <c>referenceId → </c><see cref="GateEvidence"/> index (Phase 3, P3-1). The
/// model-facing refusal shows only an opaque <c>referenceId</c>; an operator (or the <c>agenteval trace
/// find-reference</c> CLI) resolves that id back to the full audit record in O(1) here — <b>without needing the
/// trace</b>, because the resolver is fed directly from the evidence stream. Bounded (FIFO eviction past a max
/// count) and TTL'd so it can run continuously without unbounded growth; thread-safe.
/// </summary>
public sealed class GateVerdictResolver
{
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly Dictionary<string, (GateEvidence Evidence, DateTimeOffset ExpiresAtUtc)> _byReference = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();

    /// <summary>Creates the resolver.</summary>
    /// <param name="maxEntries">Maximum retained records; the oldest are evicted past this (default 4096).</param>
    /// <param name="ttl">How long a record stays resolvable (default 1 hour).</param>
    /// <param name="timeProvider">Clock (testability); defaults to <see cref="TimeProvider.System"/>.</param>
    public GateVerdictResolver(int maxEntries = 4096, TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "must be at least 1.");
        }

        _maxEntries = maxEntries;
        _ttl = ttl ?? TimeSpan.FromHours(1);
        if (_ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "must be positive.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Indexes <paramref name="evidence"/> under its <see cref="GateEvidence.ReferenceId"/>, evicting the oldest entry if at capacity.</summary>
    public void Record(GateEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_lock)
        {
            var expiresAt = _timeProvider.GetUtcNow() + _ttl;
            if (!_byReference.ContainsKey(evidence.ReferenceId))
            {
                _insertionOrder.Enqueue(evidence.ReferenceId);
            }

            _byReference[evidence.ReferenceId] = (evidence, expiresAt);

            while (_insertionOrder.Count > _maxEntries)
            {
                var oldest = _insertionOrder.Dequeue();
                _byReference.Remove(oldest);
            }
        }
    }

    /// <summary>Resolves a <paramref name="referenceId"/> to its record, or false if unknown or expired (an expired entry is dropped).</summary>
    public bool TryResolve(string referenceId, out GateEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(referenceId);
        lock (_lock)
        {
            if (_byReference.TryGetValue(referenceId, out var entry))
            {
                if (_timeProvider.GetUtcNow() < entry.ExpiresAtUtc)
                {
                    evidence = entry.Evidence;
                    return true;
                }

                _byReference.Remove(referenceId);   // lazily drop the expired entry
            }

            evidence = null!;
            return false;
        }
    }

    /// <summary>Count of currently-retained records (expired-but-not-yet-swept entries included).</summary>
    public int Count
    {
        get { lock (_lock) { return _byReference.Count; } }
    }
}
