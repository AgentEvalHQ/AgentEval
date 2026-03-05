using AgentEval.Memory.Models;

namespace AgentEval.Memory.Engine;

/// <summary>
/// Interface for LLM-based evaluation of memory test responses.
/// Provides structured assessment of memory quality and accuracy.
/// </summary>
public interface IMemoryJudge
{
    /// <summary>
    /// Evaluates whether expected facts are present in an agent's response.
    /// </summary>
    /// <param name="response">The agent's response to analyze</param>
    /// <param name="query">The memory query with expected/forbidden facts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Judgment result with score and fact analysis</returns>
    Task<MemoryJudgmentResult> JudgeAsync(
        string response, 
        MemoryQuery query, 
        CancellationToken cancellationToken = default);
}