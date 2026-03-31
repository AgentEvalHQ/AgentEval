// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External;

/// <summary>
/// Scoring judge for an external benchmark.
/// Each benchmark may use different judge prompts, scoring scales, and tolerance rules.
/// </summary>
public interface IExternalBenchmarkJudge
{
    /// <summary>
    /// Judges an agent's response to a benchmark question.
    /// </summary>
    /// <param name="agentResponse">The agent's response text.</param>
    /// <param name="question">The benchmark question with gold answer and type metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Judgment result with binary correctness and optional raw score.</returns>
    Task<ExternalJudgmentResult> JudgeAsync(
        string agentResponse,
        ExternalBenchmarkQuestion question,
        CancellationToken ct = default);
}
