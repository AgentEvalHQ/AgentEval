// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Atomic eval that delegates scoring to an <see cref="AgentEval.Core.IEvaluator"/> (LLM judge).
/// </summary>
public sealed class AtomicLlmEval : AtomicEval
{
    private readonly AgentEval.Core.IEvaluator _evaluator;
    private readonly IReadOnlyList<string> _criteria;
    private readonly string? _judgeModel;
    private readonly string? _promptId;
    private readonly double _passThreshold;

    /// <summary>
    /// Initialises a new <see cref="AtomicLlmEval"/>.
    /// </summary>
    /// <param name="evaluator">The LLM evaluator to delegate to.</param>
    /// <param name="key">Machine-readable eval key.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="category">Eval category.</param>
    /// <param name="version">Semver-style version string.</param>
    /// <param name="criteria">Evaluation criteria passed to the judge.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="promptId">Optional prompt identifier recorded in provenance.</param>
    public AtomicLlmEval(
        AgentEval.Core.IEvaluator evaluator,
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<string> criteria,
        double passThreshold = 0.70,
        string? judgeModel = null,
        string? promptId = null)
        : base(key, name, category, version)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        _passThreshold = passThreshold;
        _judgeModel = judgeModel;
        _promptId = promptId;
    }

    /// <inheritdoc/>
    public override async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Response is null)
            throw new InvalidOperationException("AtomicLlmEval requires EvalInput.Response to be set.");

        var er = await _evaluator.EvaluateAsync(input.Query, input.Response, _criteria, ct);

        var value = Math.Clamp(er.OverallScore / 100.0, 0.0, 1.0);
        var passed = value >= _passThreshold;
        var severity = passed ? "none" : (value < 0.40 ? "high" : "medium");
        var label = passed ? "pass" : "fail";

        var dimensions = er.CriteriaResults?
            .GroupBy(c => c.Criterion)
            .ToDictionary(g => g.Key, g => g.Last().Met ? 1.0 : 0.0)
            ?? new Dictionary<string, double>();

        var evidence = er.CriteriaResults?
            .Where(c => !string.IsNullOrEmpty(c.Explanation))
            .Select(c => new EvalEvidence(Source: "criterion", Reference: c.Criterion, Message: c.Explanation))
            .ToList()
            ?? new List<EvalEvidence>();

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(value, null, label, passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: dimensions.Count > 0 ? dimensions : null,
                Evidence: evidence.Count > 0 ? evidence : null,
                Recommendations: er.Improvements?.Count > 0 ? er.Improvements : null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new(
                Type: "atomic-llm",
                JudgeModel: _judgeModel,
                PromptId: _promptId,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: 0,
                CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
