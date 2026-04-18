// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using AgentEval.Core;
using AgentEval.Comparison;

namespace AgentEval.MAF;

/// <summary>
/// Adapts a Microsoft Agent Framework (MAF) AIAgent for testing with AgentEval,
/// with support for model identification for comparison scenarios.
/// </summary>
/// <remarks>
/// Inherits all adapter behavior from <see cref="MAFAgentAdapter"/> (session management,
/// history injection, streaming, token extraction) and adds <see cref="IModelIdentifiable"/>
/// support by tagging responses with the configured <see cref="ModelId"/>.
/// </remarks>
public class MAFIdentifiableAgentAdapter : MAFAgentAdapter, IModelIdentifiable
{
    /// <summary>
    /// Create an adapter for an AIAgent with model identification.
    /// </summary>
    /// <param name="agent">The MAF agent to adapt.</param>
    /// <param name="modelId">Unique identifier for the model (e.g., "gpt-4o-2024-08-06").</param>
    /// <param name="modelDisplayName">Human-readable model name (e.g., "GPT-4o").</param>
    /// <param name="session">Optional session for conversation context.</param>
    public MAFIdentifiableAgentAdapter(
        AIAgent agent, 
        string modelId,
        string modelDisplayName,
        AgentSession? session = null)
        : base(agent, session)
    {
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        ModelDisplayName = modelDisplayName ?? throw new ArgumentNullException(nameof(modelDisplayName));
    }
    
    /// <inheritdoc/>
    public string ModelId { get; }
    
    /// <inheritdoc/>
    public string ModelDisplayName { get; }
    
    /// <summary>
    /// Tags all responses with the configured <see cref="ModelId"/>.
    /// </summary>
    protected override string? ResponseModelId => ModelId;
}
