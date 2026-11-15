// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Globalization;

namespace AgentEval.Models;

/// <summary>
/// Performance metrics captured during an agent run.
/// </summary>
public class PerformanceMetrics
{
    /// <summary>When the request started.</summary>
    public DateTimeOffset StartTime { get; set; }
    
    /// <summary>When the request completed.</summary>
    public DateTimeOffset EndTime { get; set; }
    
    /// <summary>Total duration of the request.</summary>
    public TimeSpan TotalDuration => EndTime - StartTime;
    
    /// <summary>Time to receive first token (streaming only).</summary>
    public TimeSpan? TimeToFirstToken { get; set; }
    
    /// <summary>Number of prompt/input tokens used.</summary>
    public int? PromptTokens { get; set; }
    
    /// <summary>Number of completion/output tokens used.</summary>
    public int? CompletionTokens { get; set; }
    
    /// <summary>
    /// Total tokens used, or <see langword="null"/> when the provider reported NO usage at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This was <c>(PromptTokens ?? 0) + (CompletionTokens ?? 0)</c>, which is never null.</b>
    /// The type said <c>int?</c> — "this can be unknown" — and the body made that impossible, so a
    /// provider that reported no usage read as a run that used ZERO tokens. That is the absence-as-a-
    /// zero defect, in the flattering direction: an unmeasured run is the cheapest one in the suite.
    /// </para>
    /// <para>
    /// Two readers were relying on the promise the body broke.
    /// <c>StochasticResult.GetTotalTokens</c> filters on <c>Performance?.TotalTokens != null</c> — a
    /// filter that could never remove anything, so every unmeasured run contributed a fabricated 0
    /// into <c>TotalTokenStats</c> and pulled the mean down. <c>PerformanceAssertions</c> compares
    /// <c>TotalTokens &gt; max</c>, so an unmeasured run passed every token budget. Both are correct
    /// now without changing a line of either.
    /// </para>
    /// <para>
    /// ⚠ PARTIAL usage is still usage: when one side is reported and the other is not, the missing
    /// side counts as 0 and the total is real. Only BOTH being absent means nobody measured.
    /// </para>
    /// </remarks>
    public int? TotalTokens => PromptTokens is null && CompletionTokens is null
        ? null
        : (int)Math.Min((long)(PromptTokens ?? 0) + (CompletionTokens ?? 0), int.MaxValue);
    
    /// <summary>Number of tool calls made.</summary>
    public int ToolCallCount { get; set; }
    
    /// <summary>Total time spent in tool execution.</summary>
    public TimeSpan TotalToolTime { get; set; }
    
    /// <summary>Average time per tool call.</summary>
    public TimeSpan AverageToolTime => ToolCallCount > 0 
        ? TimeSpan.FromTicks(TotalToolTime.Ticks / ToolCallCount) 
        : TimeSpan.Zero;
    
    /// <summary>Estimated cost in USD (based on model pricing).</summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>Usage, effective rates, model source, and cache assumptions behind the estimate.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TokenCostEstimate? CostEstimate { get; set; }
    
    /// <summary>Model used for the request.</summary>
    public string? ModelUsed { get; set; }
    
    /// <summary>Whether streaming was used.</summary>
    public bool WasStreaming { get; set; }
    
    /// <summary>Indicates if token counts are estimated (true) vs actual from provider (false).</summary>
    public bool TokensAreEstimated { get; set; }
    
    public override string ToString()
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Duration: {TotalDuration.TotalMilliseconds:F0}ms")
        };
        
        if (TimeToFirstToken.HasValue)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"TTFT: {TimeToFirstToken.Value.TotalMilliseconds:F0}ms"));
        
        if (TotalTokens > 0)
            parts.Add($"Tokens: {TotalTokens}");
        
        if (EstimatedCost.HasValue)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Cost: ${EstimatedCost:F4}"));
        
        if (ToolCallCount > 0)
            parts.Add($"Tools: {ToolCallCount}");
        
        return string.Join(" | ", parts);
    }
}

