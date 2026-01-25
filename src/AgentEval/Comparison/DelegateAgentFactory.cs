// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Comparison;

/// <summary>
/// Generic factory using a delegate for agent creation.
/// Use when you don't need a custom factory class.
/// </summary>
/// <example>
/// <code>
/// var factory = new DelegateAgentFactory(
///     "gpt-4o",
///     "GPT-4o",
///     () => new MAFAgentAdapter(CreateMyAgent("gpt-4o"))
/// );
/// </code>
/// </example>
public class DelegateAgentFactory : IAgentFactory
{
    private readonly Func<IEvaluableAgent> _createAgent;
    
    /// <summary>
    /// Creates a factory using a delegate for agent creation.
    /// </summary>
    /// <param name="modelId">Unique identifier for this model.</param>
    /// <param name="modelName">Human-readable display name.</param>
    /// <param name="createAgent">Delegate that creates agent instances.</param>
    /// <param name="configuration">Optional model configuration.</param>
    public DelegateAgentFactory(
        string modelId,
        string modelName,
        Func<IEvaluableAgent> createAgent,
        ModelConfiguration? configuration = null)
    {
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        ModelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _createAgent = createAgent ?? throw new ArgumentNullException(nameof(createAgent));
        Configuration = configuration;
    }
    
    /// <inheritdoc/>
    public string ModelId { get; }
    
    /// <inheritdoc/>
    public string ModelName { get; }
    
    /// <inheritdoc/>
    public ModelConfiguration? Configuration { get; }
    
    /// <inheritdoc/>
    public IEvaluableAgent CreateAgent() => _createAgent();
}
