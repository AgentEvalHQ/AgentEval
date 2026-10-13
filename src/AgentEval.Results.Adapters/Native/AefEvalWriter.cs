// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>What <c>agenteval eval</c> knows about its run that its results do not hold (S1 #5a).</summary>
public sealed record AefEvalRunOptions
{
    /// <summary>Observed CLI invocation and Git facts; absent for non-CLI producers.</summary>
    public AefCliProvenance? Cli { get; init; }

    /// <summary>Content-free configuration actually applied by the producer.</summary>
    public AefRunConfig? Config { get; init; }

    /// <summary>Pricing policy; defaults to the bundled baseline, with no implied spending limit.</summary>
    public AefCostPolicy? CostPolicy { get; init; } = new() { PriceTable = AgentEval.Models.ModelPricing.TableName };

    /// <summary>The subject's ref (<c>kind:name</c>, [ENC-13]); by default <c>model:</c> and the model or deployment.</summary>
    public required string SubjectRef { get; init; }

    /// <summary>The subject's kind.</summary>
    public AefSubjectKind SubjectKind { get; init; } = AefSubjectKind.Model;

    /// <summary>The subject's exact version (<c>--subject-version</c>); without it the run serves no checkpoint lane ([LANE-1]).</summary>
    public string? SubjectVersion { get; init; }

    /// <summary>The endpoint the subject answered at, if any: written without credentials ([RUN-10]).</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The judge model configured for the run (<c>--judge</c>), if any. The run names it only when it graded at least one
    /// case, since a run's judges are the models that graded it ([RUN-9]).
    /// </summary>
    public string? JudgeModel { get; init; }

    /// <summary>The configured judge provider, when known from client construction.</summary>
    public string? JudgeProvider { get; init; }

    /// <summary>The model used by named LLM metrics, when different from the optional harness judge.</summary>
    public string? MetricJudgeModel { get; init; }

    /// <summary>The named metrics' provider; used with MetricJudgeModel, without guessing from the harness judge.</summary>
    public string? MetricJudgeProvider { get; init; }

    /// <summary>
    /// What the judge consumed over the run, when its responses reported it: the run summary's judge usage entry
    /// ([SUM-7]). It counts every judge call, a call that produced no verdict included.
    /// </summary>
    public AefUsage? JudgeUsage { get; init; }

    /// <summary>Observed totals by role/model; each present role replaces its legacy usage source.</summary>
    public IReadOnlyList<AefUsage>? Usage { get; init; }

    /// <summary>
    /// The gate the command decided on when it closed, if its decision rests on the cases alone: its rule name, such
    /// as <c>all-cases-pass</c>. The run then records it ([GATE-1]): <c>ship</c> (exit 0) when every case passed,
    /// <c>no_ship</c> (exit 1) otherwise, naming the cases that did not pass. Null records none.
    /// </summary>
    public string? GateRule { get; init; }

    /// <summary>The command's final decision; takes precedence over <see cref="GateRule"/> when supplied.</summary>
    public AefCommandGate? Gate { get; init; }

    /// <summary>A content-free reason for an interrupted run; when set the run closes aborted and records no gate.</summary>
    public string? AbortReason { get; init; }

    /// <summary>How the run drove its target ([RUN-7]); a stand-in target is never <c>live</c>.</summary>
    public required AefTargetMode TargetMode { get; init; }

    /// <summary>Whether the run keeps content ([RUN-11]); AgentEval's default is <c>off</c>.</summary>
    public AefContentCapture ContentCapture { get; init; } = AefContentCapture.Off;

    /// <summary>The dataset's name as the suite's ref (a path relative to the workspace, say).</summary>
    public required string DatasetName { get; init; }

    /// <summary>The dataset's bytes: its digest, over LF-normalised text, is the suite's digest (allowed under <c>off</c>).</summary>
    public required byte[] DatasetBytes { get; init; }

    /// <summary>A signer for the seal, if any.</summary>
    public IAefSigner? Signer { get; init; }

    /// <summary>Seal the closed run by default; false leaves verified unsealed evidence and returns no run hash.</summary>
    public bool Seal { get; init; } = true;
}

