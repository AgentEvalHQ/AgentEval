// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.Evals.Agentic;

/// <summary>
/// Puts <see cref="EvalInput.GroundTruth"/> in front of the judge for the LLM evaluators whose rubric compares the
/// response with a reference answer. <c>AtomicLlmEval</c> sends the judge the query, the response and the context only,
/// so similarity and response completeness never saw a reference a caller supplied: the judge improvised a comparison
/// (a fabricated 0.98), and once it was sent its rubric, the missing-reference rule failed every input (#203, B12a).
/// </summary>
internal static class GroundTruthInput
{
    /// <summary>Whether the input carries a reference answer.</summary>
    public static bool Has(EvalInput input) => !string.IsNullOrWhiteSpace(input.GroundTruth);

    /// <summary>
    /// The input with its reference answer after the query, every other field kept (a <c>with</c> copy); unchanged when
    /// it has none.
    /// </summary>
    public static EvalInput Fold(EvalInput input) => Has(input)
        ? input with { Query = $"{input.Query}\n\nGround-truth reference answer:\n{input.GroundTruth}" }
        : input;
}
