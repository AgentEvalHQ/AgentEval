// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Wraps a judge <see cref="IChatGate"/> with a content-hash cache so identical turns don't re-litigate — the
/// "precedent" of The Tribunal. Cuts token cost and latency when the same content recurs (e.g. a RAG chunk seen
/// across many runs).
/// <para><b>Only <see cref="GateAction.Allow"/> verdicts are cached.</b> A block is re-evaluated every time — so a
/// transient fail-closed block (a judge timeout / model error) can never be cached into a permanent block, and a
/// real detection is always re-confirmed.</para>
/// <para><b>Bounded LRU + TTL (Phase 5, P5-4).</b> The cache holds at most <c>maxEntries</c> allows, evicting the
/// LEAST-RECENTLY-USED when full (a full cache still admits hot new content — evicting only forces a cheap
/// re-judge, never a wrong verdict). An optional <c>ttl</c> expires a memoized allow after a fixed lifetime so a
/// later-improved judge is not shadowed forever by an old allow; with no TTL an allow is memoized for the process
/// lifetime. An optional <c>invalidationKey</c> (a hash of the judge / prompt / model) is folded into the cache key
/// so a config change never serves a stale precedent. The bound is a <i>hard</i> cap under a lock, so it cannot
/// overshoot.</para>
/// </summary>
public sealed class JudgeVerdictCache : IChatGate
{
    private readonly IChatGate _inner;
    private readonly int _maxEntries;
    private readonly TimeSpan? _ttl;
    private readonly string _invalidationKey;
    private readonly TimeProvider _timeProvider;
    private readonly Action<JudgeCacheEvent>? _onLookup;

    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();   // First = most-recently-used, Last = least-recently-used
    private long _hits;
    private long _misses;

    private sealed record Entry(string Key, GateVerdict Verdict, DateTimeOffset FirstSeenUtc);

    /// <inheritdoc/>
    public string PolicyName => _inner.PolicyName;

    /// <summary>Cache hits served so far (P3-7).</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups that missed and re-litigated the inner judge (P3-7).</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Entries currently cached.</summary>
    public int Count
    {
        get { lock (_lock) { return _map.Count; } }
    }

    /// <summary>Wraps <paramref name="inner"/> with an allow-verdict cache.</summary>
    /// <param name="inner">The judge whose Allow verdicts are memoized.</param>
    /// <param name="maxEntries">Hard cap on cached entries; the least-recently-used is evicted past this (default 4096).</param>
    /// <param name="onLookup">Optional cache-precedent hook (P3-7) invoked on every lookup — a caller wires it to emit <c>gate.cache.*</c> evidence.</param>
    /// <param name="timeProvider">Clock (testability); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="ttl">Optional lifetime for a memoized allow (P5-4); null keeps it for the process lifetime.</param>
    /// <param name="invalidationKey">Optional judge/prompt/model version folded into the cache key (P5-4) so a config change never serves a stale precedent.</param>
    public JudgeVerdictCache(
        IChatGate inner,
        int maxEntries = 4096,
        Action<JudgeCacheEvent>? onLookup = null,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null,
        string? invalidationKey = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "must be at least 1.");
        }

        if (ttl is { } t && t <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "must be positive when set.");
        }

        _inner = inner;
        _maxEntries = maxEntries;
        _onLookup = onLookup;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl;
        _invalidationKey = invalidationKey ?? string.Empty;
    }

    /// <inheritdoc/>
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        var safe = text ?? string.Empty;
        var key = HashOf(safe);

        if (TryGetCached(key, out var cachedVerdict, out var firstSeen))
        {
            Interlocked.Increment(ref _hits);
            NotifyLookup(new JudgeCacheEvent(PolicyName, Hit: true, firstSeen));   // precedent: served the original allow
            return cachedVerdict;
        }

        Interlocked.Increment(ref _misses);
        var verdict = await _inner.InspectAsync(safe, cancellationToken).ConfigureAwait(false);

        // Cache only Allow (see class remarks).
        if (verdict.Action == GateAction.Allow)
        {
            Admit(key, verdict);
        }

        NotifyLookup(new JudgeCacheEvent(PolicyName, Hit: false, OriginalTimestampUtc: null));
        return verdict;
    }

    private bool TryGetCached(string key, out GateVerdict verdict, out DateTimeOffset firstSeenUtc)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                if (_ttl is { } ttl && _timeProvider.GetUtcNow() - node.Value.FirstSeenUtc >= ttl)
                {
                    _lru.Remove(node);   // expired — drop it (a later-improved judge gets to re-judge)
                    _map.Remove(key);
                }
                else
                {
                    _lru.Remove(node);   // touch: promote to most-recently-used
                    _lru.AddFirst(node);
                    verdict = node.Value.Verdict;
                    firstSeenUtc = node.Value.FirstSeenUtc;
                    return true;
                }
            }

            verdict = null!;
            firstSeenUtc = default;
            return false;
        }
    }

    private void Admit(string key, GateVerdict verdict)
    {
        lock (_lock)
        {
            if (_map.ContainsKey(key))
            {
                return;   // already cached (a concurrent miss admitted it first) — leave its recency/first-seen intact
            }

            var node = _lru.AddFirst(new Entry(key, verdict, _timeProvider.GetUtcNow()));
            _map[key] = node;

            while (_map.Count > _maxEntries)
            {
                var lru = _lru.Last!;   // least-recently-used
                _lru.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
        }
    }

    // The precedent hook is pure observability — a throw from it must never propagate out of InspectAsync, where
    // the gate loop would convert it into a fail-closed Block and turn a genuine cached Allow into a refusal.
    private void NotifyLookup(JudgeCacheEvent cacheEvent)
    {
        if (_onLookup is null)
        {
            return;
        }

        try
        {
            _onLookup(cacheEvent);
        }
        catch
        {
            // Swallow — observability must not change the verdict.
        }
    }

    private string HashOf(string text)
    {
        // Fold the invalidation key in so a judge/prompt/model change never collides with a stale precedent (P5-4).
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_invalidationKey + "\n" + text));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>
/// A cache-precedent event (Phase 3, P3-7) surfaced by <see cref="JudgeVerdictCache"/> on each lookup. On a
/// <see cref="Hit"/>, <see cref="OriginalTimestampUtc"/> is when the served allow was FIRST evaluated — the
/// precedent's age — so a caller can emit a <c>gate.cache.*</c> record showing the verdict was memoized, not
/// freshly litigated. On a miss it is null (the inner judge ran).
/// </summary>
/// <param name="Policy">The judge's policy name.</param>
/// <param name="Hit">True when the verdict was served from cache; false when the inner judge was invoked.</param>
/// <param name="OriginalTimestampUtc">When the served allow was first evaluated (hits only).</param>
public sealed record JudgeCacheEvent(string Policy, bool Hit, DateTimeOffset? OriginalTimestampUtc);
