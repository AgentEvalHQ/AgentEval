// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates whether an AI response is factually grounded in the provided context.
/// <para>
/// Implemented as a <see cref="CompositeEval"/> with four <see cref="AtomicLlmEval"/>
/// sub-dimensions (per findings-and-suggestions §4 and master analysis §5.3):
/// </para>
/// <list type="table">
///   <listheader><term>Sub-dimension</term><description>Weight</description></listheader>
///   <item><term><c>claim_support</c></term><description>0.30 — every factual claim has context support</description></item>
///   <item><term><c>claim_contradicted</c></term><description>0.25 — no claims are contradicted by context</description></item>
///   <item><term><c>citation_accuracy</c></term><description>0.20 — explicit citations are accurate</description></item>
///   <item><term><c>evidence_coverage</c></term><description>0.25 — available evidence is leveraged</description></item>
/// </list>
/// <para>
/// Splitting Foundry's monolithic Groundedness into four sub-dimensions enables precise
/// diagnosis: a low <c>claim_support</c> score indicates hallucination; a low
/// <c>evidence_coverage</c> score indicates under-utilisation of context.
/// </para>
/// <para>
/// <b>Input contract</b>: requires <see cref="EvalInput.Query"/>,
/// <see cref="EvalInput.Response"/>, and ideally <see cref="EvalInput.Context"/>.
/// If <see cref="EvalInput.Context"/> is absent, the judge will score based on the
/// response alone and will typically return low scores for grounded claims.
/// </para>
/// <para>
/// Lineage: AgentEval's own criteria and reference prompt, modelled on the evaluator concept (name,
/// inputs and scoring dimensions) of Azure/azure-sdk-for-python
/// <c>sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_groundedness/</c>
/// (<c>groundedness_with_query.prompty</c> and <c>groundedness_without_query.prompty</c>).
/// A 2026-10-02 check found no upstream prompt text in the reference prompt file under
/// <c>Resources/Prompts/</c>, which is not yet sent to the judge.
/// </para>
/// </summary>
public sealed class GroundednessEval : IEval
{
    private readonly CompositeEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Initialises a new <see cref="GroundednessEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score each groundedness dimension.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the composite passes. Defaults to 0.75.</param>
    public GroundednessEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.75)
    {
        ArgumentNullException.ThrowIfNull(judge);

        // Each sub-dimension uses the same prompt, differentiated by the {dimension} placeholder
        // embedded in the prompt at runtime via criteria descriptions (the judge interprets them).
        // Claim contradictions are zero-tolerance → severity=high; citation and coverage are medium.
        var components = new[]
        {
            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "claim_support",
                    name: "Claim Support",
                    category: "rag",
                    version: "1.0.0",
                    criteria: new[]
                    {
                        "Every factual claim in the response has direct supporting evidence in the provided context",
                        "The response does not assert facts that cannot be derived from the context",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.groundedness.v1",
                    failureSeverity: "medium"),
                Weight: 0.30) { OnFailure = ComponentFailureEffect.Fail },

            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "claim_contradicted",
                    name: "Claim Contradicted",
                    category: "rag",
                    version: "1.0.0",
                    criteria: new[]
                    {
                        "No claims in the response are directly contradicted by information in the provided context",
                        "The response does not state the opposite of what the context establishes",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.groundedness.v1",
                    // Contradictions are more severe — escalate to high
                    failureSeverity: "high"),
                Weight: 0.25) { OnFailure = ComponentFailureEffect.Fail },

            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "citation_accuracy",
                    name: "Citation Accuracy",
                    category: "rag",
                    version: "1.0.0",
                    criteria: new[]
                    {
                        "When the response cites a source, passage, or quote, the citation accurately represents what the context says",
                        "No citations are fabricated or misattributed (responses with no citations trivially pass this criterion)",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.groundedness.v1",
                    failureSeverity: "medium"),
                Weight: 0.20) { OnFailure = ComponentFailureEffect.Fail },

            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: judge,
                    key: "evidence_coverage",
                    name: "Evidence Coverage",
                    category: "rag",
                    version: "1.0.0",
                    criteria: new[]
                    {
                        "The response leverages a substantial portion of the key evidence available in the context",
                        "The response does not ignore major context passages that are directly relevant to the query",
                    },
                    passThreshold: passThreshold,
                    judgeModel: judgeModel,
                    promptId: "agenteval.groundedness.v1",
                    failureSeverity: "medium"),
                Weight: 0.25) { OnFailure = ComponentFailureEffect.Warn },
        };

        _inner = new CompositeEval(
            key: "groundedness",
            name: "Groundedness",
            category: "rag",
            version: "1.1.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: passThreshold);
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
