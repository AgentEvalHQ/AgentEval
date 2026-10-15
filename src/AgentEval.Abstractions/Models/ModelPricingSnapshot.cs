// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Collections.ObjectModel;

namespace AgentEval.Models;

/// <summary>Immutable pricing rates in deterministic model-key order.</summary>
public sealed class ModelPricingSnapshot
{
    /// <summary>Captured USD rates per thousand tokens, ordered by model key.</summary>
    public IReadOnlyDictionary<string, (decimal InputPer1K, decimal OutputPer1K)> Rates { get; }

    internal ModelPricingSnapshot(KeyValuePair<string, (decimal InputPer1K, decimal OutputPer1K)>[] rates)
        => Rates = new ReadOnlyDictionary<string, (decimal InputPer1K, decimal OutputPer1K)>(
            rates.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>Resolves rates with the same exact-then-longest-substring policy as <see cref="ModelPricing"/>.</summary>
    public (decimal InputPer1K, decimal OutputPer1K, decimal InputPricePerMillion, decimal OutputPricePerMillion)? GetPricing(string? modelName)
    {
        if (!ModelKeyMatcher.TryMatchLongestFirst(Rates, modelName, out var price)) return null;
        return (price.InputPer1K, price.OutputPer1K, price.InputPer1K * 1000, price.OutputPer1K * 1000);
    }
}
