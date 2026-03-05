using AgentEval.Memory.Models;

namespace AgentEval.Memory.Engine;

/// <summary>
/// Result of memory judgment/scoring.
/// </summary>
public class MemoryJudgmentResult
{
    /// <summary>
    /// Score (0-100) for how well the response demonstrates memory of expected facts.
    /// </summary>
    public required double Score { get; init; }
    
    /// <summary>
    /// Expected facts that were found in the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> FoundFacts { get; init; }
    
    /// <summary>
    /// Expected facts that were missing from the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> MissingFacts { get; init; }
    
    /// <summary>
    /// Forbidden facts that were incorrectly present in the response.
    /// </summary>
    public required IReadOnlyList<MemoryFact> ForbiddenFound { get; init; }
    
    /// <summary>
    /// Detailed explanation of the scoring decision.
    /// </summary>
    public string? Explanation { get; init; }
    
    /// <summary>
    /// Number of tokens used for this judgment.
    /// </summary>
    public int TokensUsed { get; init; }
}