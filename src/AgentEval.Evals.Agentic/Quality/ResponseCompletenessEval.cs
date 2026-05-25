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
/// Wraps an <see cref="AtomicLlmEval"/> configured with the response-completeness rubric.
/// The prompt instructs the judge to enumerate expected facts, classify each as
/// <c>critical</c> or <c>optional</c>, and compute a weighted score
/// (critical gaps weighted at 0.80, optional gaps at 0.20). The <c>missing_facts[]</c>
/// array in the judge output is surfaced via <see cref="EvalDetails"/> evidence
/// for diagnosability.
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/> and
/// <see cref="EvalInput.Response"/>. <see cref="EvalInput.Context"/> and
/// <see cref="EvalInput.GroundTruth"/> are optional but strongly recommended — they
/// allow the judge to enumerate expected facts precisely. Without context, the judge
/// infers expected facts from the query alone, which may undercount expectations.
/// </para>
/// <para>
/// Source: forked from Azure/azure-sdk-for-python (commit <TBD-foundry-sha> see CHANGELOG T3.7)
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_response_completeness/response_completeness.prompty
/// License: MIT. Modifications: temperature=0, critical/optional gap classification,
/// missing_facts[] array, structured evidence[], label table, severity=medium.
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
            version: "1.0.0",
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
