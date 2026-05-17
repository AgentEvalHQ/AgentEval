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
    private readonly string? _failureSeverity;
    private readonly Func<string?, JudgeCostMap.ModelRate>? _rateResolver;

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
    /// <param name="failureSeverity">
    /// When set, overrides the severity used in the result when the eval fails.
    /// This propagates the article-level severity (e.g. "critical") from YAML metadata
    /// into the eval result, enabling severity-aware aggregation strategies such as
    /// <see cref="CapByWorstAggregation"/> to identify critical-article failures.
    /// When <c>null</c> (default), severity is computed from score: &lt;0.40 → "high", else "medium".
    /// </param>
    /// <param name="rateResolver">
    /// Optional per-instance cost-rate resolver. When supplied, takes precedence over the
    /// static <see cref="JudgeCostMap"/> for this eval's cost computation — useful when a
    /// tenant has a negotiated rate that differs from the public list price, when running
    /// against a regional Azure deployment with regional pricing, or when overriding cost
    /// in a test fixture. The delegate receives the judge model identifier
    /// (<paramref name="judgeModel"/>) and returns a <see cref="JudgeCostMap.ModelRate"/>.
    /// When <c>null</c> (default), falls back to <see cref="JudgeCostMap.GetRate(string?)"/>.
    /// </param>
    public AtomicLlmEval(
        AgentEval.Core.IEvaluator evaluator,
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<string> criteria,
        double passThreshold = 0.70,
        string? judgeModel = null,
        string? promptId = null,
        string? failureSeverity = null,
        Func<string?, JudgeCostMap.ModelRate>? rateResolver = null)
        : base(key, name, category, version)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        if (!double.IsFinite(passThreshold) || passThreshold < 0.0 || passThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(passThreshold), passThreshold, "passThreshold must be a finite value in [0, 1].");
        _passThreshold = passThreshold;
        _judgeModel = judgeModel;
        _promptId = promptId;
        _failureSeverity = failureSeverity;
        _rateResolver = rateResolver;
    }

    /// <inheritdoc/>
    public override async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Response is null)
            throw new InvalidOperationException("AtomicLlmEval requires EvalInput.Response to be set.");

        var er = await _evaluator.EvaluateAsync(input.Query, input.Response, _criteria, ct);

        // v1.1 task 1.7 / 6-plan F-002: populate EstimatedCost from real judge token usage
        // so composite cost rollups stop summing to $0. When the underlying IEvaluator does
        // not report token counts (e.g. test fakes, non-chat evaluators), tokensUsed stays
        // null and estimatedCost stays 0 — matching the pre-1.7 behaviour for those paths.
        long? inputTokens = er.InputTokenCount;
        long? outputTokens = er.OutputTokenCount;
        int? tokensUsed = null;
        double estimatedCost = 0;
        if (inputTokens is not null || outputTokens is not null)
        {
            var inT = inputTokens ?? 0;
            var outT = outputTokens ?? 0;
            if (_rateResolver is not null)
            {
                // LR7-F1: per-instance resolver wins over the static JudgeCostMap.
                // Lets a tenant override list price with a negotiated rate without
                // forking or mutating the process-global rate table.
                var rate = _rateResolver(_judgeModel);
                if (inT > 0 || outT > 0)
                    estimatedCost = (Math.Max(0, inT) / 1000.0) * rate.InputRatePer1K
                                  + (Math.Max(0, outT) / 1000.0) * rate.OutputRatePer1K;
            }
            else
            {
                estimatedCost = JudgeCostMap.EstimateCost(_judgeModel, inT, outT);
            }
            // EvalProvenance.TokensUsed is the legacy single-int totals field; sum the two
            // for backwards-compatible reporting. We clamp to int.MaxValue defensively even
            // though realistic judge transcripts are nowhere near that volume.
            var total = inT + outT;
            tokensUsed = total > int.MaxValue ? int.MaxValue : (int)total;
        }

        var value = Math.Clamp(er.OverallScore / 100.0, 0.0, 1.0);
        var passed = value >= _passThreshold;
        // Compute severity from score; then take the maximum with failureSeverity (if set)
        // so that critical/high article metadata severity is surfaced without downgrading
        // score-derived severity for lower-severity articles.
        var scoreSeverity = value < 0.40 ? "high" : "medium";
        var severity = passed
            ? "none"
            : (_failureSeverity is null
                ? scoreSeverity
                : SeverityRollup.Max([scoreSeverity, _failureSeverity]));
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
                TokensUsed: tokensUsed,
                EstimatedCost: estimatedCost,
                CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