/// <summary>
/// Writes an <c>agenteval eval</c> run as an AEF run, sealed by default: one root line per case at path <c>case</c> in lane
/// <c>eval</c>, with the metrics <c>pass</c> and <c>score</c> and a summary over them. A case's judge criteria and
/// assertions are its children, at <c>case/criteria/&lt;n&gt;</c> and <c>case/assertions/&lt;n&gt;</c> (1-based), recorded beside
/// its own verdict (<c>Own</c>, §7.5). Named metrics are children at <c>case/metrics/&lt;name&gt;</c>.
/// A case run several times (<c>--runs</c>) has one line per trial and a rollup line
/// ([RES-8]), without children. The run is AgentEval's own (no <c>imported</c>), sealed by its producer by default.
/// </summary>
public static partial class AefEvalWriter
{
    public const string Lane = "eval";
    public const string CasePath = "case";
    public const string PassMetric = "pass";
    public const string ScoreMetric = "score";

    /// <summary>
    /// Maps one harness result to its native root and children, without writing a run. The runner supplies the
    /// lane, timestamps and usage accounting. Criteria, assertions and named metrics preserve the same Own
    /// aggregation as the incremental eval writer; no prompt, response or judge explanation is included.
    /// </summary>
    public static AefEvalCase MapCase(TestResult result, string caseId, int? passingScore = null) =>
        MapCase(result, caseId, passingScore, judgeModel: null);

