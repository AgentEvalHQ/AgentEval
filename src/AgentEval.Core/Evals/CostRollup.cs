// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Reduces a sequence of eval results to a total cost and cache-hit summary.</summary>
public static class CostRollup
{
    /// <summary>Sums estimated costs; <c>AllCacheHits</c> is true only when every result was a cache hit and the sequence is non-empty.</summary>
    public static (double EstimatedCost, bool AllCacheHits) Aggregate(IEnumerable<EvalResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        double total = 0;
        bool allHit = true;
        bool any = false;
        foreach (var r in results)
        {
            any = true;
            total += r.Provenance.EstimatedCost;
            if (!r.Provenance.CacheHit) allHit = false;
        }
        return (total, any && allHit);
    }
}
