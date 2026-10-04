// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;

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

    /// <summary>
    /// The share of components (0..1) that must produce a measurement for a passing composite to report
    /// <c>pass</c>. Below it the composite reports <c>warn</c>: nothing failed, but the pass would rest on a minority
    /// of what the composite claims to cover. Default 0.5. Set 0 to drop this bar. It is not the only one: a pass also
    /// needs every <see cref="EvalComponent.Required"/> component to have run — a nested composite counts as not run
    /// when it withheld its own pass for that reason (it records <see cref="MeasurementState.NotMeasured"/>) — whatever
    /// this share is, and components are required by default.
    /// </summary>
    /// <remarks>
    /// Leaves that could not measure (skipped, inapplicable, errored) are excluded from the score, which is right:
    /// they must not score 0. But it means a composite whose components mostly declare themselves inapplicable
    /// passes on whatever is left. A CI gate keyed on the label then exits 0 on a 1-of-10 measurement. This bar
    /// stops a self-declared "not applicable" from diluting the denominator into a pass.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite number in [0, 1].</exception>
    public double MinimumMeasuredShare
    {
        get => _minimumMeasuredShare;
        init => _minimumMeasuredShare = double.IsFinite(value) && value is >= 0.0 and <= 1.0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MinimumMeasuredShare), value, "must be a finite value in [0, 1].");
    }
    private readonly double _minimumMeasuredShare = 0.5;

    /// <summary>
    /// When <see langword="true"/> and a <see cref="Threshold"/> is set, a score at or above the threshold still has to
    /// clear the required components' severity, exactly as the severity path does: <c>critical</c>/<c>high</c> → fail,
    /// <c>medium</c> → warn. Default <see langword="false"/> (score alone decides, as before).
    /// </summary>
    /// <remarks>
    /// Without it, a weighted average can absorb a severe failure: GDPR Standard (threshold 0.85) passed with one
    /// critical article failing, though its docs promise FAIL for any high or critical article failure (#203 review,
    /// B4). Set it on a preset whose docs make that promise.
    /// </remarks>
    public bool SeverityCapsThreshold { get; init; }

    /// <summary>
    /// A copy of this composite with <paramref name="components"/> in place of its own and every other setting kept —
    /// key, name, category, version, aggregation, threshold and each init-only setting. Code that rebuilds a composite
    /// (domain packs' <c>WithExtraScenarios</c>, the cost filter) goes through here: two such sites rebuilt it from the
    /// constructor and silently dropped <see cref="MinimumMeasuredShare"/> and <see cref="SeverityCapsThreshold"/>
    /// (#203 review, B4 and B6b). A setting added later is guarded by a reflection test over the init-only properties.
    /// </summary>
    public CompositeEval WithComponents(IReadOnlyList<EvalComponent> components) =>
        new(Key, Name, Category, Version, components, Aggregation, Threshold)
        {
            MinimumMeasuredShare = MinimumMeasuredShare,
            SeverityCapsThreshold = SeverityCapsThreshold,
        };

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
        // And the verdict reads only the parts that did NOT pass (#203 review, B6c-3). The aggregations roll severity up
        // over every measured part, so a PASSING article still carried the severity of a scenario failure its own
        // scoring absorbed — and a preset failed with every article passing (GDPR Standard: 28 such single-scenario
        // cases), while the same article failing as a whole read only a warn.
        var failingSeverities = subs
            .Zip(Components, (s, c) => (Sub: s, Component: c))
            .Where(pair => (pair.Component.Required || Components.All(c => c.Required))
                           && pair.Sub.Score.CountsTowardAggregate()
                           && !pair.Sub.Score.Passed)
            .Select(pair => pair.Sub.Score.Severity)
            .ToArray();
        var verdictSeverity = failingSeverities.Length > 0 ? SeverityRollup.Max(failingSeverities) : "none";

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

        // The same rule for a REQUIRED component that did not run for any other reason: a skipped leaf (a
        // required input, trace or telemetry was not supplied), a nested composite that measured nothing, any
        // score whose measurement is NotMeasured. Only "error" was checked above, so a required component that
        // returned EvalResult.Skipped was simply left out and the composite passed on the rest ("Measured 1 of
        // 2", label pass; reported in #203). It cannot attest a pass, so a would-be pass becomes warn below. A
        // measured fail stays a fail, "error" still wins, and NotApplicable (the CASE cannot test the thing,
        // ADR-030) is not this: it never blocks.
        //
        // One level down it is the same gap, and the parent learns it from STATE, never from the label. A nested
        // composite that withheld its pass for this reason reports warn with severity "none" — but a warn is just as
        // often a measured medium-severity fail, and reading the label made GDPR/EU Standard warn on medium article
        // failures while high/critical ones still passed (#203 review, round 2). So the composite records the reason
        // in its own Score.Measurement (see `measurement` below): NotMeasured when it withholds a pass because a
        // required component did not run — its parent blocks on that exactly as on a skipped leaf — and NotApplicable
        // when every required component could only say "the case cannot test this", which never blocks.
        var requiredUnattested = Components
            .Zip(subs, (c, s) => (Component: c, Sub: s))
            .Where(pair => pair.Component.Required
                           && pair.Sub.Score.Label != "error"
                           && pair.Sub.Score.CensusBucket() == MeasurementState.NotMeasured)
            .Select(pair => pair.Sub.Metric.Key)
            .ToArray();

        // "The case cannot test what this composite requires": every REQUIRED component (every component when none
        // is required) is NotApplicable — a leaf that said so, or a nested composite that recorded it.
        var gating = Components.Any(c => c.Required)
            ? Components.Zip(subs, (c, s) => (Component: c, Sub: s)).Where(pair => pair.Component.Required).Select(pair => pair.Sub)
            : subs;
        var requiredAllInapplicable = gating.All(s => s.Score.CensusBucket() == MeasurementState.NotApplicable);

        // ADR-030 Slice 0.1 (defect D-a): a composite none of whose leaves produced a measurement has
        // nothing to render a verdict on. Every aggregation strategy already excludes "skipped" and
        // "error" leaves from the score and returns (0, "none") when nothing is left — and that
        // (0, "none") then fell straight through BOTH verdict paths below: Threshold==null read the
        // empty severity rollup as "pass", and a Threshold read the placeholder 0.0 as a real "fail"
        // (or "pass" at threshold 0). A green verdict from an instrument that measured nothing is the
        // silent-{} shape; the honest label is "skipped" (every leaf skipped) or "error" (nothing
        // measured and at least one leaf errored — an optional judge that could not speak is still
        // the only thing that ran).
        //
        // ADR-030 Slice 1.2, and a correction to that slice's own acceptance criterion. The criterion
        // reads "one predicate, five call sites" and names the five aggregation strategies. THIS IS THE
        // SIXTH SITE, it decides pass/fail, and it carried its own label-only copy of the rule — so
        // Slice 1.1's new neutral label, "inapplicable", was invisible to it. A composite every leaf of
        // which was inapplicable counted them as measurements, skipped the branch below, took the
        // Threshold==null path, read an empty severity rollup as "none" and reported PASS. That is
        // defect D-a exactly, re-opened by the slice that exists to make undecidability expressible.
        // Routing through EvalScoreExtensions.CountsTowardAggregate() is what stops the next neutral
        // state having to be added here a second time.
        var measuredCount = subs.Count(s => s.Score.CountsTowardAggregate());
        var nothingMeasured = measuredCount == 0;
        var erroredCount = subs.Count(s => s.Score.Label == "error");
        var skippedCount = subs.Count(s => s.Score.Label == "skipped");
        var inapplicableCount = subs.Count(s => s.Score.CensusBucket() == MeasurementState.NotApplicable);

        // Verdict matrix:
        //   Required sub errored -> error (regardless of score/threshold — nothing was actually evaluated)
        //   No leaf measured     -> error when any leaf errored, else skipped; never pass/fail
        //   Threshold set        -> score >= threshold ? pass : fail
        //   Threshold null       -> severity is { high|critical -> fail, medium -> warn, _ -> pass }
        //   then a pass becomes warn when a required component did not run or could not attest its own pass (a
        //   nested composite's warn), or when less than MinimumMeasuredShare of the components was measured
        // "warn" is a soft fail: passed = false but label distinguishes from a hard fail.
        //
        // An all-inapplicable composite is a CORPUS finding and its true label is "inapplicable". The
        // schema has ACCEPTED that label since v1.1 (ADR-030 Slice 1.4(i), 2026-09-06); what is not done is
        // the writer half, Slice 1.4(ii) — emitting it here changes historical content hashes, and that
        // slice is unfunded, not blocked by an open question. Until it lands this composite reports
        // "skipped" (non-passing, and correct about the one thing that matters here: no verdict) and the
        // note below carries the attribution the label cannot. Behaviour-identical for every pre-existing
        // label: all-skipped still yields "skipped", and any errored leaf still yields "error" — except when every
        // required component is inapplicable: the composite cannot be tested at all, so an optional component that
        // errored is not its verdict (it reports "skipped", recorded NotApplicable below, and never blocks a parent).
        var label = hasRequiredError
            ? "error"
            : nothingMeasured
                ? (erroredCount > 0 && !requiredAllInapplicable ? "error" : "skipped")
                : Threshold is { } t
                    ? (score < t ? "fail" : SeverityCapsThreshold ? SeverityLabel(verdictSeverity) : "pass")
                    : SeverityLabel(verdictSeverity);

        // A pass that rests on a minority of the components is not the composite's pass. Nothing failed, so it is a
        // soft finding (warn → exit 10 through BenchExitCodes), not a fail.
        var underCovered = label == "pass" && measuredCount < MinimumMeasuredShare * subs.Length;
        // Nor is a pass that leaves out a required component that did not run (see requiredUnattested).
        var passUnattested = label == "pass" && requiredUnattested.Length > 0;
        if (underCovered || passUnattested)
            label = "warn";

        // What a component's own measured failure does to the verdict (#203 review, B6b — the owner's rule: keep
        // working and say the answer is not optimal because of the failed dimension, unless that failure means the
        // answer cannot be trusted). Before, every component was "averaged": one dimension could fail and the
        // composite still read PASS. Only a MEASURED failure counts (skipped and errored components are the rules
        // above), and the effect only escalates. A component that only warned passes a warn up, never a fail.
        var effectsFired = Components.Zip(subs, (c, s) => (Component: c, Sub: s))
            .Where(p => p.Component.OnFailure != ComponentFailureEffect.Averaged
                        && p.Sub.Score.CountsTowardAggregate()
                        && !p.Sub.Score.Passed
                        && p.Sub.Score.Label is "fail" or "warn")
            .ToArray();
        var failingAccuracy = effectsFired
            .Where(p => p.Component.OnFailure == ComponentFailureEffect.Fail && p.Sub.Score.Label == "fail")
            .Select(p => p.Sub.Metric.Key)
            .ToArray();
        var notOptimal = effectsFired
            .Where(p => !(p.Component.OnFailure == ComponentFailureEffect.Fail && p.Sub.Score.Label == "fail"))
            .Select(p => p.Sub.Metric.Key)
            .ToArray();
        if (failingAccuracy.Length > 0 && label is "pass" or "warn")
            label = "fail";
        else if (notOptimal.Length > 0 && label == "pass")
            label = "warn";
        var passed = label == "pass";

        // The composite's own measurement state, which is how a parent tells "withheld" from "measured" — never by the
        // label (see requiredUnattested). NotMeasured: this composite withheld its pass because a required component
        // did not run. NotApplicable: nothing was measured and the case cannot test what it requires. Otherwise the
        // default (Measured; written to JSON only when it is not), so every other result serialises as before.
        // A measured accuracy failure is a verdict in its own right, whatever else did not run.
        var measurement = passUnattested && label != "fail"
            ? MeasurementState.NotMeasured
            : nothingMeasured && requiredAllInapplicable && !hasRequiredError
                ? MeasurementState.NotApplicable
                : MeasurementState.Measured;

        // Say why in the result itself (mirrors EvalResult.Skipped, which writes its reason to
        // Recommendations) so a reader of the artifact sees "nothing ran", not a bare 0.0. The three
        // states have different owners and different fixes — inapplicable is "fix the cases", skipped
        // and errored are "fix the run" — so the note counts them separately rather than pooling them.
        string? nothingMeasuredNote = nothingMeasured && !hasRequiredError
            ? (skippedCount == subs.Length
                ? $"All {subs.Length} component(s) were skipped; nothing was measured, so no verdict is reported."
                : inapplicableCount == subs.Length
                    ? $"All {subs.Length} component(s) were inapplicable — no case could test the thing, " +
                      "so no verdict is reported. This is a corpus finding, not a run failure."
                    : requiredAllInapplicable
                        ? "Every required component was inapplicable — the case cannot test what this composite " +
                          $"requires — so no verdict is reported ({erroredCount} errored, {skippedCount} skipped among " +
                          "the optional ones). This is a corpus finding, not a run failure."
                        : $"No component produced a measurement ({erroredCount} errored, " +
                          $"{skippedCount} skipped, {inapplicableCount} inapplicable); no verdict is reported.")
            : null;

        // Coverage disclosure for a PARTLY measured composite. Excluding skipped, inapplicable and
        // errored leaves from the denominator is deliberate (and test-pinned): a leaf that could not
        // measure must not score 0. But without a note, a composite with 9 of 10 leaves unmeasured and
        // one leaf at 1.0 reported pass, Score = 1.0 and nothing else, under every aggregation strategy —
        // the diluted-denominator shape, in silence. The verdict is unchanged; the result now says how
        // much of it was measured.
        // The breakdown is mutually exclusive (error first, then inapplicable, then everything else not
        // measured), so its three numbers always add up to the unmeasured count it explains.
        var unmeasured = subs.Where(s => !s.Score.CountsTowardAggregate()).ToArray();
        var unmeasuredErrored = unmeasured.Count(s => s.Score.Label == "error");
        var unmeasuredInapplicable = unmeasured.Count(s =>
            s.Score.Label != "error" && s.Score.CensusBucket() == MeasurementState.NotApplicable);
        var unmeasuredOther = unmeasured.Length - unmeasuredErrored - unmeasuredInapplicable;
        var breakdown = $"({unmeasuredOther} skipped or not measured, {unmeasuredInapplicable} inapplicable, {unmeasuredErrored} errored)";
        var share = MinimumMeasuredShare.ToString("P0", System.Globalization.CultureInfo.InvariantCulture);
        var leftOut = unmeasured.Length == 0 ? "" : $"; {unmeasured.Length} left out of the score {breakdown}";
        string? partialCoverageNote = nothingMeasured
            ? null
            : hasRequiredError
                ? $"A required component errored, so no pass/fail verdict is reported. Measured {measuredCount} of " +
                  $"{subs.Length} component(s); {unmeasured.Length} produced no measurement {breakdown}."
                : passUnattested
                    // Both bars can fire at once; say both, so neither reason is lost.
                    ? $"Required component(s) that did not run or could not attest their own pass: " +
                      $"{string.Join(", ", requiredUnattested)}. A pass cannot rest on them, so the verdict is warn. " +
                      (underCovered
                          ? $"It also rests on only {measuredCount} of {subs.Length} component(s), below the {share} a pass needs"
                          : $"Measured {measuredCount} of {subs.Length} component(s)") +
                      $"{leftOut}."
                    : unmeasured.Length == 0
                        ? null
                        : underCovered
                            ? $"Passed on only {measuredCount} of {subs.Length} component(s), below the {share} a pass needs, " +
                              $"so the verdict is warn; {unmeasured.Length} left out of the score {breakdown}."
                            : $"Measured {measuredCount} of {subs.Length} component(s); {unmeasured.Length} left out of the score " +
                              $"{breakdown}, so this verdict covers only the measured part.";
        // Name the dimensions that decided an escalated verdict, so a FAIL or WARN says why at the top.
        string? effectNote = failingAccuracy.Length > 0
            ? $"Failed: {string.Join(", ", failingAccuracy)} — a dimension whose failure means the answer cannot be " +
              "trusted, so the verdict is fail." +
              (notOptimal.Length > 0 ? $" Also not optimal: {string.Join(", ", notOptimal)}." : "")
            : notOptimal.Length > 0
                ? $"Not optimal: {string.Join(", ", notOptimal)} did not pass — the answer is usable, so the verdict is " +
                  "warn, not fail."
                : null;
        var coverageNote = string.Join(" ", new[] { effectNote, nothingMeasuredNote ?? partialCoverageNote }.Where(n => n is not null))
                           is { Length: > 0 } joined ? joined : null;

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(score, null, label, passed, Threshold, ReportedSeverity(label, severity), null) { Measurement = measurement },
            Details: new(
                Dimensions: null,
                Evidence: null,
                Recommendations: coverageNote is null ? null : new[] { coverageNote },
                SubResults: subs,
                AggregationStrategy: Aggregation.Name)
            {
                Summary = coverageNote,
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

    // The severity a composite reports is the one its verdict implies (#203 review, B6c-3): a pass reports "none" — a
    // failure its scoring absorbed stays absorbed, instead of reaching a parent as if it were the composite's own — and a
    // fail reports at least "medium", so a parent's severity cap never reads a failed composite as harmless.
    // A warn reports at most "medium" — the severity a warn means everywhere (high/critical → fail, medium → warn): a warn
    // carrying "critical" (a quality dimension that failed badly, classified Warn) was read as a FAIL by a parent's
    // severity cap, and once a skipped part made that child withhold its pass, the parent dropped it and read WARN — a
    // part that did not run lifting the verdict (B6c-6). The dimension's own severity stays on the sub-result.
    private static string ReportedSeverity(string label, string aggregated) => label switch
    {
        "pass" => "none",
        "fail" => SeverityRollup.Max([aggregated, "medium"]),
        "warn" => aggregated is "high" or "critical" ? "medium" : aggregated,
        _ => aggregated,
    };

    // The severity path's verdict: high or critical fails, medium warns, none or low passes.
    private static string SeverityLabel(string severity) => severity switch
    {
        "critical" or "high" => "fail",
        "medium" => "warn",
        _ => "pass",
    };
}