/// <summary>
/// Model pricing information for cost estimation.
/// Thread-safe for concurrent access.
/// </summary>
public static class ModelPricing
{
    /// <summary>Identity of the bundled pricing baseline; hosts may override individual rates with <see cref="SetPricing(string, decimal, decimal)"/>.</summary>
    public const string TableName = "agenteval-model-pricing-2026-10-21";

    /// <summary>
    /// Display context for the historical bundled table version. This does not identify which rates a
    /// particular recorded cost used: hosts can override rates, and providers can report their own cost.
    /// </summary>
    public const string BaselineNotice = "Bundled pricing baseline: " + TableName +
        " (historical rates; custom/provider rates may differ; not a live quote).";

    private static readonly ConcurrentDictionary<string, TokenPricing> _pricing =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI standard-tier list prices, checked against developers.openai.com/api/docs/pricing on 2026-10-07.
        // Rows the page no longer lists keep their last known value and say so. JudgeCostMap carries the same
        // prices for the models both tables know (ModelPricingConsistencyTests).
        ["gpt-5.5"] = new(0.005m, 0.03m),        // $5 / $30 per 1M
        ["gpt-5"] = new(0.00125m, 0.01m),        // $1.25 / $10 per 1M
        ["gpt-5-chat"] = new(0.005m, 0.02m),     // not on OpenAI's page (2026-10-07); last known value
        ["gpt-5-mini"] = new(0.00025m, 0.002m),  // $0.25 / $2 per 1M
        ["gpt-5-nano"] = new(0.00005m, 0.0004m), // $0.05 / $0.40 per 1M
        ["gpt-4.1"] = new(0.002m, 0.008m, 0.0005m) { Source = "https://developers.openai.com/api/docs/models/gpt-4.1 (standard text rates, 2026-11-11)" },
        ["gpt-4.1-mini"] = new(0.0004m, 0.0016m),
        ["gpt-4.1-nano"] = new(0.0001m, 0.0004m),
        ["gpt-4o"] = new(0.0025m, 0.01m, 0.00125m) { Source = "https://developers.openai.com/api/docs/models/gpt-4o (standard text rates, 2026-11-11)" },        // $2.50 / $10 per 1M
        ["gpt-4o-2024-11-20"] = new(0.0025m, 0.01m, 0.00125m) { Source = "https://developers.openai.com/api/docs/models/gpt-4o (standard text rates, 2026-11-11)" },
        ["gpt-4o-mini"] = new(0.00015m, 0.0006m, 0.000075m) { Source = "https://developers.openai.com/api/docs/models/gpt-4o-mini (standard text rates, 2026-11-11)" },
        ["gpt-4o-mini-2024-07-18"] = new(0.00015m, 0.0006m, 0.000075m) { Source = "https://developers.openai.com/api/docs/models/gpt-4o-mini (standard text rates, 2026-11-11)" },
        ["gpt-4-turbo"] = new(0.01m, 0.03m),
        ["gpt-4-turbo-2024-04-09"] = new(0.01m, 0.03m),
        ["gpt-4"] = new(0.03m, 0.06m),
        ["gpt-4-0613"] = new(0.03m, 0.06m),
        ["gpt-3.5-turbo"] = new(0.0005m, 0.0015m),
        ["gpt-3.5-turbo-0125"] = new(0.0005m, 0.0015m),
        
        // OpenAI reasoning models (o-series)
        ["o1"] = new(0.015m, 0.06m),
        ["o1-preview"] = new(0.015m, 0.06m),
        ["o1-mini"] = new(0.003m, 0.012m),
        ["o3-mini"] = new(0.0011m, 0.0044m),     // $1.10 / $4.40 per 1M
        
        // OpenAI embedding models
        ["text-embedding-3-small"] = new(0.00002m, 0m),  // Embedding models have no output cost
        ["text-embedding-3-large"] = new(0.00013m, 0m),
        ["text-embedding-ada-002"] = new(0.0001m, 0m),

