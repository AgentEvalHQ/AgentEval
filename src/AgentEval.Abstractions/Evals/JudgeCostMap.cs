// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Per-model token-pricing table used by <c>AtomicLlmEval</c> to translate
/// judge token usage into <see cref="EvalProvenance.EstimatedCost"/>.
/// </summary>
/// <remarks>
/// <para>
/// Rates are USD per 1,000 tokens. The table covers common Azure OpenAI / OpenAI
/// judge deployments shipped with the v0.8.1-beta benchmarks. Unknown model keys
/// fall back to <see cref="DefaultInputRatePer1K"/> / <see cref="DefaultOutputRatePer1K"/>
/// — a conservative gpt-4o-mini-grade rate that produces a useful order-of-magnitude
/// signal rather than the misleading $0 the composite cost rollup currently sums to
/// (v1.1 task 1.7 / 6-plan F-002).
/// </para>
/// <para>
/// Consumers shipping their own judge models can extend the table via
/// <see cref="Register(string, double, double)"/>.
/// </para>
/// <para>
/// Match is case-insensitive on the full model identifier, then on substrings
/// (e.g. <c>"gpt-4o-mini-2024-07-18"</c> matches the <c>"gpt-4o-mini"</c> entry).
/// </para>
/// </remarks>
public static class JudgeCostMap
{
    /// <summary>Default input rate per 1,000 tokens (USD) when the model is unknown.</summary>
    public const double DefaultInputRatePer1K = 0.00015;

    /// <summary>Default output rate per 1,000 tokens (USD) when the model is unknown.</summary>
    public const double DefaultOutputRatePer1K = 0.00060;

    /// <summary>Per-model pricing record. Rates are USD per 1,000 tokens.</summary>
    /// <param name="InputRatePer1K">USD per 1K input (prompt) tokens.</param>
    /// <param name="OutputRatePer1K">USD per 1K output (completion) tokens.</param>
    public readonly record struct ModelRate(double InputRatePer1K, double OutputRatePer1K);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelRate> s_rates = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI / Azure OpenAI list prices snapshot (2026-05). These are intentionally
        // conservative defaults; downstream consumers should Register(...) more precise
        // rates if they hit a different list price or volume discount.
        ["gpt-4o"]                   = new(0.00250, 0.01000),
        ["gpt-4o-mini"]              = new(0.00015, 0.00060),
        ["gpt-4.1"]                  = new(0.00200, 0.00800),
        ["gpt-4.1-mini"]             = new(0.00040, 0.00160),
        ["gpt-4.1-nano"]             = new(0.00010, 0.00040),
        ["gpt-4-turbo"]              = new(0.01000, 0.03000),
        ["gpt-4"]                    = new(0.03000, 0.06000),
        ["gpt-3.5-turbo"]            = new(0.00050, 0.00150),
        ["gpt-5"]                    = new(0.00500, 0.02000),
        ["gpt-5-chat"]               = new(0.00500, 0.02000),
        ["gpt-5-mini"]               = new(0.00030, 0.00120),
        ["o1-preview"]               = new(0.01500, 0.06000),
        ["o1-mini"]                  = new(0.00300, 0.01200),
        ["o3-mini"]                  = new(0.00100, 0.00400),
        // Anthropic Claude (Bedrock / direct) — used by some calibration harnesses
        ["claude-3-5-sonnet"]        = new(0.00300, 0.01500),
        ["claude-3-5-haiku"]         = new(0.00080, 0.00400),
        ["claude-3-opus"]            = new(0.01500, 0.07500),
    };

    /// <summary>
    /// Returns the per-1K input/output rates for <paramref name="judgeModel"/>.
    /// Tries an exact match first, then a substring match against registered keys.
    /// Falls back to <see cref="DefaultInputRatePer1K"/> / <see cref="DefaultOutputRatePer1K"/>
    /// when no match is found or when <paramref name="judgeModel"/> is null/whitespace.
    /// </summary>
    /// <param name="judgeModel">
    /// The judge model identifier as plumbed through <c>AtomicLlmEval.judgeModel</c>.
    /// Typically an Azure OpenAI deployment name or a canonical OpenAI model id such as
    /// <c>"gpt-4o-mini"</c>. Substring matching tolerates dated suffixes like
    /// <c>"gpt-4o-mini-2024-07-18"</c>.
    /// </param>
    /// <returns>
    /// The matched <see cref="ModelRate"/>, or the default rate when <paramref name="judgeModel"/>
    /// is null / whitespace / unknown.
    /// </returns>
    public static ModelRate GetRate(string? judgeModel)
    {
        if (string.IsNullOrWhiteSpace(judgeModel))
            return new ModelRate(DefaultInputRatePer1K, DefaultOutputRatePer1K);

        if (s_rates.TryGetValue(judgeModel, out var exact))
            return exact;

        // Walk registered keys longest-first so that more specific keys (e.g. "gpt-4o-mini")
        // win over less specific ones (e.g. "gpt-4") when the judge identifier carries a
        // dated suffix such as "gpt-4o-mini-2024-07-18". Equal-length keys retain their
        // ConcurrentDictionary ordering (insertion-time, effectively undefined) but in
        // practice the table has no collisions at the same length.
        foreach (var kv in s_rates.OrderByDescending(p => p.Key.Length))
        {
            if (judgeModel.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return new ModelRate(DefaultInputRatePer1K, DefaultOutputRatePer1K);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="judgeModel"/> has an explicit registration
    /// (exact key) — useful for distinguishing fallback-priced runs from priced-as-configured runs.
    /// </summary>
    /// <param name="judgeModel">The judge model identifier.</param>
    /// <returns><c>true</c> if registered; <c>false</c> when null, whitespace, or unknown.</returns>
    public static bool IsRegistered(string? judgeModel) =>
        !string.IsNullOrWhiteSpace(judgeModel) && s_rates.ContainsKey(judgeModel);

    /// <summary>
    /// Registers or overrides the rate for <paramref name="judgeModel"/>. Last-write-wins.
    /// </summary>
    /// <param name="judgeModel">The judge model identifier (exact key for <see cref="GetRate"/> lookup).</param>
    /// <param name="inputRatePer1K">USD per 1K input tokens.</param>
    /// <param name="outputRatePer1K">USD per 1K output tokens.</param>
    public static void Register(string judgeModel, double inputRatePer1K, double outputRatePer1K)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(judgeModel);
        s_rates[judgeModel] = new ModelRate(inputRatePer1K, outputRatePer1K);
    }

    /// <summary>
    /// Computes the USD cost for the given input/output token counts at the rate registered
    /// for <paramref name="judgeModel"/> (or the default rate when unknown).
    /// </summary>
    /// <param name="judgeModel">The judge model identifier.</param>
    /// <param name="inputTokens">Input (prompt) token count.</param>
    /// <param name="outputTokens">Output (completion) token count.</param>
    /// <returns>Estimated cost in USD. Returns 0 when both token counts are non-positive.</returns>
    public static double EstimateCost(string? judgeModel, long inputTokens, long outputTokens)
    {
        if (inputTokens <= 0 && outputTokens <= 0) return 0;
        var rate = GetRate(judgeModel);
        var inT = Math.Max(0, inputTokens);
        var outT = Math.Max(0, outputTokens);
        return (inT / 1000.0) * rate.InputRatePer1K + (outT / 1000.0) * rate.OutputRatePer1K;
    }
}
