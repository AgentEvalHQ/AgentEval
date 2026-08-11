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

    /// <summary>
    /// Judges a response with explicit judge options.
    /// </summary>
    /// <remarks>
    /// Supplied as a default implementation that discards <paramref name="options"/> and forwards to the
    /// three-argument overload, so existing implementers keep compiling and keep their current
    /// behaviour — they never received options before either. Implementations that honour options, such
    /// as <see cref="LongMemEval.LongMemEvalJudge"/>, provide their own.
    /// </remarks>
    /// <param name="agentResponse">The agent's response text.</param>
    /// <param name="question">The benchmark question with gold answer and type metadata.</param>
    /// <param name="options">Judge configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ExternalJudgmentResult> JudgeAsync(
        string agentResponse,
        ExternalBenchmarkQuestion question,
        ExternalBenchmarkOptions options,
        CancellationToken ct = default)
        => JudgeAsync(agentResponse, question, ct);
}
