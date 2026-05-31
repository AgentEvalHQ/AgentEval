// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// The single, canonical model-key matching algorithm shared by the pricing tables
/// (<c>ModelPricing</c> and <c>JudgeCostMap</c>) — ARC-07. Both previously copy-pasted the same
/// "exact match, then longest registered substring" scan; that duplication is what produced the
/// non-deterministic-ordering bug (BUG-11) and was the most drift-prone part of the two tables.
/// Centralising it here means a future fix to the matching rule is made once.
/// </summary>
/// <remarks>
/// This consolidates the LOOKUP algorithm only. Fully merging the two pricing tables into one registry
/// (they still hold their own entries and disagree on a handful of overlapping values, and differ on
/// miss-handling — <c>ModelPricing</c> returns null while <c>JudgeCostMap</c> falls back to a default
/// rate) requires a canonical-pricing decision and calibration re-baselining, and remains vNext.
/// </remarks>
internal static class ModelKeyMatcher
{
    /// <summary>
    /// Resolves <paramref name="model"/> against <paramref name="table"/>: an exact (case-insensitive)
    /// key match if present, otherwise the LONGEST registered key that is a case-insensitive substring of
    /// <paramref name="model"/> (so "gpt-4o-mini" wins over "gpt-4o" wins over "gpt-4" for a deployment
    /// name that embeds the longer key). Deterministic regardless of dictionary enumeration order.
    /// </summary>
    /// <returns><c>true</c> and the matched value, or <c>false</c> when the model is null/blank or unmatched.</returns>
    public static bool TryMatchLongestFirst<T>(
        IReadOnlyDictionary<string, T> table, string? model, out T value)
    {
        value = default!;
        if (string.IsNullOrWhiteSpace(model))
            return false;

        if (table.TryGetValue(model, out var exact))
        {
            value = exact;
            return true;
        }

        string? best = null;
        foreach (var key in table.Keys)
        {
            if (model.Contains(key, StringComparison.OrdinalIgnoreCase) &&
                (best is null || key.Length > best.Length))
            {
                best = key;
            }
        }

        if (best is null)
            return false;

        value = table[best];
        return true;
    }
}
