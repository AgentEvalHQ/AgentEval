// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>
/// Evaluates whether an AI response is factually grounded in the provided context.
/// <para>
/// Implemented as a <see cref="CompositeEval"/> of configured <see cref="AtomicLlmEval"/> dimensions.
/// The legacy constructor retains the following four dimensions; SupportFocusedV2 omits source coverage
/// and offers separately enabled, query-relative answer completeness:
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
/// The support-focused policy requires captured context and reports missing evidence as unmeasured.
/// The original constructor retains the legacy policy; use the options constructor to select SupportFocused.
/// </para>
/// <para>
/// Lineage: AgentEval's own criteria and reference prompt, modelled on the evaluator concept (name,
/// inputs and scoring dimensions) of Azure/azure-sdk-for-python
/// <c>sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_groundedness/</c>
/// (<c>groundedness_with_query.prompty</c> and <c>groundedness_without_query.prompty</c>).
/// A 2026-10-02 check found no upstream prompt text in the rubric file under
/// <c>Resources/Prompts/</c>, which the judge is sent as its system prompt.
/// </para>
/// </summary>
public sealed class GroundednessEval : IEvalDefinitionWrapper
{
    private readonly CompositeEval _inner;

    /// <inheritdoc/>
    public IEval DefinitionInner => _inner;

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
        : this(judge, judgeModel, passThreshold, GroundednessOptions.Legacy) { }

    /// <summary>Creates a groundedness instrument with explicit, immutable scoring options.</summary>
    /// <param name="judge">Judge used by active dimensions.</param>
    /// <param name="judgeModel">Configured judge identity for provenance and pricing.</param>
    /// <param name="passThreshold">Composite and dimension pass threshold in [0, 1].</param>
    /// <param name="options">Use SupportFocused for claim support without a source-coverage penalty.</param>
    public GroundednessEval(IEvaluator judge, string? judgeModel, double passThreshold, GroundednessOptions options)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var supportFocused = options.Policy == GroundednessPolicy.SupportFocusedV2;
        var promptId = supportFocused ? "agenteval.groundedness.v2" : "agenteval.groundedness.v1";
        var components = new List<EvalComponent>();

        void Add(string key, string name, GroundednessDimensionOptions dimension, string severity, params string[] criteria)
        {
            if (!dimension.Enabled) return;
            var eval = new AtomicLlmEval(judge, key, name, "rag", supportFocused ? "2.0.0" : "1.0.0",
                criteria, passThreshold, judgeModel, promptId, severity)
            {
                JudgeNeedsData = options.RequireContext ? JudgeInputData.Context : JudgeInputData.None
            };
            components.Add(new(eval, dimension.Weight, dimension.Required) { OnFailure = dimension.OnFailure });
        }

        Add("claim_support", "Claim Support", options.ClaimSupport, "medium",
            "Every factual claim in the response has direct supporting evidence in the provided context",
            "The response does not assert facts that cannot be derived from the context");
        Add("claim_contradicted", "Claim Contradicted", options.ClaimContradicted, "high",
            "No claims in the response are directly contradicted by information in the provided context",
            "The response does not state the opposite of what the context establishes");
        Add("citation_accuracy", "Citation Accuracy", options.CitationAccuracy, "medium",
            "When the response cites a source, passage, or quote, the citation accurately represents what the context says",
            options.RequireCitations
                ? "Every factual claim requiring evidence has an explicit, accurate citation; absent citations do not pass"
                : "No citations are fabricated or misattributed (responses with no citations trivially pass this criterion)");
        if (supportFocused)
            Add("answer_completeness", "Answer Completeness", options.Completeness, "medium",
                "The response supplies the information needed to answer the query, without requiring unrelated context to be reproduced",
                "A concise answer that fully resolves the query receives full credit even when the source contains additional facts");
        else
            Add("evidence_coverage", "Evidence Coverage", options.Completeness, "medium",
                "The response leverages a substantial portion of the key evidence available in the context",
                "The response does not ignore major context passages that are directly relevant to the query");

        _inner = new CompositeEval("groundedness", "Groundedness", "rag", supportFocused ? "2.0.0" : "1.1.0",
            components.AsReadOnly(), WeightedSumAggregation.Instance, passThreshold);
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
        _inner.EvaluateAsync(input, ct);
}
