using AgentEval.Core;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Interface for running comprehensive memory benchmark suites against agents.
/// Executes multiple scenarios across categories and produces a holistic memory quality score.
/// </summary>
public interface IMemoryBenchmarkRunner
{
    /// <summary>
    /// Runs a memory benchmark suite against an agent.
    /// </summary>
    /// <param name="agent">The agent to benchmark</param>
    /// <param name="benchmark">The benchmark preset to run (Quick, Standard, or Full)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Comprehensive benchmark result with per-category scores</returns>
    Task<MemoryBenchmarkResult> RunBenchmarkAsync(
        IEvaluableAgent agent,
        MemoryBenchmark benchmark,
        CancellationToken cancellationToken = default);
}
