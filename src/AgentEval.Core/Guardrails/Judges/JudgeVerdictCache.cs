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
    private readonly ConcurrentDictionary<string, GateVerdict> _allowed = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string PolicyName => _inner.PolicyName;

    /// <summary>Wraps <paramref name="inner"/> with an allow-verdict cache of at most <paramref name="maxEntries"/> entries.</summary>
    public JudgeVerdictCache(IChatGate inner, int maxEntries = 4096)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "must be at least 1.");
        }

        _inner = inner;
        _maxEntries = maxEntries;
    }

    /// <inheritdoc/>
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        var safe = text ?? string.Empty;
        var key = HashOf(safe);
        if (_allowed.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var verdict = await _inner.InspectAsync(safe, cancellationToken).ConfigureAwait(false);

        // Cache only Allow (see class remarks). Stop admitting once full — never evict a proven-safe entry.
        if (verdict.Action == GateAction.Allow && _allowed.Count < _maxEntries)
        {
            _allowed.TryAdd(key, verdict);
        }

        return verdict;
    }

    private static string HashOf(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