        // Bitdeer AI serverless models, keyed by Bitdeer's model id: the prices bitdeer.ai/en/pricing/ai-models charged
        // on 2026-10-21. Rows marked "sale" show a struck-through list price and a lower sale price with no end date;
        // the row holds the observed sale price for historical estimates and the comment the list price. JudgeCostMap carries
        // the same chat-model prices.
        ["zai-org/GLM-5.3-Flash"] = new(0.000083m, 0.000275m),                      // sale; list $0.15 / $0.50 per 1M
        ["zai-org/GLM-5.3"] = new(0.00063m, 0.00198m),                              // sale; list $1.40 / $4.40 per 1M
        ["deepseek-ai/DeepSeek-V4.1-Flash"] = new(0.000144m, 0.000576m),            // sale; list $0.30 / $1.20 per 1M (also -1017)
        ["deepseek-ai/DeepSeek-V4-Flash"] = new(0.00014m, 0.00028m),                // $0.14 / $0.28 per 1M
        ["Qwen/Qwen3.8-27B"] = new(0.0004m, 0.0024m),                               // sale; list $0.50 / $3.00 per 1M
        ["nvidia/NVIDIA-Nemotron-3.5-Super-VL-120B-A12B"] = new(0.0001m, 0.0004m),  // $0.10 / $0.40 per 1M
        ["BAAI/bge-m3"] = new(0.00001m, 0m),                                        // embeddings, $0.01 per 1M input
        ["BAAI/bge-reranker-v2-m3"] = new(0.00001m, 0m),                            // reranker, $0.01 per 1M input

        // Anthropic Claude models. Haiku 3.5 checked against platform.claude.com pricing on 2026-10-07 ($0.80 / $4,
        // retired except on Bedrock and Google Cloud); the other rows are retired models no longer listed there.
        ["claude-3-5-sonnet"] = new(0.003m, 0.015m),
        ["claude-3-5-sonnet-20241022"] = new(0.003m, 0.015m),
        ["claude-3-5-haiku"] = new(0.0008m, 0.004m),
        ["claude-3-opus"] = new(0.015m, 0.075m),
        ["claude-3-sonnet"] = new(0.003m, 0.015m),
        ["claude-3-haiku"] = new(0.00025m, 0.00125m),
        ["claude-2.1"] = new(0.008m, 0.024m),
        ["claude-2.0"] = new(0.008m, 0.024m),
        
        // Google Gemini models
        ["gemini-1.5-pro"] = new(0.00125m, 0.005m),
        ["gemini-1.5-pro-latest"] = new(0.00125m, 0.005m),
        ["gemini-1.5-flash"] = new(0.000075m, 0.0003m),
        ["gemini-1.0-pro"] = new(0.0005m, 0.0015m),
        ["gemini-pro"] = new(0.0005m, 0.0015m),
        
        // Meta Llama models (via various providers)
        ["llama-3.1-405b"] = new(0.005m, 0.015m),  // Approximate pricing via cloud providers
        ["llama-3.1-70b"] = new(0.0009m, 0.0009m),
        ["llama-3.1-8b"] = new(0.0002m, 0.0002m),
        ["llama-3-70b"] = new(0.0009m, 0.0009m),
        ["llama-3-8b"] = new(0.0002m, 0.0002m),
        
        // Mistral AI models
        ["mistral-large"] = new(0.002m, 0.006m),
        ["mistral-medium"] = new(0.0027m, 0.0081m),
        ["mistral-small"] = new(0.001m, 0.003m),
        ["mistral-7b"] = new(0.0002m, 0.0002m),
        ["mixtral-8x7b"] = new(0.0007m, 0.0007m),
        ["mixtral-8x22b"] = new(0.002m, 0.006m),
        
        // Cohere models
        ["command-r-plus"] = new(0.003m, 0.015m),
        ["command-r"] = new(0.0005m, 0.0015m),
        ["command"] = new(0.001m, 0.002m),
        ["command-light"] = new(0.0003m, 0.0006m),
        
