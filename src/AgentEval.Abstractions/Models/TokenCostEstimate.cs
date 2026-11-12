// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Models;

/// <summary>USD prices per thousand text tokens. Cached input is a subset of input, never additional input.</summary>
public sealed record TokenPricing(decimal InputPer1K, decimal OutputPer1K, decimal? CachedInputPer1K = null)
{
    /// <summary>Optional rate provenance, such as a dated list-price URL or a customer rate-card identifier.</summary>
    public string? Source { get; init; }
}

/// <summary>How an estimate treated provider usage and cache discounts.</summary>
public enum TokenCostStatus
{
    /// <summary>Usage or a required price was unavailable.</summary>
    Unpriced,
    /// <summary>All reported counts were zero.</summary>
    ZeroUsage,
    /// <summary>The provider explicitly reported no cached input.</summary>
    Standard,
    /// <summary>The reported cached subset was charged at its configured rate.</summary>
    CacheAdjusted,
    /// <summary>The cache count was unavailable; all input was priced at the ordinary rate.</summary>
    UndiscountedUnknownCache,
    /// <summary>Cached input was reported but no cached rate was configured.</summary>
    UndiscountedMissingCacheRate,
    /// <summary>Counts, prices, or arithmetic were invalid; no amount was produced.</summary>
    Invalid
}

/// <summary>Reproducible text-token estimate, excluding cache writes, storage, tools and provider-specific fees.</summary>
public sealed record TokenCostEstimate(
    decimal? Amount, TokenCostStatus Status, long? InputTokens, long? OutputTokens,
    long? CachedInputTokens, TokenPricing? Pricing)
{
    /// <summary>Effective model identifier used for rate lookup.</summary>
    public string? ModelId { get; init; }
    /// <summary>Origin of the pricing identity: override, response, configured, or unknown.</summary>
    public string? ModelSource { get; init; }
    /// <summary>True when text-length estimates replaced provider usage.</summary>
    public bool UsageEstimated { get; init; }

    /// <summary>Calculates input minus cached input, cached input, and output once each. Missing is never zero.</summary>
    public static TokenCostEstimate Calculate(long? input, long? output, long? cachedInput, TokenPricing? pricing)
    {
        TokenCostEstimate Result(decimal? amount, TokenCostStatus status) =>
            new(amount, status, input, output, cachedInput, pricing);

        if (input < 0 || output < 0 || cachedInput < 0 || cachedInput > input ||
            pricing is { InputPer1K: < 0 } or { OutputPer1K: < 0 } or { CachedInputPer1K: < 0 })
            return Result(null, TokenCostStatus.Invalid);
        if (input is null || output is null) return Result(null, TokenCostStatus.Unpriced);
        if (input == 0 && output == 0) return Result(0, TokenCostStatus.ZeroUsage);
        if (pricing is null) return Result(null, TokenCostStatus.Unpriced);
        try
        {
            var discounted = cachedInput is > 0 && pricing.CachedInputPer1K.HasValue;
            var cached = discounted ? cachedInput!.Value : 0;
            var amount = (input.Value - cached) / 1000m * pricing.InputPer1K
                + cached / 1000m * (pricing.CachedInputPer1K ?? 0)
                + output.Value / 1000m * pricing.OutputPer1K;
            var status = discounted ? TokenCostStatus.CacheAdjusted
                : cachedInput is null ? TokenCostStatus.UndiscountedUnknownCache
                : cachedInput > 0 ? TokenCostStatus.UndiscountedMissingCacheRate : TokenCostStatus.Standard;
            return Result(amount, status);
        }
        catch (OverflowException) { return Result(null, TokenCostStatus.Invalid); }
    }
}