    /// <summary>Maps a case with known harness and metric judge models; unknown models remain unannotated.</summary>
    public static AefEvalCase MapCase(TestResult result, string caseId, int? passingScore, string? judgeModel, string? metricJudgeModel = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(caseId);
        var (root, children) = MapBaseCase(result, caseId, passingScore, judgeModel);
        var metrics = new List<AefMetric>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in result.MetricResults ?? [])
        {
            if (!names.Add(metric.MetricName))
                throw new ArgumentException("A named metric can be recorded only once per case.", nameof(result));
            ValidateMetricName(metric.MetricName, nameof(result));
            metrics.Add(NamedMetric(metric.MetricName));
            children.Add(MetricChild(caseId, metric, metricJudgeModel ?? judgeModel));
            root = IncludeMetric(root, metric, passingScore);
        }
        return new AefEvalCase(root, children, metrics);
    }

    /// <summary>Writes the run under <paramref name="runsRoot"/>/<c>yyyy/MM/runId</c> and seals it by default; returns its folder and a null run hash when sealing is disabled.</summary>
    /// <param name="passingScores">Each case's passing score, the threshold of its own verdict; null records none.</param>
    /// <param name="runsRoot">The root folder for runs.</param>
    /// <param name="summary">The completed evaluation results.</param>
    /// <param name="caseIds">One stable dataset id per result.</param>
    /// <param name="options">Run identity and recording options.</param>
    /// <param name="startedAt">When evaluation started.</param>
    /// <param name="endedAt">When evaluation and its gate decision finished.</param>
    public static (string Directory, string? RunHash) Write(
        string runsRoot, TestSummary summary, IReadOnlyList<string> caseIds, AefEvalRunOptions options,
        DateTimeOffset startedAt, DateTimeOffset endedAt, IReadOnlyList<int>? passingScores = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (passingScores is not null && passingScores.Count != summary.Results.Count)
        {
            throw new ArgumentException("one passing score per result", nameof(passingScores));
        }

        return WriteRun(runsRoot, summary.Results.Select(r => (IReadOnlyList<TestResult>)[r]).ToList(), caseIds, null, options, startedAt, endedAt, passingScores);
    }

    /// <summary>
    /// Writes a run whose cases each ran <c>n</c> times: a line per trial (<c>trial</c> 0 to n − 1) and a rollup per case
    /// with <c>trials</c>, under <c>AtLeast</c> with <paramref name="requirePasses"/> ([RES-8], [RUN-8]).
    /// </summary>
    public static (string Directory, string? RunHash) WriteTrials(
        string runsRoot, IReadOnlyList<IReadOnlyList<TestResult>> trials, IReadOnlyList<string> caseIds, int requirePasses,
        AefEvalRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(trials);
        if (trials.Count == 0 || trials.Any(t => t.Count != trials[0].Count) || trials[0].Count is < 1 or > 1000)
        {
            throw new ArgumentException("every case needs the same number of trials, 1 to 1000", nameof(trials));
        }

        if (requirePasses < 1 || requirePasses > trials[0].Count)
        {
            throw new ArgumentOutOfRangeException(nameof(requirePasses), requirePasses, "requirePasses is 1 to the number of trials (RUN-8).");
        }

        return WriteRun(runsRoot, trials, caseIds, requirePasses, options, startedAt, endedAt);
    }

    /// <summary>
    /// The least number of passing trials out of <paramref name="trials"/> that meets a pass-share threshold, compared as
    /// AgentEval's stochastic runner compares it (pass rate ≥ threshold): how a share threshold is written as
    /// <c>AtLeast</c> ([RES-8]). A threshold that zero passes meet has no <c>AtLeast</c> form, since <c>requirePasses</c>
    /// is at least 1.
    /// </summary>
    public static int RequirePasses(double threshold, int trials)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, 1);
        for (var k = 0; k <= trials; k++)
        {
            if ((double)k / trials >= threshold)
            {
                return k > 0 ? k : throw new ArgumentOutOfRangeException(nameof(threshold), threshold,
                    "a threshold that zero passing trials meet cannot be written as AtLeast (requirePasses is at least 1)");
            }
        }

        throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "no number of passing trials meets the threshold");
    }

    /// <summary>Seals the completed trials of an interrupted run, retaining its original execution policy.</summary>
    public static (string Directory, string? RunHash) WriteAbortedTrials(
        string runsRoot, IReadOnlyList<IReadOnlyList<TestResult>> trials, IReadOnlyList<string> caseIds,
        int plannedTrials, int requirePasses, AefEvalRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(options);
        if (options.AbortReason is null) throw new ArgumentException("An interrupted run needs an abort reason.", nameof(options));
        if (plannedTrials is < 1 or > 1000 || requirePasses < 1 || requirePasses > plannedTrials)
            throw new ArgumentOutOfRangeException(nameof(plannedTrials), "The original policy requires 1 to 1000 trials and a pass count within it.");
        if (trials.Any(t => t.Count < 1 || t.Count > plannedTrials))
            throw new ArgumentException("Only cases with completed trials belong in an interrupted run.", nameof(trials));
        return WriteRun(runsRoot, trials, caseIds, requirePasses, options, startedAt, endedAt, plannedTrials: plannedTrials);
    }

    private static (string Directory, string? RunHash) WriteRun(
        string runsRoot, IReadOnlyList<IReadOnlyList<TestResult>> cases, IReadOnlyList<string> caseIds, int? requirePasses,
        AefEvalRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt, IReadOnlyList<int>? passingScores = null,
        int? plannedTrials = null)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        ArgumentNullException.ThrowIfNull(options);
        if (caseIds.Count != cases.Count)
        {
            throw new ArgumentException("one case id per case", nameof(caseIds));
        }

        using var session = Start(runsRoot, options, startedAt,
            plannedTrials ?? (requirePasses is null ? null : cases[0].Count), requirePasses);
        try
        {
            for (var i = 0; i < cases.Count; i++)
                foreach (var result in cases[i]) session.Add(result, caseIds[i], passingScores?[i]);
            return session.Complete(endedAt);
        }
        catch
        {
            AefConverter.Discard(session.Directory, existed: false);
            throw;
        }
    }
    /// <summary>
    /// <c>subject.kind</c> for a ref: the part before its colon when it is one of <c>subject.kind</c>'s values, and
    /// <c>other</c> otherwise, as a runner derives it ([PLAN-10]).
    /// </summary>
    public static AefSubjectKind KindOf(string subjectRef)
    {
        ArgumentNullException.ThrowIfNull(subjectRef);
        var colon = subjectRef.IndexOf(':', StringComparison.Ordinal);
        return (colon > 0 ? subjectRef[..colon] : "") switch
        {
            "agent" => AefSubjectKind.Agent,
            "workflow" => AefSubjectKind.Workflow,
            "model" => AefSubjectKind.Model,
            "endpoint" => AefSubjectKind.Endpoint,
            "mcp-server" => AefSubjectKind.McpServer,
            _ => AefSubjectKind.Other,
        };
    }

    /// <summary>A ref (<c>kind:name</c>) with the name encoded as [ENC-13] encodes one.</summary>
    public static string Ref(string kind, string name) => AefConverter.TypedRef(kind, name);

    /// <summary>The suite digest: SHA-256 of the dataset's text with CRLF and CR turned into LF, so a checkout's line endings do not change it.</summary>
    public static string DatasetDigest(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return AefConverter.Sha256Of(Encoding.UTF8.GetBytes(text));
    }

    // The judge graded a case when it returned criteria results and a verdict ([RUN-9]).
    private static bool Graded(TestResult result) => result.CriteriaResults is not null && !result.JudgeFailed;

    // The run's total usage, one entry per role and model ([SUM-7]): the agent's, summed over the cases by the model that
    // answered, and the judge's, as its responses reported it. A total is complete or absent: the agent's figures are
    // written only when every case reported its performance, and each field only when every case of that model has it.
    private static List<AefUsage>? Totals(IReadOnlyList<TestResult> results, AefUsage? judge)
    {
        var totals = results.Any(r => r.Performance is null) ? new List<AefUsage>() : results
            .Select(r => r.Performance!)
            .GroupBy(p => AefConverter.Text(p.ModelUsed, 256), StringComparer.Ordinal)
            .Select(g => new AefUsage
            {
                Role = AefUsageRole.Agent,
                Model = g.Key,
                InputTokens = g.All(p => p.PromptTokens is not null) ? g.Sum(p => (long)p.PromptTokens!.Value) : null,
                OutputTokens = g.All(p => p.CompletionTokens is not null) ? g.Sum(p => (long)p.CompletionTokens!.Value) : null,
                CostUsd = g.All(p => p.EstimatedCost is not null) ? g.Sum(p => (double)p.EstimatedCost!.Value) : null,
                CostSource = g.All(p => p.EstimatedCost is not null) ? "AgentEval's estimate from the reported token usage" : null,
            })
            .Where(u => u.InputTokens is not null || u.OutputTokens is not null || u.CostUsd is not null)
            .ToList();
        if (judge is not null)
        {
            totals.Add(judge with { Role = AefUsageRole.Judge });
        }

        return totals.Count > 0 ? totals : null;
    }

    // A judge that produced no verdict measured nothing: its Passed and Score are placeholders, so the case is an error
    // (a typed absence, [RES-1]), never a failure.
    private static AefState StateOf(TestResult result) =>
        result.Error is not null || result.JudgeFailed ? AefState.Error : result.Passed ? AefState.Passed : AefState.Failed;

    // A case's result over its trials ([RES-8]): passed when at least requirePasses trials passed; failed when they could
    // not have, even had every unmeasured trial passed; otherwise not decided, an error. Its score is the mean of the
    // measured trials'. Usage and duration stay on the trial lines, so nothing is counted twice.
    private static AefResult Rollup(IReadOnlyList<TestResult> trials, string caseId, int requirePasses, int? plannedTrials = null)
    {
        var states = trials.Select(StateOf).ToList();
        var passed = states.Count(s => s == AefState.Passed);
        var unmeasured = states.Count(s => s == AefState.Error);
        var incomplete = plannedTrials is { } planned && trials.Count < planned;
        var state = incomplete ? AefState.Error : passed >= requirePasses ? AefState.Passed
            : passed + unmeasured >= requirePasses ? AefState.Error
            : AefState.Failed;
        var measured = trials.Where(t => StateOf(t) != AefState.Error).ToList();
        return new AefResult
        {
            CaseId = caseId,
            Path = CasePath,
            Evaluator = new AefEvaluator("agenteval.harness", AefConverter.Version),
            State = state,
            Reason = incomplete ? "The case was interrupted before all requested trials completed" : state == AefState.Error
                ? $"{unmeasured} of {trials.Count} trials were not measured, so whether at least {requirePasses} pass is undecided"
                : null,
            Scores = state == AefState.Error || measured.Count == 0 ? null : [new AefScore { Metric = ScoreMetric, Value = measured.Average(t => (double)t.Score) }],
            Lane = Lane,
            Trials = new AefTrials(trials.Count, passed, AefTrialAggregation.AtLeast, states.Distinct().Count() == 1),
        };
    }

    // A case and its children: the judge's criteria (graded by the judge, so no text of them is written) and the
    // assertions (no name or message: either may quote the expected text or the response). The
    // case's verdict is its own, recorded beside them: Own, with each child weight 0 and not required (§7.5).
    private static (AefResultHandle Handle, AefResult Root) AddCase(AefRunWriter writer, TestResult result, string caseId, int? passingScore, AefNativeContent? content = null, string? judgeModel = null)
    {
        var (root, children) = MapBaseCase(result, caseId, passingScore, judgeModel);
        root = AefNativeContent.Attach(writer, root, content);
        var handle = writer.AddResult(root);
        foreach (var child in children) writer.AddResult(child, handle);
        return (handle, root);
    }

    private static (AefResult Root, List<AefResult> Children) MapBaseCase(TestResult result, string caseId, int? passingScore, string? judgeModel)
    {
        var children = new List<AefResult>();
        var criteria = result.JudgeFailed ? [] : result.CriteriaResults ?? [];
        for (var n = 0; n < criteria.Count; n++)
        {
            children.Add(Child(caseId, $"{CasePath}/criteria/{n + 1}", "agenteval.judge.criterion", criteria[n].Met ? AefState.Passed : AefState.Failed) with
            {
                Annotator = HarnessAnnotator(result, judgeModel),
            });
        }

        var assertions = result.AssertionResults ?? [];
        for (var n = 0; n < assertions.Count; n++)
        {
            var assertion = assertions[n];
            var state = assertion.Outcome switch
            {
                AssertionOutcome.Passed => AefState.Passed,
                AssertionOutcome.Failed => AefState.Failed,
                _ => AefState.Inconclusive,
            };
            // Not the assertion's name: it can quote the expected text (Contain("…"), a regex), which is content ([RUN-11]).
            children.Add(Child(caseId, $"{CasePath}/assertions/{n + 1}", "agenteval.assertion", state) with
            {
                Annotator = new AefAnnotator { Kind = AefAnnotatorKind.Code },
            });
        }

        var root = Line(result, caseId, trial: null, judgeModel);
        if (children.Count > 0)
        {
            var measuredCase = root.State != AefState.Error;
            root = root with
            {
                Annotator = null, // The children identify graders; this node records the harness decision.
                Aggregation = new AefAggregation
                {
                    Strategy = AefAggregationStrategy.Own,
                    // An error case's verdict was never measured (the evaluation threw, or the judge gave none).
                    RulePath = measuredCase ? AefRulePath.Threshold : AefRulePath.NothingMeasured,
                    Score = measuredCase ? result.Score : null,
                    Threshold = measuredCase ? passingScore : null,
                    Measured = children.Count,   // passed, failed and inconclusive children are all measured (SUM-4)
                    Total = children.Count,
                },
            };
        }

        return (root, children);
    }

    private static void ValidateMetricName(string name, string parameterName)
    {
        if (name is PassMetric or ScoreMetric)
            throw new ArgumentException("Named metrics cannot use the reserved harness metric names pass or score.", parameterName);
    }

    private static AefMetric NamedMetric(string name) => new()
    {
        Id = name, Kind = AefMetricKind.Score, Direction = AefMetricDirection.None, Scale = AefScale.Between(0, 100),
    };

    private static AefResult MetricChild(string caseId, AgentEval.Core.MetricResult metric, string? judgeModel = null) =>
        Child(caseId, $"{CasePath}/metrics/{Uri.EscapeDataString(metric.MetricName)}", "agenteval.metric",
            metric.Measured ? metric.Passed ? AefState.Passed : AefState.Failed : AefState.NotMeasured) with
        {
            Annotator = !metric.Measured ? null
                : metric.ModelGraded != false && metric.MetricName.StartsWith("llm_", StringComparison.Ordinal) && judgeModel is not null
                    ? new AefAnnotator { Kind = AefAnnotatorKind.Llm, Model = judgeModel, RubricDigest = metric.RubricDigest }
                : metric.ModelGraded == false || metric.MetricName.StartsWith("code_", StringComparison.Ordinal)
                    ? new AefAnnotator { Kind = AefAnnotatorKind.Code } : null,
            Scores = metric.Measured ? [new AefScore { Metric = metric.MetricName, Value = metric.Score }] : null,
            Reason = metric.Measured ? null : "The metric could not be measured",
        };

    private static AefResult IncludeMetric(AefResult root, AgentEval.Core.MetricResult metric, int? passingScore)
    {
        var previous = root.Aggregation;
        var absent = (previous?.Unmeasured?.NotMeasured ?? 0) + (metric.Measured ? 0 : 1);
        return root with
        {
            Annotator = null,
            Aggregation = new AefAggregation
            {
                Strategy = AefAggregationStrategy.Own,
                RulePath = root.State == AefState.Error ? AefRulePath.NothingMeasured : AefRulePath.Threshold,
                Score = root.State == AefState.Error ? null : root.Scores![0].Value,
                Threshold = root.State == AefState.Error ? null : passingScore,
                Measured = (previous?.Measured ?? 0) + (metric.Measured ? 1 : 0),
                Total = (previous?.Total ?? 0) + 1,
                Unmeasured = absent == 0 ? null : new AefUnmeasured { NotMeasured = absent },
            },
        };
    }

    private static AefResult Child(string caseId, string path, string evaluator, AefState state) => new()
    {
        CaseId = caseId,
        Path = path,
        Evaluator = new AefEvaluator(evaluator, null),
        State = state,
        Lane = Lane,
        Component = new AefComponent(0, false),
    };

    private static AefAnnotator? HarnessAnnotator(TestResult result, string? judgeModel) =>
        Graded(result) && judgeModel is not null
            ? new AefAnnotator { Kind = AefAnnotatorKind.Llm, Model = judgeModel, RubricDigest = result.JudgeRubricDigest }
            : null;

    private static AefResult Line(TestResult result, string caseId, int? trial, string? judgeModel = null)
    {
        var state = StateOf(result);
        var performance = result.Performance;
        var usage = performance is null || (performance.PromptTokens is null && performance.CompletionTokens is null && performance.EstimatedCost is null)
            ? null
            : new List<AefUsage>
            {
                new()
                {
                    Role = AefUsageRole.Agent,
                    Model = AefConverter.Text(performance.ModelUsed, 256),
                    InputTokens = performance.PromptTokens,
                    OutputTokens = performance.CompletionTokens,
                    CostUsd = performance.EstimatedCost is { } c ? (double)c : null,
                    CostSource = performance.EstimatedCost is null ? null : "AgentEval's estimate from the reported token usage",
                },
            };
        return new AefResult
        {
            CaseId = caseId,
            Path = CasePath,
            Evaluator = new AefEvaluator("agenteval.harness", AefConverter.Version),
            State = state,
            // An error's reason names its kind only: its message may quote the subject's or the judge's text ([RUN-11]).
            Reason = result.Error is not null ? $"the evaluation threw {result.Error.GetType().Name}"
                : result.JudgeFailed ? "the judge produced no verdict (an evaluation-infrastructure failure, not a grade)"
                : null,
            Scores = state == AefState.Error ? null : [new AefScore { Metric = ScoreMetric, Value = result.Score }],
            Lane = Lane,
            DurationMs = performance is { } p && p.EndTime > p.StartTime ? (p.EndTime - p.StartTime).TotalMilliseconds : null,
            Usage = usage,   // no test name: without a dataset id it is the start of the input ([RUN-11])
            Annotator = state == AefState.Error ? null : HarnessAnnotator(result, judgeModel),
            Trial = trial,
        };
    }
}

/// <summary>A native harness case, its immediate child evidence and any named metric declarations.</summary>
/// <param name="Root">The harness's own decision.</param>
/// <param name="Children">Criteria, assertions and named metrics in their recording order.</param>
/// <param name="Metrics">Named metrics used by the children, excluding the standard pass and score declarations.</param>
public sealed record AefEvalCase(AefResult Root, IReadOnlyList<AefResult> Children, IReadOnlyList<AefMetric> Metrics);
