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
    /// <summary>The subject's ref (<c>kind:name</c>, [ENC-13]).</summary>
    public required string SubjectRef { get; init; }

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
}

/// <summary>
/// Writes a composite benchmark (a preset's <c>EvalResult</c> tree) as a sealed AEF run (S1 #5a). Each of the preset's
/// top-level components is a case: its root at path <c>case</c>, case id the component's key; its parts at
/// <c>case/&lt;key&gt;/…</c>. States come from each score's measurement and label, as §7.5 reads them; a composite records
/// its strategy, threshold, score, how many parts were measured, and the rule that decided it with the parts that did
/// ([RES-5], <see cref="EvalDetails.Lineage"/>). Metrics: <c>pass</c> (a rate) and <c>score</c> (0..1). Content capture is
/// off: no eval summary, recommendation or judge reasoning is written.
/// </summary>
public static class AefCompositeRunWriter
{
    public const string CasePath = "case";
    public const string PassMetric = "pass";
    public const string ScoreMetric = "score";

    /// <summary>Writes the run under <paramref name="runsRoot"/>/<c>yyyy/MM/runId</c> and seals it; returns its folder and run hash.</summary>
    public static (string Directory, string RunHash) Write(
        string runsRoot, EvalResult preset, AefComponentShape shape, AefCompositeRunOptions options,
        DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(options);
        // The preset's parts are the cases. A result without parts is one case, itself. Without a definition (a family
        // that builds its result itself, such as bench perf) each case's parts are recorded beside its verdict (Own).
        var single = (preset.Details.SubResults ?? []).Count == 0;
        IReadOnlyList<EvalResult> cases = single ? [preset] : preset.Details.SubResults!;
        IReadOnlyList<AefComponentShape> caseShapes = single ? [shape]
            : shape.Children.Count == 0 ? cases.Select(_ => Undescribed).ToList()
            : shape.Children;
        if (cases.Count != caseShapes.Count)
        {
            throw new ArgumentException("the preset's components and its result's parts must match, one case each", nameof(shape));
        }

        // A folder this writer owns (AefRunFolder), so a failure below discards only its own run.
        using var folder = AefRunFolder.Reserve(runsRoot, startedAt, "bench");
        var runId = folder.RunId;
        var directory = folder.Directory;
        // The models that graded a leaf: a leaf whose judge gave no verdict (error, skipped) was not graded ([RUN-9]).
        var judges = Leaves(preset).Where(l => IsMeasured(StateOf(l.Score))).Select(l => l.Provenance.JudgeModel).Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal).Select(m => new AefJudge { Model = m!, Mode = AefJudgeMode.Single }).ToList();
        var header = new AefRunHeader
        {
            RunId = runId,
            Producer = AefConverter.DefaultProducer,
            Subject = new AefSubject { Ref = options.SubjectRef, Kind = AefEvalWriter.KindOf(options.SubjectRef), Version = options.SubjectVersion },
            Suite = new AefSuite
            {
                Ref = options.SuiteRef,
                Version = AefConverter.Version,
                Digest = AefProbeRunWriter.RosterDigest(Keys(preset)),
                Frozen = true,
            },
            Judges = judges.Count > 0 ? judges : null,   // the models that graded a leaf ([RUN-9])
            StartedAt = AefTime.FromDateTimeOffset(startedAt),
            ContentCapture = AefContentCapture.Off,
            Execution = new AefExecution { TargetMode = options.TargetMode, Stimulus = AefStimulus.Suite },
        };

