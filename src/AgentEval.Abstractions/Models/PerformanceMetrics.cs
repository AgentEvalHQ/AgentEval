// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;

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
    
    /// <summary>Total tokens used.</summary>
    public int? TotalTokens => (PromptTokens ?? 0) + (CompletionTokens ?? 0);
    
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
            $"Duration: {TotalDuration.TotalMilliseconds:F0}ms"
        };
        
        if (TimeToFirstToken.HasValue)
            parts.Add($"TTFT: {TimeToFirstToken.Value.TotalMilliseconds:F0}ms");
        
        if (TotalTokens > 0)
            parts.Add($"Tokens: {TotalTokens}");
        
        if (EstimatedCost.HasValue)
            parts.Add($"Cost: ${EstimatedCost:F4}");
        
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
    private static readonly ConcurrentDictionary<string, (decimal InputPer1K, decimal OutputPer1K)> _pricing = 
        new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI models (GPT family)
        ["gpt-5"] = (0.005m, 0.02m),          // GPT-5 (estimated pricing)
        ["gpt-5-chat"] = (0.005m, 0.02m),     // GPT-5 chat variant
        ["gpt-5-mini"] = (0.0001m, 0.0004m),  // GPT-5 mini (placeholder)
        ["gpt-4o"] = (0.005m, 0.015m),
        ["gpt-4o-2024-11-20"] = (0.0025m, 0.01m),
        ["gpt-4o-mini"] = (0.00015m, 0.0006m),
        ["gpt-4o-mini-2024-07-18"] = (0.00015m, 0.0006m),
        ["gpt-4-turbo"] = (0.01m, 0.03m),
        ["gpt-4-turbo-2024-04-09"] = (0.01m, 0.03m),
        ["gpt-4"] = (0.03m, 0.06m),
        ["gpt-4-0613"] = (0.03m, 0.06m),
        ["gpt-3.5-turbo"] = (0.0005m, 0.0015m),
        ["gpt-3.5-turbo-0125"] = (0.0005m, 0.0015m),
        
        // OpenAI reasoning models (o-series)
        ["o1"] = (0.015m, 0.06m),
        ["o1-preview"] = (0.015m, 0.06m),
        ["o1-mini"] = (0.003m, 0.012m),
        ["o3-mini"] = (0.00165m, 0.0066m),
        
        // OpenAI embedding models
        ["text-embedding-3-small"] = (0.00002m, 0m),  // Embedding models have no output cost
        ["text-embedding-3-large"] = (0.00013m, 0m),
        ["text-embedding-ada-002"] = (0.0001m, 0m),
        
        // Anthropic Claude models
        ["claude-3-5-sonnet"] = (0.003m, 0.015m),
        ["claude-3-5-sonnet-20241022"] = (0.003m, 0.015m),
        ["claude-3-5-haiku"] = (0.00025m, 0.00125m),
        ["claude-3-opus"] = (0.015m, 0.075m),
        ["claude-3-sonnet"] = (0.003m, 0.015m),
        ["claude-3-haiku"] = (0.00025m, 0.00125m),
        ["claude-2.1"] = (0.008m, 0.024m),
        ["claude-2.0"] = (0.008m, 0.024m),
        
        // Google Gemini models
        ["gemini-1.5-pro"] = (0.00125m, 0.005m),
        ["gemini-1.5-pro-latest"] = (0.00125m, 0.005m),
        ["gemini-1.5-flash"] = (0.000075m, 0.0003m),
        ["gemini-1.0-pro"] = (0.0005m, 0.0015m),
        ["gemini-pro"] = (0.0005m, 0.0015m),
        
        // Meta Llama models (via various providers)
        ["llama-3.1-405b"] = (0.005m, 0.015m),  // Approximate pricing via cloud providers
        ["llama-3.1-70b"] = (0.0009m, 0.0009m),
        ["llama-3.1-8b"] = (0.0002m, 0.0002m),
        ["llama-3-70b"] = (0.0009m, 0.0009m),
        ["llama-3-8b"] = (0.0002m, 0.0002m),
        
        // Mistral AI models
        ["mistral-large"] = (0.002m, 0.006m),
        ["mistral-medium"] = (0.0027m, 0.0081m),
        ["mistral-small"] = (0.001m, 0.003m),
        ["mistral-7b"] = (0.0002m, 0.0002m),
        ["mixtral-8x7b"] = (0.0007m, 0.0007m),
        ["mixtral-8x22b"] = (0.002m, 0.006m),
        
        // Cohere models
        ["command-r-plus"] = (0.003m, 0.015m),
        ["command-r"] = (0.0005m, 0.0015m),
        ["command"] = (0.001m, 0.002m),
        ["command-light"] = (0.0003m, 0.0006m),
        
        // Azure OpenAI Service (same pricing as OpenAI but with different deployment names)
        ["gpt-4o-deployment"] = (0.005m, 0.015m),  // Common Azure deployment name
        ["gpt-4o-mini-deployment"] = (0.00015m, 0.0006m),
        ["gpt-35-turbo"] = (0.0005m, 0.0015m),     // Azure naming convention
        
        // GitHub Models (GitHub-hosted versions)
        ["gpt-4o-github"] = (0.005m, 0.015m),  // GitHub Models naming
        ["claude-3-5-sonnet-github"] = (0.003m, 0.015m),
    };
    
    /// <summary>Estimate cost for a request.</summary>
    public static decimal? EstimateCost(string? modelName, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrEmpty(modelName))
            return null;
        
        var pricing = GetPricing(modelName);
        if (pricing == null)
            return null;
        
        return (inputTokens / 1000m * pricing.Value.InputPer1K) + (outputTokens / 1000m * pricing.Value.OutputPer1K);
    }
    
    /// <summary>Get pricing for a model.</summary>
    public static (decimal InputPer1K, decimal OutputPer1K, decimal InputPricePerMillion, decimal OutputPricePerMillion)? GetPricing(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return null;
        
        // Try exact match first, then partial match
        if (!_pricing.TryGetValue(modelName, out var price))
        {
            var match = _pricing.Keys.FirstOrDefault(k => 
                modelName.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                return null;
            price = _pricing[match];
        }
        
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
            (inputPer1K, outputPer1K),
            (_, _) => (inputPer1K, outputPer1K));
    }
    
    /// <summary>Get all known model names.</summary>
    public static IEnumerable<string> KnownModels => _pricing.Keys;
    
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
