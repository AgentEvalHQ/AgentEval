// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Reduces a sequence of eval results to a total cost and cache-hit summary.</summary>
public static class CostRollup
{
    /// <summary>
    /// Sums estimated costs. A total is complete or absent: when any result is not priced
    /// (<see cref="EvalProvenance.EstimatedCost"/> is <see langword="null"/>) the total is <see langword="null"/>, and
    /// <c>Unpriced</c> names those results by metric key. <c>AllCacheHits</c> is true only when every result was a
    /// cache hit and the sequence is non-empty.
    /// </summary>
    /// <remarks>
    /// The total of the priced parts alone is not returned: it would read as the cost of the whole wherever it lands
    /// (a composite's own <see cref="EvalProvenance.EstimatedCost"/>, a report line, a sum across runs), and nothing
    /// there would carry a "partial" flag. An empty sequence costs 0.
    /// </remarks>
    public static (double? EstimatedCost, bool AllCacheHits, IReadOnlyList<string> Unpriced) Aggregate(IEnumerable<EvalResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        double total = 0;
        bool allHit = true;
        bool any = false;
        var unpriced = new List<string>();
        foreach (var r in results)
        {
            any = true;
            if (r.Provenance.EstimatedCost is { } cost) total += cost;
            else unpriced.Add(r.Metric.Key);
            if (!r.Provenance.CacheHit) allHit = false;
        }
        return (unpriced.Count == 0 ? total : null, any && allHit, unpriced);
    }
}