        var writer = AefRunWriter.Create(directory, header);
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
            ]);

            for (var i = 0; i < cases.Count; i++)
            {
                Add(writer, cases[i], caseShapes[i], cases[i].Metric.Key, CasePath, parent: null, component: null);
            }

            writer.SetSummary(new AefSummary
            {
                Lanes = [new AefSummaryLane(options.Lane, [new AefSummaryEntry { Metric = PassMetric, Path = CasePath }, new AefSummaryEntry { Metric = ScoreMetric, Path = CasePath }])],
                // The preset's own verdict, which no lane recomputes ([RES-6]): its label, score and the rule that decided it.
                Ext = new JsonObject
                {
                    ["agenteval.bench"] = new JsonObject
                    {
                        ["preset"] = preset.Metric.Key,
                        ["label"] = preset.Score.Label,
                        ["score"] = double.IsFinite(preset.Score.Value) ? preset.Score.Value : null,
                        ["strategy"] = preset.Details.AggregationStrategy,
                        ["rulePath"] = preset.Details.Lineage?.RulePath,
                    },
                },
            });
            writer.Close(AefRunStatus.Completed, AefTime.FromDateTimeOffset(endedAt));
            var seal = AefSealer.Seal(directory, new AefSealOptions { SealedBy = AefSealedBy.Producer, Signer = options.Signer });
            return (directory, seal.RunHash);
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
            MeasurementState.NotMeasured => AefState.NotMeasured,
            _ => score.Label switch
            {
                "pass" => AefState.Passed,
                "fail" => AefState.Failed,
                "warn" => AefState.Warn,
                "error" => AefState.Error,
                "skipped" => AefState.Skipped,
                "inapplicable" => AefState.NotApplicable,
                _ => score.Passed ? AefState.Passed : AefState.Failed,
            },
        };
    }

    private static void Add(AefRunWriter writer, EvalResult node, AefComponentShape shape, string caseId, string path,
        AefResultHandle? parent, AefComponent? component)
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
                    ? lineage.Decisive.Where(keyToPath.ContainsKey).Select(k => writer.ResultIdOf(caseId, keyToPath[k])).ToList()
                    : null,
            };
        }

        AefSeverity? severity = state is AefState.Failed or AefState.Warn && AefNames.TryParse<AefSeverity>(node.Score.Severity, out var s) ? s : null;
        var llm = node.Provenance.Type == "atomic-llm" && !string.IsNullOrWhiteSpace(node.Provenance.JudgeModel);
        var handle = writer.AddResult(new AefResult
        {
            CaseId = caseId,
            Path = path,
            Evaluator = new AefEvaluator(AefConverter.Text(node.Metric.Key, 256) ?? "agenteval.eval", AefConverter.Text(node.Metric.Version, 64)),
            State = state,
            // A typed absence says only where its state came from: the eval's own words may quote content ([RUN-11]).
            Reason = measured ? null : $"AgentEval: the eval's label is '{AefConverter.Text(node.Score.Label, 64)}' and its measurement state {node.Score.CensusBucket()}",
            Severity = severity,
            Scores = measured && double.IsFinite(node.Score.Value) ? [new AefScore { Metric = ScoreMetric, Value = node.Score.Value }] : null,
            Annotator = subs.Count > 0 || !measured ? null : new AefAnnotator { Kind = llm ? AefAnnotatorKind.Llm : AefAnnotatorKind.Code, Model = llm ? node.Provenance.JudgeModel : null },
            Aggregation = aggregation,
            Component = component,
            Ext = component is not null && shape.OnFailure != "Averaged"
                ? new JsonObject { ["agenteval.component"] = new JsonObject { ["onFailure"] = shape.OnFailure } }
                : null,
        }, parent);

        for (var i = 0; i < subs.Count; i++)
        {
            var child = described ? shape.Children[i] : Undescribed;
            Add(writer, subs[i], child, caseId, childPaths[i], handle, new AefComponent(child.Weight, child.Required));
        }
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

    private static IEnumerable<EvalResult> Leaves(EvalResult node) =>
        node.Details.SubResults is { Count: > 0 } subs ? subs.SelectMany(Leaves) : [node];

    private static IEnumerable<string> Keys(EvalResult node) =>
        node.Details.SubResults is { Count: > 0 } subs ? subs.SelectMany(s => Keys(s).Prepend(s.Metric.Key)) : [];
}
