// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>
/// A composite eval's definition as AEF needs it beside its result: each component's weight, whether it is required,
/// what its failure does, and the same for its own components. Built by the caller from <c>CompositeEval.Components</c>,
/// in the order of the result's <c>SubResults</c>.
/// </summary>
/// <param name="Weight">The component's weight in its parent.</param>
/// <param name="Required">Whether its parent's rules require it.</param>
/// <param name="OnFailure">What its measured failure does (<c>Averaged</c>, <c>Warn</c>, <c>Fail</c>, <c>FailUnlessPass</c>).</param>
/// <param name="Children">Its own components, when it is a composite; empty for a leaf.</param>
public sealed record AefComponentShape(double Weight, bool Required, string OnFailure, IReadOnlyList<AefComponentShape> Children);

/// <summary>What a composite benchmark run knows about itself that its result does not hold.</summary>
public sealed record AefCompositeRunOptions
{
    /// <summary>Observed CLI invocation and Git facts; absent for non-CLI producers.</summary>
    public AefCliProvenance? Cli { get; init; }

    /// <summary>Observed invocation totals, already grouped by role and model; null when unknown.</summary>
    public IReadOnlyList<AefUsage>? Usage { get; init; }

    /// <summary>Content-free configuration actually applied by the producer.</summary>
    public AefRunConfig? Config { get; init; }

    /// <summary>Pricing policy; defaults to the bundled baseline, with no implied spending limit.</summary>
    public AefCostPolicy? CostPolicy { get; init; } = new() { PriceTable = AgentEval.Models.ModelPricing.TableName };

    /// <summary>Related store-v1 run identities, if persistence created any.</summary>
    public AefStoreV1Link? StoreV1 { get; init; }
    /// <summary>Whether to persist evaluation details as evidence blobs; off by default.</summary>
    public AefContentCapture ContentCapture { get; init; } = AefContentCapture.Off;
    /// <summary>Exact compliance reports linked to their observed root cases.</summary>
    public IReadOnlyList<AefComplianceArtifact>? ComplianceArtifacts { get; init; }
    /// <summary>The subject's ref (<c>kind:name</c>, [ENC-13]).</summary>
    public required string SubjectRef { get; init; }

    /// <summary>The actual target endpoint, when known; user information, query and fragment are removed.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The subject's version, if exact ([ENC-10]).</summary>
    public string? SubjectVersion { get; init; }

    /// <summary>How the run drove its target ([RUN-7]); a stand-in is <c>mocked</c>.</summary>
    public required AefTargetMode TargetMode { get; init; }

    /// <summary>The family, the run's lane: <c>gdpr</c>, <c>eu-ai-act</c>, <c>agentic</c>, …</summary>
    public required string Lane { get; init; }

    /// <summary>The suite's ref: the family and its preset, such as <c>bench:gdpr/standard</c>.</summary>
    public required string SuiteRef { get; init; }

    /// <summary>A signer for the seal, if any.</summary>
    public IAefSigner? Signer { get; init; }

    /// <summary>Seal the closed run by default; false leaves verified unsealed evidence and returns no run hash.</summary>
    public bool Seal { get; init; } = true;

    /// <summary>The command's final decision, if this benchmark applies a gate ([GATE-1]).</summary>
    public AefCommandGate? Gate { get; init; }

    /// <summary>A content-free reason when execution was interrupted; suppresses any final gate.</summary>
    public string? AbortReason { get; init; }

    /// <summary>The originally requested trial policy, including when cancellation stops it early.</summary>
    public AefExecutionPolicy? ExecutionPolicy { get; init; }

    /// <summary>
    /// Facts a case's root line carries in <c>ext</c>, by case id: what the producer measured that AEF has no field for
    /// (TypedMemEval's typed outcome, say). Never content.
    /// </summary>
    public IReadOnlyDictionary<string, JsonObject>? CaseExt { get; init; }
}

