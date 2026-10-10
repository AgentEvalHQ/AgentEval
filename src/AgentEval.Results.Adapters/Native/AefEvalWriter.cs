// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
/// <c>eval</c>, with the metrics <c>pass</c> and <c>score</c> and a summary over them. The run is AgentEval's own
/// (no <c>imported</c>), sealed by its producer.
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
        ArgumentNullException.ThrowIfNull(caseIds);
        ArgumentNullException.ThrowIfNull(options);
        if (caseIds.Count != summary.Results.Count)
        {
            throw new ArgumentException("one case id per result", nameof(caseIds));
        }

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
            },
            Judges = options.JudgeModel is not null && summary.Results.Any(Graded)
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
                    Description = "The case's score from AgentEval's evaluation harness (0 to 100).",
                },
            ]);

            for (var i = 0; i < summary.Results.Count; i++)
            {
                writer.AddResult(Root(summary.Results[i], caseIds[i]));
            }

            var cost = summary.Results.Select(r => r.Performance?.EstimatedCost).Where(c => c is not null).Sum(c => (double)c!.Value);
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

    private static AefResult Root(TestResult result, string caseId)
    {
        // A judge that produced no verdict measured nothing: its Passed and Score are placeholders, so the case is an
        // error (a typed absence, [RES-1]), never a failure.
        var state = result.Error is not null || result.JudgeFailed ? AefState.Error : result.Passed ? AefState.Passed : AefState.Failed;
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
            Usage = usage,
            Ext = new JsonObject { ["agenteval.eval"] = new JsonObject { ["testName"] = AefConverter.Text(result.TestName, 1024) } },
        };
    }
}
