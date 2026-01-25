// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Comparison;

/// <summary>
/// Factory for creating testable agents with a specific model.
/// Enables model swapping without changing agent behavior.
/// </summary>
/// <remarks>
/// Use this interface when comparing the same agent across different models.
/// Each factory creates agents configured for a specific model deployment.
/// </remarks>
/// <example>
/// <code>
/// var factory = new AzureOpenAIAgentFactory(client, "gpt-4o", "GPT-4o", agentOptions);
/// var agent = factory.CreateAgent();
/// </code>
/// </example>
public interface IAgentFactory
{
    /// <summary>
    /// Unique identifier for this model configuration.
    /// Used for result grouping and reporting.
    /// </summary>
    string ModelId { get; }
    
    /// <summary>
    /// Human-readable name for display in reports.
    /// </summary>
    /// <example>"GPT-4o", "Claude 3.5 Sonnet", "GPT-4o Mini"</example>
    string ModelName { get; }
    
    /// <summary>
    /// Create a fresh agent instance for testing.
    /// Each call should return a new instance to ensure test isolation.
    /// </summary>
    /// <returns>A new testable agent instance.</returns>
    IEvaluableAgent CreateAgent();
    
    /// <summary>
    /// Optional model-specific configuration.
    /// </summary>
    ModelConfiguration? Configuration { get; }
}

/// <summary>
/// Configuration for a model in comparison.
/// </summary>
public record ModelConfiguration
{
    /// <summary>Deployment name (e.g., Azure OpenAI deployment).</summary>
    public string? DeploymentName { get; init; }
    
    /// <summary>Temperature setting for generation.</summary>
    public double? Temperature { get; init; }
    
    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }
    
    /// <summary>Random seed for reproducibility.</summary>
    public int? Seed { get; init; }
    
    /// <summary>Additional provider-specific properties.</summary>
    public IDictionary<string, object>? AdditionalProperties { get; init; }
}
