// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;
using MafIAgentEvaluator = Microsoft.Agents.AI.IAgentEvaluator;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Fluent helpers for running AgentEval evaluations through Microsoft Agent Framework's
/// <c>agent.EvaluateAsync(...)</c> feature.
/// </summary>
public static class AgentEvaluatorExtensions
{
    /// <summary>
    /// Wraps an AgentEval (MEAI) <see cref="MEAIIEvaluator"/> as MAF's native
    /// <see cref="Microsoft.Agents.AI.IAgentEvaluator"/> so it can be run with
    /// <c>agent.EvaluateAsync(queries, evaluator)</c>. Forwards the full
    /// <see cref="Microsoft.Agents.AI.EvalItem.Conversation"/>, so code-based tool metrics see the
    /// real tool calls. See <see cref="AgentEvalAgentEvaluator"/> for the rationale.
    /// </summary>
    public static AgentEvalAgentEvaluator AsAgentEvaluator(
        this MEAIIEvaluator evaluator,
        ChatConfiguration chatConfiguration,
        string? name = null) => new(evaluator, chatConfiguration, name);

    /// <summary>
    /// Wraps an AgentEval composite (<see cref="IEval"/> — e.g. an <c>AgenticBenchmark</c> preset) as
    /// an MEAI <see cref="MEAIIEvaluator"/> so the whole weighted tree can run as one evaluator. The
    /// rich <see cref="EvalResult"/> tree is available on
    /// <see cref="AgentEvalCompositeEvaluator.CapturedResults"/> for rendering.
    /// </summary>
    public static AgentEvalCompositeEvaluator AsMeaiEvaluator(this IEval composite) => new(composite);

    /// <summary>
    /// Wraps a MAF <see cref="MafIAgentEvaluator"/> (e.g. an Azure AI Foundry <c>FoundryEvals</c> instance)
    /// as an AgentEval <see cref="IEval"/> leaf, so a provider's evaluator can be a <b>weighted component
    /// inside an AgentEval <c>CompositeEval</c></b> — the inverse of <see cref="AsAgentEvaluator"/>. See
    /// <see cref="AgentEvaluatorEvalLeaf"/> for the per-input cost note (prefer batching for large runs).
    /// </summary>
    public static IEval AsEvalLeaf(
        this MafIAgentEvaluator evaluator,
        string key,
        string name,
        string category = "hybrid",
        string version = "1.0.0",
        string? judgeModel = null,
        double? threshold = 0.70) => new AgentEvaluatorEvalLeaf(evaluator, key, name, category, version, judgeModel, threshold);
}
