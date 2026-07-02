// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Adapts a MAF <see cref="IAgentEvaluator"/> (e.g. an Azure AI Foundry <c>FoundryEvals</c> instance) so it
/// can be used as an AgentEval <see cref="IEval"/> — i.e. a <b>weighted leaf inside an AgentEval
/// <c>CompositeEval</c></b>. This is the inverse of <see cref="AgentEvaluatorExtensions.AsAgentEvaluator"/>
/// (which wraps an AgentEval/MEAI evaluator as a MAF <see cref="IAgentEvaluator"/>): it lets a provider's
/// evaluator become a first-class, weighted component of a hierarchical AgentEval benchmark.
/// </summary>
/// <remarks>
/// <para>
/// The leaf is provider-agnostic — it holds no reference to Foundry. Each call builds a single-item
/// <see cref="EvalItem"/> from the <see cref="EvalInput"/>, invokes the wrapped evaluator, and reuses
/// <see cref="MeaiToEvalResultBridge"/> to normalise the returned metrics into an AgentEval score, so the
/// scale matches how MAF results are rendered everywhere else in the repo.
/// </para>
/// <para>
/// COST NOTE: a <c>CompositeEval</c> evaluates once per <see cref="EvalInput"/>. If the wrapped evaluator is
/// a cloud/batch job (Foundry), each leaf makes one call per input — fine for a benchmark with a handful of
/// scenarios, but for large item counts prefer running the provider ONCE over the whole batch and composing
/// the results with <see cref="UnifiedEvalReport"/> instead (see the "Foundry alongside local" sample).
/// </para>
/// </remarks>
public sealed class AgentEvaluatorEvalLeaf : IEval
{
    private readonly IAgentEvaluator _inner;
    private readonly string? _judgeModel;
    private readonly double? _threshold;

    /// <param name="inner">The MAF evaluator to wrap (e.g. a <c>FoundryEvals</c> configured with one evaluator).</param>
    /// <param name="key">Machine id for the leaf (e.g. <c>"foundry.relevance"</c>).</param>
    /// <param name="name">Human-readable name (e.g. <c>"Foundry Relevance"</c>).</param>
    /// <param name="category">Eval category (default <c>"hybrid"</c>).</param>
    /// <param name="version">Semver string (default <c>"1.0.0"</c>).</param>
    /// <param name="judgeModel">Recorded on the result's provenance; the provider's judge model, if known.</param>
    /// <param name="threshold">Pass threshold recorded on the score (informational; default 0.70).</param>
    public AgentEvaluatorEvalLeaf(
        IAgentEvaluator inner,
        string key,
        string name,
        string category = "hybrid",
        string version = "1.0.0",
        string? judgeModel = null,
        double? threshold = 0.70)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Category = category;
        Version = version;
        _judgeModel = judgeModel;
        _threshold = threshold;
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Category { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var item = new EvalItem(input.Query, input.Response ?? string.Empty);
        if (input.Context is not null) item.Context = input.Context;
        if (input.GroundTruth is not null) item.ExpectedOutput = input.GroundTruth;

        AgentEvaluationResults results;
        try
        {
            results = await _inner.EvaluateAsync(new[] { item }, Key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested is false)
        {
            // Isolate a provider failure into a failing leaf — never take down the whole composite.
            return Fail($"evaluator '{Name}' threw: {ex.Message}");
        }

        if (results.Error is not null || results.Items.Count == 0)
            return Fail(results.Error ?? "no results returned");

        // Reuse the existing MEAI -> EvalResult bridge for robust metric normalisation, then re-key its
        // aggregate as THIS leaf so it participates (by weight) in the parent CompositeEval.
        EvalResult tree = MeaiToEvalResultBridge.Build(Name, new[] { input.Query }, results, _judgeModel);
        EvalScore agg = tree.Score;

        return new EvalResult(
            Metric: new EvalMetadata(Key, Name, Category, Version),
            Score: new EvalScore(
                Value: agg.Value, Ordinal: null, Label: agg.Label, Passed: agg.Passed,
                Threshold: _threshold, Severity: agg.Severity, Confidence: null),
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: new[] { new EvalEvidence("provider", Key, $"{results.Passed}/{results.Total} metric(s) passed") },
                Recommendations: null,
                // Keep the provider's per-metric breakdown as a sub-tree so the hierarchy shows depth.
                SubResults: tree.Details.SubResults,
                AggregationStrategy: tree.Details.AggregationStrategy),
            Provenance: new EvalProvenance(
                Type: "atomic-llm", JudgeModel: _judgeModel, PromptId: null, PromptHash: null,
                TokensUsed: null, EstimatedCost: 0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private EvalResult Fail(string reason) => new(
        Metric: new EvalMetadata(Key, Name, Category, Version),
        Score: new EvalScore(0.0, Ordinal: null, Label: "fail", Passed: false, Threshold: _threshold, Severity: "medium", Confidence: null),
        Details: new EvalDetails(null, new[] { new EvalEvidence("provider", Key, reason) }, null, null, null),
        Provenance: new EvalProvenance("atomic-llm", _judgeModel, null, null, null, 0, false),
        EvaluatedAt: DateTimeOffset.UtcNow);
}
