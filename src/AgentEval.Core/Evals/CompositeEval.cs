// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Aggregates multiple component evals (atomic or nested composites) into a single scored result.
/// </summary>
public sealed class CompositeEval : IEval
{
    /// <summary>
    /// Producer-side recursion-depth cap matching Mission Control's
    /// <c>MaxTreeWalkDepth=32</c>. Phase-7 Task 7.5: a deeply-nested composite
    /// configuration (root → pillar → article → sub-article → … past 32 levels)
    /// would stack-overflow the resolver thread on the consumer side. Fail at
    /// the producer instead so the bug surfaces during the bench run with a
    /// clear diagnostic, not as a hard crash during PDF rendering.
    /// </summary>
    internal const int MaxNestingDepth = AgentEval.Evals.EvalTreeLimits.MaxTreeWalkDepth; // ARC-03: one source of truth

    /// <summary>
    /// AsyncLocal depth counter — increments on each <see cref="EvaluateAsync"/>
    /// entry, decrements on exit. AsyncLocal flows naturally through awaits so
    /// the depth tracks the true composite-tree nesting even with parallel
    /// fan-out (each child task sees the same logical-call depth).
    /// </summary>
    private static readonly AsyncLocal<int> s_nestingDepth = new();

    /// <inheritdoc/>
    public string Key { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string Category { get; }

    /// <inheritdoc/>
    public string Version { get; }

    /// <summary>The component evals that make up this composite.</summary>
    public IReadOnlyList<EvalComponent> Components { get; }

    /// <summary>The aggregation strategy used to combine component results.</summary>
    public IAggregationStrategy Aggregation { get; }

    /// <summary>
    /// Optional pass threshold (0..1). When set, <c>score &gt;= Threshold</c> is required to pass.
    /// When <c>null</c>, the verdict is severity-driven per the matrix in <see cref="EvaluateAsync"/>.
    /// </summary>
    public double? Threshold { get; }

    /// <summary>Initialises a new <see cref="CompositeEval"/>.</summary>
    public CompositeEval(
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<EvalComponent> components,
        IAggregationStrategy aggregation,
        double? threshold = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(aggregation);
        if (components.Count == 0)
            throw new ArgumentException("Composite must have at least one component.", nameof(components));
        if (threshold is { } t && (!double.IsFinite(t) || t < 0.0 || t > 1.0))
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "threshold must be a finite value in [0, 1].");

        Key = key;
        Name = name;
        Category = category;
        Version = version;
        Components = components;
        Aggregation = aggregation;
        Threshold = threshold;
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Phase-7 Task 7.5: producer-side recursion-depth check. Mirrors the
        // consumer-side MissionControl.GraphQL.Query.MaxTreeWalkDepth so a tree
        // that would crash the resolver also fails fast at construction time.
        var depth = s_nestingDepth.Value + 1;
        if (depth > MaxNestingDepth)
            throw new InvalidOperationException(
                $"CompositeEval recursion depth exceeded {MaxNestingDepth} (current: {depth}). " +
                "A composite tree this deep would stack-overflow Mission Control's resolver — " +
                "flatten the composite or split it into multiple top-level evals.");
        s_nestingDepth.Value = depth;
        try
        {
            return await EvaluateCoreAsync(input, ct);
        }
        finally
        {
            s_nestingDepth.Value = depth - 1;
        }
    }

