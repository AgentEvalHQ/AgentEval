// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Collections.ObjectModel;

namespace AgentEval.Models;

/// <summary>Immutable pricing rates in deterministic model-key order.</summary>
public sealed class ModelPricingSnapshot
{
    /// <summary>Captured USD rates per thousand tokens, ordered by model key.</summary>
    public IReadOnlyDictionary<string, (decimal InputPer1K, decimal OutputPer1K)> Rates { get; }

    /// <summary>Captured immutable full rate cards, including cached-input rates and provenance.</summary>
    public IReadOnlyDictionary<string, TokenPricing> TokenRates { get; }

    internal ModelPricingSnapshot(KeyValuePair<string, TokenPricing>[] rates)
    {
        var ordered = rates.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        TokenRates = new ReadOnlyDictionary<string, TokenPricing>(
            ordered.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
        Rates = new ReadOnlyDictionary<string, (decimal InputPer1K, decimal OutputPer1K)>(
            ordered.ToDictionary(p => p.Key, p => (p.Value.InputPer1K, p.Value.OutputPer1K), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a full captured rate card without reading the mutable global table.</summary>
    public TokenPricing? GetTokenPricing(string? modelName) =>
        ModelKeyMatcher.TryMatchLongestFirst(TokenRates, modelName, out var pricing) ? pricing : null;

    /// <summary>Resolves rates with the same exact-then-longest-substring policy as <see cref="ModelPricing"/>.</summary>
    public (decimal InputPer1K, decimal OutputPer1K, decimal InputPricePerMillion, decimal OutputPricePerMillion)? GetPricing(string? modelName)
    {
        if (!ModelKeyMatcher.TryMatchLongestFirst(Rates, modelName, out var price)) return null;
        return (price.InputPer1K, price.OutputPer1K, price.InputPer1K * 1000, price.OutputPer1K * 1000);
    }
}