/// <summary>
/// Writes a composite benchmark (a preset's <c>EvalResult</c> tree) as an AEF run, sealed by default (S1 #5a). Each of the preset's
/// top-level components is a case: its root at path <c>case</c>, case id the component's key; its parts at
/// <c>case/&lt;key&gt;/…</c>. States come from each score's measurement and label, as §7.5 reads them; a composite records
/// its strategy, threshold, score, how many parts were measured, and the rule that decided it with the parts that did
/// ([RES-5], <see cref="EvalDetails.Lineage"/>). Metrics: <c>pass</c> (a rate) and <c>score</c> (0..1). Content capture is
/// off by default: summaries, recommendations and supporting evidence are captured only with explicit opt-in.
/// </summary>
public static class AefCompositeRunWriter
{
    public const string CasePath = "case";
    public const string PassMetric = "pass";
    public const string ScoreMetric = "score";
    /// <summary>Case scores supplied to the preset's own aggregation, distinct from their arithmetic mean.</summary>
    public const string PresetScoreMetric = "preset-score";

    /// <summary>Writes the run under <paramref name="runsRoot"/>/<c>yyyy/MM/runId</c> and seals it by default; returns its folder and a null run hash when sealing is disabled.</summary>
    public static (string Directory, string? RunHash) Write(
        string runsRoot, EvalResult preset, AefComponentShape shape, AefCompositeRunOptions options,
        DateTimeOffset startedAt, DateTimeOffset endedAt) =>
        WriteCore(runsRoot, preset, shape, options, startedAt, endedAt, null);

    /// <summary>Writes an aborted run with no result lines when evaluation produced no completed node.</summary>
    public static (string Directory, string? RunHash) WriteAbortedEmpty(
        string runsRoot, EvalResult preset, AefCompositeRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        AefNativeFinalization.Validate(options.Seal, options.Signer);
        if (options.AbortReason is null) throw new ArgumentException("An empty aborted run requires an abort reason.", nameof(options));
        return WriteCore(runsRoot, preset, Undescribed, options, startedAt, endedAt, null, emptyResults: true);
    }

    /// <summary>Writes each repeated preset tree and its caller-computed majority-vote rollup, preserving trial parentage.</summary>
    public static (string Directory, string? RunHash) WriteTrials(
        string runsRoot, EvalResult rollup, IReadOnlyList<EvalResult> trials, AefComponentShape shape,
        AefCompositeRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        ArgumentNullException.ThrowIfNull(trials);
        if (trials.Count == 0) throw new ArgumentException("At least one trial is required.", nameof(trials));
        ValidateTrials(rollup, trials);
        return WriteCore(runsRoot, rollup, shape, options, startedAt, endedAt, trials);
    }

    /// <summary>
    /// Preserves interrupted trials, including differing leaf breakdowns. Every observed path receives an error
    /// rollup with exact trial counts; an interrupted invocation makes no aggregate verdict.
    /// </summary>
    public static (string Directory, string? RunHash) WriteAbortedTrials(
        string runsRoot, IReadOnlyList<EvalResult> trials, AefComponentShape shape,
        AefCompositeRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(options);
        AefNativeFinalization.Validate(options.Seal, options.Signer);
        if (trials.Count == 0) throw new ArgumentException("At least one started trial is required.", nameof(trials));
        if (options.AbortReason is null) throw new ArgumentException("An aborted trial run requires an abort reason.", nameof(options));
        var preset = trials[0] with
        {
            Score = new EvalScore(0, null, "error", false, null, "none", null) { Measurement = MeasurementState.NotMeasured },
        };
        return WriteCore(runsRoot, preset, shape, options, startedAt, endedAt, trials, abortedTrials: true);
    }

