// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Interface for agents that can identify which model they are using.
/// This is an optional extension to IEvaluableAgent for model comparison scenarios.
/// </summary>
public interface IModelIdentifiable
{
    /// <summary>
    /// Unique identifier for the model being used.
    /// </summary>
    /// <example>"gpt-4o-2024-08-06" or "gpt-4o-mini"</example>
    string ModelId { get; }
    
    /// <summary>
    /// Human-readable display name for the model.
    /// </summary>
    /// <example>"GPT-4o" or "GPT-4o Mini"</example>
    string ModelDisplayName { get; }
}
