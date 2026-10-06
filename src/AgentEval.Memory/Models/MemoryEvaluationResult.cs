// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Models;

/// <summary>
/// Result of a memory evaluation, containing scores, found/missing facts, and performance metrics.
/// </summary>
public class MemoryEvaluationResult
{
    /// <summary>
    /// Overall memory score (0-100) for the entire evaluation.
    /// </summary>
    public required double OverallScore { get; init; }

    /// <summary>
    /// Results for each individual query in the scenario.
    /// </summary>
    public required IReadOnlyList<MemoryQueryResult> QueryResults { get; init; }

    /// <summary>
    /// Facts that were successfully recalled across all queries.
    /// </summary>
    public required IReadOnlyList<MemoryFact> FoundFacts { get; init; }

    /// <summary>
    /// Facts that were expected but not found in responses.
    /// </summary>
    public required IReadOnlyList<MemoryFact> MissingFacts { get; init; }

    /// <summary>
    /// Forbidden facts that were incorrectly mentioned in responses.
    /// </summary>
    public required IReadOnlyList<MemoryFact> ForbiddenFound { get; init; }

    /// <summary>
    /// Total execution time for the memory evaluation.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of LLM tokens used during evaluation.
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Estimated cost of the evaluation (in USD).
    /// </summary>
    public decimal EstimatedCost { get; init; }

    /// <summary>
    /// Name of the scenario that was evaluated.
    /// </summary>
    public required string ScenarioName { get; init; }

    /// <summary>
    /// Timestamp when this evaluation was performed.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional metadata for additional context.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Memory retention rate as a percentage (0-100), over the measured queries.
    /// </summary>
    public double RetentionRate => MeasuredQueries > 0 ? QueryResults.Where(r => r.Measured).Average(r => r.Score) : 0;

    /// <summary>
    /// Number of queries that passed their minimum score threshold.
    /// </summary>
    public int PassedQueries => QueryResults.Count(r => r.Passed);

    /// <summary>
    /// Total number of queries evaluated.
    /// </summary>
    public int TotalQueries => QueryResults.Count;

    /// <summary>Queries the judge scored.</summary>
    public int MeasuredQueries => QueryResults.Count(r => r.Measured);

    /// <summary>
    /// Queries the judge produced no score for (<see cref="MemoryQueryResult.Measured"/>). They are left out of every
    /// score and rate here, and reported.
    /// </summary>
    public int UnmeasuredQueries => TotalQueries - MeasuredQueries;

    /// <summary>True when at least one query was measured, so <see cref="OverallScore"/> means something.</summary>
    public bool IsMeasured => MeasuredQueries > 0;

    /// <summary>
    /// Success rate as a percentage of the measured queries that passed.
    /// </summary>
    public double SuccessRate => MeasuredQueries > 0 ? (double)PassedQueries / MeasuredQueries * 100 : 0;

    public override string ToString() => $"{ScenarioName}: {OverallScore:F1}% ({PassedQueries}/{TotalQueries} queries passed)";
}

/// <summary>
/// Result of evaluating a single memory query.
/// </summary>
public class MemoryQueryResult
{
    /// <summary>
    /// The query that was evaluated.
    /// </summary>
    public required MemoryQuery Query { get; init; }

    /// <summary>
    /// The agent's response to the query.
    /// </summary>
    public required string Response { get; init; }

    /// <summary>
    /// Score for this query (0-100).
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// <see langword="false"/> when the judge produced no score (<see cref="AgentEval.Memory.Engine.MemoryJudgmentResult.Measured"/>);
    /// <see cref="Score"/> is then a placeholder 0, not a measurement.
    /// </summary>
    public bool Measured { get; init; } = true;

    /// <summary>
    /// Whether this query passed its minimum score threshold. An unmeasured query never passes.
    /// </summary>
    public bool Passed => Measured && Score >= Query.MinimumScore;

    /// <summary>
    /// Facts that were found in the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> FoundFacts { get; init; }

    /// <summary>
    /// Expected facts that were missing from the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> MissingFacts { get; init; }

    /// <summary>
    /// Forbidden facts that were incorrectly found in the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> ForbiddenFound { get; init; }

    /// <summary>
    /// Detailed explanation of the scoring decision.
    /// </summary>
    public string? Explanation { get; init; }

    /// <summary>
    /// Time taken to process this query.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Tokens used for this query evaluation.
    /// </summary>
    public int TokensUsed { get; init; }

    public override string ToString() => $"{Query.Question}: {Score:F1}% ({(Passed ? "PASS" : "FAIL")})"; 
}