    private static (string Directory, string? RunHash) WriteCore(
        string runsRoot, EvalResult preset, AefComponentShape shape, AefCompositeRunOptions options,
        DateTimeOffset startedAt, DateTimeOffset endedAt, IReadOnlyList<EvalResult>? trials, bool abortedTrials = false,
        bool emptyResults = false)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(options);
        AefNativeFinalization.Validate(options.Seal, options.Signer);
        // The preset's parts are the cases. A result without parts is one case, itself. Without a definition (a family
        // that builds its result itself, such as bench perf) each case's parts are recorded beside its verdict (Own).
        var single = (preset.Details.SubResults ?? []).Count == 0;
        IReadOnlyList<EvalResult> cases = emptyResults ? [] : single ? [preset] : preset.Details.SubResults!;
        IReadOnlyList<AefComponentShape> caseShapes = emptyResults ? [] : single ? [shape]
            : shape.Children.Count == 0 ? cases.Select(_ => Undescribed).ToList()
            : shape.Children;
        if (cases.Count != caseShapes.Count)
        {
            throw new ArgumentException("the preset's components and its result's parts must match, one case each", nameof(shape));
        }

        // A folder this writer owns (AefRunFolder), so a failure below discards only its own run.
        var storeExtension = AefCliProvenance.Extension(options.Cli, options.StoreV1);
        var endpoint = options.Endpoint is null ? null : AefConverter.Endpoint(AefConverter.WithoutCredentials(options.Endpoint));
        using var folder = AefRunFolder.Reserve(runsRoot, startedAt, "bench");
        var runId = folder.RunId;
        var directory = folder.Directory;
        // The models that graded a leaf: a leaf whose judge gave no verdict (error, skipped) was not graded ([RUN-9]).
        var judges = (emptyResults ? [] : trials ?? [preset]).SelectMany(r => ObservedJudges(r))
            .Distinct().ToList();
        var header = new AefRunHeader
        {
            RunId = runId,
            Producer = AefConverter.DefaultProducer,
            Config = options.Config,
            CostPolicy = options.CostPolicy,
            Subject = new AefSubject { Ref = options.SubjectRef, Kind = AefEvalWriter.KindOf(options.SubjectRef), Version = options.SubjectVersion },
            Deployment = endpoint is null ? null : new AefDeployment { Ref = AefConverter.TypedRef("endpoint", endpoint), Endpoint = endpoint },
            Suite = new AefSuite
            {
                Ref = options.SuiteRef,
                Version = preset.Metric.Version,
                Digest = AefProbeRunWriter.RosterDigest(single ? [preset.Metric.Key] : Keys(preset)),
                Frozen = true,
                ExecutionPolicy = options.ExecutionPolicy,
            },
            Judges = judges.Count > 0 ? judges : null,   // the models that graded a leaf ([RUN-9])
            StartedAt = AefTime.FromDateTimeOffset(startedAt),
            ContentCapture = options.ContentCapture,
            Execution = new AefExecution { TargetMode = options.TargetMode, Stimulus = AefStimulus.Suite },
            Ext = storeExtension,
        };