    private async Task<EvalResult> EvaluateCoreAsync(EvalInput input, CancellationToken ct)
    {
        // Run all sub-evals in parallel (no throttle in Phase 1).
        var subTasks = Components.Select(c => c.Eval.EvaluateAsync(input, ct)).ToArray();
        var subs = await Task.WhenAll(subTasks);

        var (score, severity) = Aggregation.Aggregate(subs, Components);
        var (cost, allCacheHits) = CostRollup.Aggregate(subs);

        // Severity rollup honours EvalComponent.Required: an optional component
        // that fails should not propagate its severity to the composite verdict.
        // The aggregation strategy still sees the optional component's score
        // (it shapes the weighted-sum / median / etc.), but the verdict-level
        // severity considers only required-component severities.
        var verdictSeverity = severity;
        if (Components.Any(c => !c.Required))
        {
            var requiredSeverities = subs
                .Zip(Components, (s, c) => (Sub: s, Component: c))
                .Where(pair => pair.Component.Required)
                .Select(pair => pair.Sub.Score.Severity)
                .ToArray();
            verdictSeverity = requiredSeverities.Length > 0
                ? SeverityRollup.Max(requiredSeverities)
                : "none";
        }

        // A REQUIRED sub-result that itself errored (an infrastructure/judge failure, not a real low score —
        // see AtomicLlmEval's own "error" label) must propagate honestly, overriding either verdict path
        // below. AtomicLlmEval deliberately keeps severity "none" on error specifically so it never masquerades
        // as a confirmed high/critical violation — but that means the Threshold==null path (which reads ONLY
        // severity, not label/passed) previously fell straight through to "pass" whenever every required
        // sub's severity happened to be "none", even though a required Article/Scenario was never actually
        // evaluated. "An un-evaluated required control cannot be attested as compliant" (AtomicLlmEval's own
        // words) must hold at every level of the composite tree, not just the leaf.
        var hasRequiredError = Components
            .Zip(subs, (c, s) => (Component: c, Sub: s))
            .Any(pair => pair.Component.Required && pair.Sub.Score.Label == "error");

        // ADR-030 Slice 0.1 (defect D-a): a composite none of whose leaves produced a measurement has
        // nothing to render a verdict on. Every aggregation strategy already excludes "skipped" and
        // "error" leaves from the score and returns (0, "none") when nothing is left — and that
        // (0, "none") then fell straight through BOTH verdict paths below: Threshold==null read the
        // empty severity rollup as "pass", and a Threshold read the placeholder 0.0 as a real "fail"
        // (or "pass" at threshold 0). A green verdict from an instrument that measured nothing is the
        // silent-{} shape; the honest label is "skipped" (every leaf skipped) or "error" (nothing
        // measured and at least one leaf errored — an optional judge that could not speak is still
        // the only thing that ran).
        var measuredCount = subs.Count(s => s.Score.Label is not ("skipped" or "error"));
        var nothingMeasured = measuredCount == 0;
        var allSkipped = nothingMeasured && subs.All(s => s.Score.Label == "skipped");

        // Verdict matrix:
        //   Required sub errored -> error (regardless of score/threshold — nothing was actually evaluated)
        //   No leaf measured     -> skipped (all leaves skipped) or error (some leaf errored), never pass/fail
        //   Threshold set        -> score >= threshold ? pass : fail
        //   Threshold null       -> severity is { high|critical -> fail, medium -> warn, _ -> pass }
        // "warn" is a soft fail: passed = false but label distinguishes from a hard fail.
        var label = hasRequiredError
            ? "error"
            : nothingMeasured
                ? (allSkipped ? "skipped" : "error")
                : Threshold is { } t
                    ? (score >= t ? "pass" : "fail")
                    : verdictSeverity switch
                    {
                        "critical" or "high" => "fail",
                        "medium" => "warn",
                        _ => "pass"
                    };
        var passed = label == "pass";

        // Say why in the result itself (mirrors EvalResult.Skipped, which writes its reason to
        // Recommendations) so a reader of the artifact sees "nothing ran", not a bare 0.0.
        string? nothingMeasuredNote = nothingMeasured && !hasRequiredError
            ? (allSkipped
                ? $"All {subs.Length} component(s) were skipped; nothing was measured, so no verdict is reported."
                : $"No component produced a measurement ({subs.Count(s => s.Score.Label == "error")} errored, " +
                  $"{subs.Count(s => s.Score.Label == "skipped")} skipped); no verdict is reported.")
            : null;

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(score, null, label, passed, Threshold, severity, null),
            Details: new(
                Dimensions: null,
                Evidence: null,
                Recommendations: nothingMeasuredNote is null ? null : new[] { nothingMeasuredNote },
                SubResults: subs,
                AggregationStrategy: Aggregation.Name)
            {
                Summary = nothingMeasuredNote,
            },
            Provenance: new(
                Type: "composite",
                JudgeModel: null,
                PromptId: null,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: cost,
                CacheHit: allCacheHits),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
