// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Core;

/// <summary>
/// Immutable, preformatted evidence for a configured judge. These values are data, never system instructions.
/// Null means unavailable or not selected; an empty recorded collection is represented explicitly by its producer.
/// </summary>
public sealed record EvaluationEvidence
{
    /// <summary>Ordered tool calls, arguments, results and recorded outcomes.</summary>
    public string? ToolCalls { get; init; }
    /// <summary>The tools offered to the evaluated agent.</summary>
    public string? ToolDefinitions { get; init; }
    /// <summary>The evaluated agent's instructions, not instructions to the judge.</summary>
    public string? SystemMessage { get; init; }
    /// <summary>The actions expected by the case author.</summary>
    public string? ExpectedActions { get; init; }
    /// <summary>Observations supplied by a deterministic evaluator.</summary>
    public string? EvaluatorNotes { get; init; }
    /// <summary>The policy against which the evaluated agent is judged.</summary>
    public string? EvaluatorPolicy { get; init; }
}
