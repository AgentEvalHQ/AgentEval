namespace AgentEval.Memory.Models;

/// <summary>
/// Result of a reach-back depth evaluation, containing per-depth scores and degradation analysis.
/// </summary>
public class ReachBackResult
{
    /// <summary>
    /// The fact that was tested for recall at various depths.
    /// </summary>
    public required MemoryFact Fact { get; init; }

    /// <summary>
    /// Individual results for each noise depth tested.
    /// </summary>
    public required IReadOnlyList<DepthResult> DepthResults { get; init; }

    /// <summary>
    /// Maximum depth where the agent reliably recalled the fact (score >= 80).
    /// </summary>
    public int MaxReliableDepth => DepthResults
        .Where(d => d.Score >= 80)
        .Select(d => d.Depth)
        .DefaultIfEmpty(0)
        .Max();

    /// <summary>
    /// First depth where the agent failed to recall the fact (score under 80).
    /// Returns null if the agent succeeded at all tested depths.
    /// </summary>
    public int? FailurePoint => DepthResults
        .Where(d => d.Score < 80)
        .Select(d => (int?)d.Depth)
        .FirstOrDefault();

    /// <summary>
    /// Overall score (0-100) based on the proportion of depths that passed.
    /// </summary>
    public double OverallScore => DepthResults.Count > 0
        ? DepthResults.Average(d => d.Score)
        : 0;

    /// <summary>
    /// Whether the evaluation passed (max reliable depth meets minimum threshold).
    /// </summary>
    public bool Passed => MaxReliableDepth > 0;

    /// <summary>
    /// Total execution time for the complete reach-back evaluation.
    /// </summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Result of recall at a specific noise depth.
/// </summary>
public class DepthResult
{
    /// <summary>
    /// Number of noise turns between the fact and the query.
    /// </summary>
    public required int Depth { get; init; }

    /// <summary>
    /// Score (0-100) for recall at this depth.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Whether the fact was recalled at this depth (score >= 80).
    /// </summary>
    public bool Recalled => Score >= 80;

    /// <summary>
    /// The agent's response to the query at this depth.
    /// </summary>
    public required string Response { get; init; }

    /// <summary>
    /// Execution time for this specific depth test.
    /// </summary>
    public required TimeSpan Duration { get; init; }
}
