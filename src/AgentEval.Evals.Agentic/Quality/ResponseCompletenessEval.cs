// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates whether an AI response covers all information the user would reasonably expect
/// given the query and available context.
/// <para>
/// Wraps an <see cref="AtomicLlmEval"/> configured with three response-completeness criteria;
/// each criterion's verdict and explanation is surfaced via <see cref="EvalDetails"/> evidence.
/// The critical/optional fact classification, its 0.80 / 0.20 weighting and the
/// <c>missing_facts[]</c> array are specified in the rubric the judge is sent; the result keeps the score,
/// criteria and evidence, not the <c>missing_facts[]</c> array.
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/> and
/// <see cref="EvalInput.Response"/>. <see cref="EvalInput.Context"/> and
/// <see cref="EvalInput.GroundTruth"/> are optional but strongly recommended — they
/// allow the judge to enumerate expected facts precisely. Without context, the judge
/// infers expected facts from the query alone, which may undercount expectations.
/// </para>
/// <para>
/// Lineage: AgentEval's own criteria and reference prompt, modelled on the evaluator concept (name,
/// inputs and scoring dimensions) of Azure/azure-sdk-for-python
/// <c>sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_response_completeness/response_completeness.prompty</c>.
/// A 2026-10-02 check found no upstream prompt text in the rubric file under
/// <c>Resources/Prompts/</c>, which the judge is sent as its system prompt.
/// </para>
/// </summary>
public sealed class ResponseCompletenessEval : IEval
{
    private readonly AtomicLlmEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Initialises a new <see cref="ResponseCompletenessEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score completeness.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public ResponseCompletenessEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "response_completeness",
            name: "Response Completeness",
            category: "rag",
            version: "1.1.0",
            criteria: new[]
            {
                "All critical facts expected from the query are covered in the response",
                "The response does not ignore major relevant context passages directly pertaining to the query",
                "The response is complete enough for the user to act on or understand the answer without significant gaps",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.response_completeness.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
