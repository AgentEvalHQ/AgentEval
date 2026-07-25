// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
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
/// <para><b>Tradeoffs (by design).</b> There is no TTL: an allow is memoized for the process lifetime, so if the
/// judge is nondeterministic a first-time allow sticks and a later-improved judge won't re-block that exact
/// content — clear/rebuild the cache on a judge change. The bound is a <i>soft</i> cap (check-then-add), so under
/// heavy concurrency it may overshoot slightly. Both are acceptable for a same-content dedup cache.</para>
/// </summary>
public sealed class JudgeVerdictCache : IChatGate
{
    private readonly IChatGate _inner;
    private readonly int _maxEntries;
    private readonly TimeProvider _timeProvider;
    private readonly Action<JudgeCacheEvent>? _onLookup;
    private readonly ConcurrentDictionary<string, (GateVerdict Verdict, DateTimeOffset FirstSeenUtc)> _allowed = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    /// <inheritdoc/>
    public string PolicyName => _inner.PolicyName;

    /// <summary>Cache hits served so far (P3-7).</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups that missed and re-litigated the inner judge (P3-7).</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Wraps <paramref name="inner"/> with an allow-verdict cache.</summary>
    /// <param name="inner">The judge whose Allow verdicts are memoized.</param>
    /// <param name="maxEntries">Soft cap on cached entries (default 4096).</param>
    /// <param name="onLookup">Optional cache-precedent hook (P3-7) invoked on every lookup — a caller wires it to
    /// emit <c>gate.cache.*</c> evidence (AllowCached, the original timestamp, hit/miss).</param>
    /// <param name="timeProvider">Clock stamped onto a first-seen allow (testability); defaults to <see cref="TimeProvider.System"/>.</param>
    public JudgeVerdictCache(IChatGate inner, int maxEntries = 4096, Action<JudgeCacheEvent>? onLookup = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "must be at least 1.");
        }

        _inner = inner;
        _maxEntries = maxEntries;
        _onLookup = onLookup;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        var safe = text ?? string.Empty;
        var key = HashOf(safe);
        if (_allowed.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _hits);
            _onLookup?.Invoke(new JudgeCacheEvent(PolicyName, Hit: true, cached.FirstSeenUtc));   // precedent: served the original allow
            return cached.Verdict;
        }

        Interlocked.Increment(ref _misses);
        var verdict = await _inner.InspectAsync(safe, cancellationToken).ConfigureAwait(false);

        // Cache only Allow (see class remarks). Stop admitting once full — never evict a proven-safe entry.
        if (verdict.Action == GateAction.Allow && _allowed.Count < _maxEntries)
        {
            _allowed.TryAdd(key, (verdict, _timeProvider.GetUtcNow()));
        }

        _onLookup?.Invoke(new JudgeCacheEvent(PolicyName, Hit: false, OriginalTimestampUtc: null));
        return verdict;
    }

    private static string HashOf(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
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
