// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using AgentEval.Models;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>What <c>agenteval eval</c> knows about its run that its results do not hold (S1 #5a).</summary>
public sealed record AefEvalRunOptions
{
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
}

/// <summary>
/// Writes an <c>agenteval eval</c> run as a sealed AEF run: one root line per case at path <c>case</c> in lane
/// <c>eval</c>, with the metrics <c>pass</c> and <c>score</c> and a summary over them. A case run several times
/// (<c>--runs</c>) has one line per trial and a rollup line ([RES-8]). The run is AgentEval's own (no
/// <c>imported</c>), sealed by its producer.
/// </summary>
public static class AefEvalWriter
{
    public const string Lane = "eval";
    public const string CasePath = "case";
    public const string PassMetric = "pass";
    public const string ScoreMetric = "score";

    /// <summary>Writes the run under <paramref name="runsRoot"/>/<c>yyyy/MM/runId</c> and seals it; returns its folder and run hash.</summary>
    public static (string Directory, string RunHash) Write(
        string runsRoot, TestSummary summary, IReadOnlyList<string> caseIds, AefEvalRunOptions options,
        DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return WriteRun(runsRoot, summary.Results.Select(r => (IReadOnlyList<TestResult>)[r]).ToList(), caseIds, null, options, startedAt, endedAt);
    }

    /// <summary>
    /// Writes a run whose cases each ran <c>n</c> times: a line per trial (<c>trial</c> 0 to n − 1) and a rollup per case
    /// with <c>trials</c>, under <c>AtLeast</c> with <paramref name="requirePasses"/> ([RES-8], [RUN-8]).
    /// </summary>
    public static (string Directory, string RunHash) WriteTrials(
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

    private static (string Directory, string RunHash) WriteRun(
        string runsRoot, IReadOnlyList<IReadOnlyList<TestResult>> cases, IReadOnlyList<string> caseIds, int? requirePasses,
        AefEvalRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        ArgumentNullException.ThrowIfNull(options);
        if (caseIds.Count != cases.Count)
        {
            throw new ArgumentException("one case id per case", nameof(caseIds));
        }

        var all = cases.SelectMany(c => c).ToList();

        var runId = $"{startedAt.UtcDateTime:yyyyMMddTHHmmssZ}-eval-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var directory = Path.Combine(runsRoot, $"{startedAt.UtcDateTime:yyyy}", $"{startedAt.UtcDateTime:MM}", runId);
        var digest = DatasetDigest(options.DatasetBytes);
        var endpoint = options.Endpoint is null ? null : AefConverter.Endpoint(AefConverter.WithoutCredentials(options.Endpoint));
        var header = new AefRunHeader
        {
            RunId = runId,
            Producer = AefConverter.DefaultProducer,
            Subject = new AefSubject { Ref = options.SubjectRef, Kind = options.SubjectKind, Version = options.SubjectVersion },
            Deployment = endpoint is null ? null : new AefDeployment { Ref = AefConverter.TypedRef("endpoint", endpoint), Endpoint = endpoint },
            Suite = new AefSuite
            {
                Ref = AefConverter.TypedRef("dataset", options.DatasetName),
                Version = "sha256-" + digest["sha256:".Length..][..12],
                Digest = digest,
                Frozen = true,
                ExecutionPolicy = requirePasses is { } k
                    ? new AefExecutionPolicy { TrialsPerCase = cases[0].Count, RequirePasses = k, Aggregation = AefTrialAggregation.AtLeast }
                    : null,
            },
            Judges = options.JudgeModel is not null && all.Any(Graded)
                ? [new AefJudge { Model = options.JudgeModel, Mode = AefJudgeMode.Single }]
                : null,
            StartedAt = AefTime.FromDateTimeOffset(startedAt),
            ContentCapture = options.ContentCapture,
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
                    Id = ScoreMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 100),
                    Description = "The case's score from AgentEval's evaluation harness (0 to 100); for a case run in trials, the mean over its measured trials.",
                },
            ]);

            for (var i = 0; i < cases.Count; i++)
            {
                if (requirePasses is not { } required)
                {
                    writer.AddResult(Line(cases[i][0], caseIds[i], trial: null));
                    continue;
                }

                for (var t = 0; t < cases[i].Count; t++)
                {
                    writer.AddResult(Line(cases[i][t], caseIds[i], t));
                }

                writer.AddResult(Rollup(cases[i], caseIds[i], required));
            }

            var cost = all.Select(r => r.Performance?.EstimatedCost).Where(c => c is not null).Sum(c => (double)c!.Value);
            writer.SetSummary(new AefSummary
            {
                Lanes = [new AefSummaryLane(Lane, [new AefSummaryEntry { Metric = PassMetric, Path = CasePath }, new AefSummaryEntry { Metric = ScoreMetric, Path = CasePath }])],
                Cost = cost > 0 ? new AefCost(cost, "AgentEval's estimate from the agent's reported token usage; judge calls are not included") : null,
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

    // A judge that produced no verdict measured nothing: its Passed and Score are placeholders, so the case is an error
    // (a typed absence, [RES-1]), never a failure.
    private static AefState StateOf(TestResult result) =>
        result.Error is not null || result.JudgeFailed ? AefState.Error : result.Passed ? AefState.Passed : AefState.Failed;

    // A case's result over its trials ([RES-8]): passed when at least requirePasses trials passed; failed when they could
    // not have, even had every unmeasured trial passed; otherwise not decided, an error. Its score is the mean of the
    // measured trials'. Usage and duration stay on the trial lines, so nothing is counted twice.
    private static AefResult Rollup(IReadOnlyList<TestResult> trials, string caseId, int requirePasses)
    {
        var states = trials.Select(StateOf).ToList();
        var passed = states.Count(s => s == AefState.Passed);
        var unmeasured = states.Count(s => s == AefState.Error);
        var state = passed >= requirePasses ? AefState.Passed
            : passed + unmeasured >= requirePasses ? AefState.Error
            : AefState.Failed;
        var measured = trials.Where(t => StateOf(t) != AefState.Error).ToList();
        return new AefResult
        {
            CaseId = caseId,
            Path = CasePath,
            Evaluator = new AefEvaluator("agenteval.harness", AefConverter.Version),
            State = state,
            Reason = state == AefState.Error
                ? $"{unmeasured} of {trials.Count} trials were not measured, so whether at least {requirePasses} pass is undecided"
                : null,
            Scores = state == AefState.Error || measured.Count == 0 ? null : [new AefScore { Metric = ScoreMetric, Value = measured.Average(t => (double)t.Score) }],
            Lane = Lane,
            Trials = new AefTrials(trials.Count, passed, AefTrialAggregation.AtLeast, states.Distinct().Count() == 1),
        };
    }

    private static AefResult Line(TestResult result, string caseId, int? trial)
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
            Trial = trial,
        };
    }
}
