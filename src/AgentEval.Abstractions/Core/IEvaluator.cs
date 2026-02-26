// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Provides AI-powered response evaluation.
/// </summary>
public interface IEvaluator
{
    /// <summary>
    /// Evaluate an agent response against criteria.
    /// </summary>
    /// <param name="input">The original input/prompt.</param>
    /// <param name="output">The agent's output.</param>
    /// <param name="criteria">Evaluation criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluation result.</returns>
    Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an AI-powered evaluation.
/// </summary>
public class EvaluationResult
{
    /// <summary>Overall score from 0 to 100.</summary>
    public int OverallScore { get; init; }
    
    /// <summary>Summary of the evaluation.</summary>
    public string Summary { get; init; } = "";
    
    /// <summary>Suggested improvements.</summary>
    public IReadOnlyList<string> Improvements { get; init; } = [];
    
    /// <summary>Individual criteria results.</summary>
    public IReadOnlyList<CriterionResult> CriteriaResults { get; init; } = [];
}

/// <summary>
/// Result for a single evaluation criterion.
/// </summary>
public class CriterionResult
{
    /// <summary>The criterion being evaluated.</summary>
    public string Criterion { get; init; } = "";
    
    /// <summary>Whether the criterion was met.</summary>
    public bool Met { get; init; }
    
    /// <summary>Explanation of the result.</summary>
    public string Explanation { get; init; } = "";
}