        var writer = AefRunWriter.Create(directory, header);
        var presetMeasured = !emptyResults && IsMeasured(StateOf(preset.Score)) && double.IsFinite(preset.Score.Value);
        try
        {
            writer.SetMetrics(
            [
                new AefMetric
                {
                    Id = PassMetric, Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                    Description = "A case counts 1 when it passed (SUM-4).",
                },
                new AefMetric
                {
                    Id = ScoreMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                    Description = "The eval's score (0 to 1), on measured lines.",
                },
                new AefMetric
                {
                    Id = PresetScoreMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                    Description = "Case scores supplied to the preset's aggregation; withheld when the preset has no measured score.",
                },
            ]);

            var artifactLinks = AefComplianceArtifact.Write(writer, options.ComplianceArtifacts, options.ContentCapture, cases.Select(c => c.Metric.Key));
            if (abortedTrials)
            {
                var captured = new List<(AefResult Result, string? Parent)>();
                for (var t = 0; t < trials!.Count; t++)
                {
                    var trialCases = single ? new[] { trials[t] } : trials[t].Details.SubResults ?? [];
                    if (trialCases.Count != cases.Count || trialCases.Where((c, i) => c.Metric.Key != cases[i].Metric.Key).Any())
                        throw new ArgumentException("Repeated presets must have the same ordered case keys.", nameof(trials));
                    for (var i = 0; i < trialCases.Count; i++)
                        Add(writer, trialCases[i], caseShapes[i], trialCases[i].Metric.Key, CasePath, null, null, trial: t,
                            capture: (line, parent) => captured.Add((line, parent?.Path)), contentCapture: options.ContentCapture);
                }
                AddAbortedRollups(writer, captured, artifactLinks);
            }
            else for (var i = 0; i < cases.Count; i++)
            {
                var caseTrials = trials?.Select(t => single ? t : t.Details.SubResults![i]).ToList();
                if (caseTrials is not null)
                {
                    for (var t = 0; t < caseTrials.Count; t++)
                        Add(writer, caseTrials[t], caseShapes[i], cases[i].Metric.Key, CasePath, null, null, trial: t, contentCapture: options.ContentCapture);
                }
                Add(writer, cases[i], caseShapes[i], cases[i].Metric.Key, CasePath, parent: null, component: null,
                    options.CaseExt is { } caseExt && caseExt.TryGetValue(cases[i].Metric.Key, out var ext) ? ext : null,
                    trials: caseTrials, presetMeasured: presetMeasured, contentCapture: options.ContentCapture,
                    artifactEvidence: artifactLinks.GetValueOrDefault(cases[i].Metric.Key));
            }

            if (options.AbortReason is null)
                options.Gate?.Write(writer, runId, endedAt, cases.Select(c => writer.ResultIdOf(c.Metric.Key, CasePath)).ToList());

            writer.SetSummary(new AefSummary
            {
                Usage = options.Usage,
                Cost = AefNativeUsage.Cost(options.Usage),
                Lanes = [new AefSummaryLane(options.Lane,
                [
                    new AefSummaryEntry { Metric = PassMetric, Path = CasePath },
                    new AefSummaryEntry { Metric = ScoreMetric, Path = CasePath },
                    new AefSummaryEntry
                    {
                        Metric = PresetScoreMetric, Path = CasePath,
                        Aggregate = new AefAggregate("agenteval." + new string((preset.Details.AggregationStrategy ?? "own")
                            .ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '@' or '.' or '_' or '-' ? c : '-').ToArray())),
                        Rule = "label:" + preset.Score.Label,
                        Decide = _ => new AefSummaryDecision(StateOf(preset.Score) switch
                        {
                            AefState.Passed => AefSummaryVerdict.Passed,
                            AefState.Failed => AefSummaryVerdict.Failed,
                            AefState.Warn => AefSummaryVerdict.Warn,
                            AefState.Scored => AefSummaryVerdict.Scored,
                            _ => AefSummaryVerdict.Inconclusive,
                        }, preset.Score.Value),
                    },
                ])],
                // The preset's own verdict, which no lane recomputes ([RES-6]): its label, score and the rule that decided it.
                Ext = new JsonObject
                {
                    ["agenteval.bench"] = new JsonObject
                    {
                        ["preset"] = preset.Metric.Key,
                        ["label"] = preset.Score.Label,
                        ["score"] = presetMeasured ? preset.Score.Value : null,
                        ["strategy"] = preset.Details.AggregationStrategy,
                        ["rulePath"] = preset.Details.Lineage?.RulePath,
                    },
                },
            });
            writer.Close(options.AbortReason is null ? AefRunStatus.Completed : AefRunStatus.Aborted,
                AefTime.FromDateTimeOffset(endedAt), options.AbortReason);
            return (directory, AefNativeFinalization.Complete(directory, options.Seal, options.Signer));
        }
        catch
        {
            AefConverter.Discard(directory, existed: false);
            throw;
        }
    }

    /// <summary>A part whose definition is not known: weight 0, not required, recorded beside its parent's verdict.</summary>
    public static AefComponentShape Undescribed { get; } = new(0, false, "Averaged", []);

    /// <summary>A score's AEF state: its measurement first, then its label (§7.5); an unknown label by its pass flag.</summary>
    public static AefState StateOf(EvalScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return score.Measurement switch
        {
            MeasurementState.NotApplicable => AefState.NotApplicable,
            MeasurementState.NotMeasured => score.Label switch
            {
                "error" => AefState.Error,
                "skipped" => AefState.Skipped,
                _ => AefState.NotMeasured,
            },
            _ => score.Label switch
            {
                "pass" => AefState.Passed,
                "fail" => AefState.Failed,
                "warn" => AefState.Warn,
                "error" => AefState.Error,
                "skipped" => AefState.Skipped,
                "inapplicable" => AefState.NotApplicable,
                "scored" => AefState.Scored,   // measured, with no verdict: a benchmark that publishes no pass mark
                _ => score.Passed ? AefState.Passed : AefState.Failed,
            },
        };
    }

    private static void Add(AefRunWriter writer, EvalResult node, AefComponentShape shape, string caseId, string path,
        AefResultHandle? parent, AefComponent? component, JsonObject? caseExt = null, int? trial = null,
        IReadOnlyList<EvalResult>? trials = null, bool presetMeasured = false,
        Action<AefResult, AefResultHandle?>? capture = null, AefContentCapture contentCapture = AefContentCapture.Off,
        IReadOnlyList<string>? artifactEvidence = null)
    {
        var subs = node.Details.SubResults ?? [];
        var state = StateOf(node.Score);
        var measured = IsMeasured(state);
        AefAggregation? aggregation = null;

        // A composite's parts are its definition's components, in order. A leaf eval that keeps its own breakdown as
        // sub-results (groundedness's claims, say) has none: its parts are recorded beside its own verdict, Own, each
        // with weight 0 and not required (section 7.5).
        var described = shape.Children.Count > 0;
        if (described && subs.Count != shape.Children.Count)
        {
            throw new ArgumentException($"'{node.Metric.Key}': its parts must match its definition's components", nameof(shape));
        }

        var distinctKeys = subs.Select(s => s.Metric.Key).Distinct(StringComparer.Ordinal).Count() == subs.Count;
        var childPaths = subs.Select((s, i) => distinctKeys ? $"{path}/{s.Metric.Key}" : $"{path}/{i + 1}").ToList();
        if (subs.Count > 0)
        {
            var childStates = subs.Select(s => StateOf(s.Score)).ToList();
            var keyToPath = distinctKeys
                ? subs.Zip(childPaths).ToDictionary(p => p.First.Metric.Key, p => p.Second, StringComparer.Ordinal)
                : [];
            aggregation = new AefAggregation
            {
                Strategy = described && AefNames.TryParse<AefAggregationStrategy>(node.Details.AggregationStrategy ?? "", out var strategy) ? strategy.Value : AefAggregationStrategy.Own,
                RulePath = AefNames.TryParse<AefRulePath>(node.Details.Lineage?.RulePath ?? "", out var rule) ? rule.Value
                    : measured ? AefRulePath.Threshold : AefRulePath.NothingMeasured,
                Threshold = node.Score.Threshold,
                Score = measured && double.IsFinite(node.Score.Value) ? node.Score.Value : null,
                Measured = childStates.Count(IsMeasured),
                Total = subs.Count,
                Unmeasured = Unmeasured(childStates),
                Decisive = node.Details.Lineage is { Decisive.Count: > 0 } lineage
                    ? lineage.Decisive.Where(keyToPath.ContainsKey).Select(k => writer.ResultIdOf(caseId, keyToPath[k], trial)).ToList()
                    : null,
            };
        }

        AefSeverity? severity = state is AefState.Failed or AefState.Warn && AefNames.TryParse<AefSeverity>(node.Score.Severity, out var s) ? s : null;
        var llm = node.Provenance.Type is "atomic-llm" or "atomic-decision"
            && !string.IsNullOrWhiteSpace(node.Provenance.JudgeModel);
        var line = new AefResult
        {
            Evidence = artifactEvidence,
            CaseId = caseId,
            Path = path,
            Trial = trial,
            Trials = trials is null ? null : new AefTrials(trials.Count,
                trials.Count(t => StateOf(t.Score) == AefState.Passed), AefTrialAggregation.MajorityVote,
                trials.Select(t => StateOf(t.Score)).Distinct().Count() == 1),
            Evaluator = new AefEvaluator(AefConverter.Text(node.Metric.Key, 256) ?? "agenteval.eval", AefConverter.Text(node.Metric.Version, 64)),
            State = state,
            // A typed absence says only where its state came from: the eval's own words may quote content ([RUN-11]).
            Reason = measured ? null : $"AgentEval: the eval's label is '{AefConverter.Text(node.Score.Label, 64)}' and its measurement state {node.Score.CensusBucket()}",
            Severity = severity,
            Scores = measured && double.IsFinite(node.Score.Value)
                ? presetMeasured
                    ? [new AefScore { Metric = ScoreMetric, Value = node.Score.Value }, new AefScore { Metric = PresetScoreMetric, Value = node.Score.Value }]
                    : [new AefScore { Metric = ScoreMetric, Value = node.Score.Value }]
                : null,
            Annotator = subs.Count > 0 || !measured || trials is not null ? null : new AefAnnotator
            {
                Kind = llm ? AefAnnotatorKind.Llm : AefAnnotatorKind.Code,
                Model = llm ? node.Provenance.JudgeModel : null,
                RubricDigest = llm ? node.Provenance.RubricDigest : null,
            },
            Aggregation = aggregation,
            Component = component,
            Ext = component is not null && shape.OnFailure != "Averaged"
                ? new JsonObject { ["agenteval.component"] = new JsonObject { ["onFailure"] = shape.OnFailure } }
                : caseExt?.DeepClone().AsObject(),
        };
        if (contentCapture == AefContentCapture.On &&
            (node.Details.Summary is not null || node.Details.Evidence is { Count: > 0 } || node.Details.Recommendations is { Count: > 0 }))
            line = AefNativeContent.Attach(writer, line, new AefNativeContent
            {
                JudgeReasoning = llm ? node.Details.Summary : null,
                Document = System.Text.Json.JsonSerializer.Serialize(new
                {
                    node.Details.Summary, node.Details.Evidence, node.Details.Recommendations,
                }),
            });
        var handle = writer.AddResult(line, parent);
        capture?.Invoke(line, parent);

        for (var i = 0; i < subs.Count; i++)
        {
            var child = described ? shape.Children[i] : Undescribed;
            Add(writer, subs[i], child, caseId, childPaths[i], handle, new AefComponent(child.Weight, child.Required),
                trial: trial, trials: trials?.Select(t => t.Details.SubResults![i]).ToList(), capture: capture, contentCapture: contentCapture);
        }
    }

    private static void AddAbortedRollups(AefRunWriter writer, List<(AefResult Result, string? Parent)> captured,
        IReadOnlyDictionary<string, IReadOnlyList<string>> artifactLinks)
    {
        var groups = captured.GroupBy(x => (x.Result.CaseId, x.Result.Path)).ToList();
        var childCounts = groups.Where(g => g.First().Parent is not null)
            .GroupBy(g => (g.Key.CaseId, Parent: g.First().Parent!)).ToDictionary(g => g.Key, g => g.Count());
        var handles = new Dictionary<(string CaseId, string Path), AefResultHandle>();
        // Each trial was traversed parent first; a newly encountered path's ancestors therefore precede it too.
        foreach (var group in groups)
        {
            var first = group.First();
            if (group.Any(x => x.Parent != first.Parent || x.Result.Evaluator != first.Result.Evaluator))
                throw new ArgumentException("Trial paths must identify the same evaluator and parent in every trial.", nameof(captured));
            var children = childCounts.GetValueOrDefault((group.Key.CaseId, group.Key.Path));
            var rollup = first.Result with
            {
                Evidence = first.Parent is null ? artifactLinks.GetValueOrDefault(group.Key.CaseId) : null,
                Reasoning = null,
                Trial = null,
                Trials = new AefTrials(group.Count(), group.Count(x => x.Result.State == AefState.Passed),
                    AefTrialAggregation.MajorityVote, group.Select(x => x.Result.State).Distinct().Count() == 1),
                State = AefState.Error,
                Reason = "AgentEval: trial aggregation did not complete because execution was cancelled",
                Scores = null, Severity = null, Annotator = null,
                Aggregation = children == 0 ? null : new AefAggregation
                {
                    Strategy = AefAggregationStrategy.Own, RulePath = AefRulePath.NothingMeasured,
                    Measured = 0, Total = children, Unmeasured = new AefUnmeasured { Error = children },
                },
            };
            var parent = first.Parent is null ? null : handles[(group.Key.CaseId, first.Parent)];
            handles.Add(group.Key, writer.AddResult(rollup, parent));
        }
    }

    private static void ValidateTrials(EvalResult rollup, IReadOnlyList<EvalResult> trials)
    {
        var children = rollup.Details.SubResults ?? [];
        if (trials.Any(t => t.Metric.Key != rollup.Metric.Key || (t.Details.SubResults?.Count ?? 0) != children.Count))
            throw new ArgumentException("Trial trees must have the same ordered metric keys and structure as their rollup.", nameof(trials));
        for (var i = 0; i < children.Count; i++)
            ValidateTrials(children[i], trials.Select(t => t.Details.SubResults![i]).ToList());
    }

    private static AefUnmeasured? Unmeasured(IReadOnlyList<AefState> states)
    {
        long Count(AefState state) => states.LongCount(s => s == state);
        var notMeasured = Count(AefState.NotMeasured) + Count(AefState.Pending);
        var unmeasured = new AefUnmeasured
        {
            NotMeasured = notMeasured > 0 ? notMeasured : null,
            NotApplicable = Count(AefState.NotApplicable) is > 0 and var na ? na : null,
            Skipped = Count(AefState.Skipped) is > 0 and var sk ? sk : null,
            Error = Count(AefState.Error) is > 0 and var er ? er : null,
        };
        return unmeasured is { NotMeasured: null, NotApplicable: null, Skipped: null, Error: null } ? null : unmeasured;
    }

    private static bool IsMeasured(AefState state) =>
        state is AefState.Passed or AefState.Failed or AefState.Warn or AefState.Inconclusive or AefState.Scored;

    private static IEnumerable<AefJudge> ObservedJudges(EvalResult node, int? panelSize = null)
    {
        if (node.Details.SubResults is { Count: > 0 } subs)
        {
            var ownPanel = node.Provenance.JudgePanelSize;
            if (ownPanel is <= 0 || ownPanel > subs.Count)
                throw new ArgumentException("Judge panel size must name the leading panel sub-results.");
            for (var i = 0; i < subs.Count; i++)
            {
                // A wrapper's trailing adjudicator is a separate invocation, not another panel vote.
                var childPanel = ownPanel is { } size ? (i < size ? size : (int?)null) : panelSize;
                foreach (var judge in ObservedJudges(subs[i], childPanel)) yield return judge;
            }
        }
        else if (IsMeasured(StateOf(node.Score)) && !string.IsNullOrWhiteSpace(node.Provenance.JudgeModel))
        {
            yield return new AefJudge
            {
                Model = node.Provenance.JudgeModel!, Provider = node.Provenance.JudgeProvider, RubricDigest = node.Provenance.RubricDigest,
                Mode = panelSize is null ? AefJudgeMode.Single : AefJudgeMode.Panel, PanelSize = panelSize,
            };
        }
    }

    private static IEnumerable<string> Keys(EvalResult node) =>
        node.Details.SubResults is { Count: > 0 } subs ? subs.SelectMany(s => Keys(s).Prepend(s.Metric.Key)) : [];
}