        // Azure OpenAI Service (same pricing as OpenAI but with different deployment names)
        ["gpt-4o-deployment"] = new(0.0025m, 0.01m),  // Common Azure deployment name
        ["gpt-4o-mini-deployment"] = new(0.00015m, 0.0006m),
        ["gpt-35-turbo"] = new(0.0005m, 0.0015m),     // Azure naming convention
        
        // GitHub Models (GitHub-hosted versions)
        ["gpt-4o-github"] = new(0.0025m, 0.01m),  // GitHub Models naming
        ["claude-3-5-sonnet-github"] = new(0.003m, 0.015m),
    };
    
    /// <summary>Estimate cost for a request.</summary>
    public static decimal? EstimateCost(string? modelName, int inputTokens, int outputTokens)
    {
        var pricing = GetTokenPricing(modelName);
        return pricing is null ? null : TokenCostEstimate.Calculate(inputTokens, outputTokens, null, pricing).Amount;
    }
    
    /// <summary>Get pricing for a model.</summary>
    public static (decimal InputPer1K, decimal OutputPer1K, decimal InputPricePerMillion, decimal OutputPricePerMillion)? GetPricing(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return null;
        
        // ARC-07: exact-then-longest-substring match via the single shared matcher (BUG-11 fix lives there
        // now, shared with JudgeCostMap.GetRate so the rule cannot drift between the two tables).
        if (!ModelKeyMatcher.TryMatchLongestFirst(_pricing, modelName, out var price))
            return null;

        return (price.InputPer1K, price.OutputPer1K, price.InputPer1K * 1000, price.OutputPer1K * 1000);
    }
    
    /// <summary>Add or update model pricing.</summary>
    /// <param name="modelName">The model name (case-insensitive).</param>
    /// <param name="inputPer1K">Price per 1000 input tokens in USD.</param>
    /// <param name="outputPer1K">Price per 1000 output tokens in USD.</param>
    /// <exception cref="ArgumentNullException">Thrown when modelName is null.</exception>
    public static void SetPricing(string modelName, decimal inputPer1K, decimal outputPer1K)
    {
        _ = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _pricing.AddOrUpdate(
            modelName.ToLowerInvariant(),
            new TokenPricing(inputPer1K, outputPer1K),
            (_, _) => new TokenPricing(inputPer1K, outputPer1K));
    }
    
    /// <summary>Sets an immutable rate card, including an optional cached-input rate.</summary>
    public static void SetPricing(string modelName, TokenPricing pricing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(pricing);
        _pricing[modelName] = pricing;
    }

    /// <summary>Resolves the full rate card with the existing exact-then-longest match policy.</summary>
    public static TokenPricing? GetTokenPricing(string? modelName) =>
        ModelKeyMatcher.TryMatchLongestFirst(_pricing, modelName, out var pricing) ? pricing : null;

    /// <summary>Get all known model names.</summary>
    public static IEnumerable<string> KnownModels => _pricing.Keys;

    /// <summary>Captures the current rates and lookup order for a reproducible evaluation.</summary>
    public static ModelPricingSnapshot CaptureSnapshot() => new(_pricing.ToArray());
    
    /// <summary>
    /// Estimate token count from text length.
    /// Uses average of ~4 characters per token for English text.
    /// This is an approximation and actual token count varies by model tokenizer.
    /// </summary>
    /// <param name="text">The text to estimate tokens for.</param>
    /// <returns>Estimated token count.</returns>
    public static int EstimateTokensFromText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }
    
    /// <summary>
    /// Estimate tokens for a prompt/input.
    /// Accounts for system prompt overhead (typically adds ~100-200 tokens).
    /// </summary>
    public static int EstimatePromptTokens(string? systemPrompt, string? userInput)
    {
        var systemTokens = EstimateTokensFromText(systemPrompt);
        var inputTokens = EstimateTokensFromText(userInput);
        // Add overhead for chat format wrappers
        return systemTokens + inputTokens + 50;
    }
}
