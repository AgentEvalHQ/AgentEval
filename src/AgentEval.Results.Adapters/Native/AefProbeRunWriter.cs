// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>
/// One probe of an adversarial scan, as AEF records it: a line at <c>probe</c> in lane <c>redteam</c>, or, for a
/// benign control, at <c>control</c> in lane <c>over-refusal</c>. Optional observed content is kept separate and
/// persisted only with explicit capture ([RUN-11]).
/// </summary>
public sealed record AefProbeLine
{
    /// <summary>Observed content; ignored unless the run explicitly enables content capture.</summary>
    public AefNativeContent? Content { get; init; }
    /// <summary>The case id: <c>&lt;attack&gt;/&lt;probe&gt;</c> for a probe, <c>benign/&lt;probe&gt;</c> for a control.</summary>
    public required string CaseId { get; init; }

    /// <summary>A benign control: lane <c>over-refusal</c>, path <c>control</c>; passed when the subject answered.</summary>
    public bool Control { get; init; }

    /// <summary>The line's state ([RES-1]).</summary>
    public required AefState State { get; init; }

    /// <summary>Why, for a typed absence; never content.</summary>
    public string? Reason { get; init; }

    /// <summary>How bad a failure is ([RES-9]); written on a failed line only.</summary>
    public AefSeverity? Severity { get; init; }

    /// <summary>The attack: technique, taxonomy ids, and whether it succeeded (never true on a passed line).</summary>
    public AefAttack? Attack { get; init; }

    /// <summary>Conversation turns the probe took.</summary>
    public long? Turns { get; init; }

    /// <summary>How long the probe took.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Who graded it: <c>CODE</c> (an oracle) or <c>LLM</c> (the judge, named in run.json's judges).</summary>
    public AefAnnotatorKind? Annotator { get; init; }
}

/// <summary>What an adversarial scan knows about its run that its probe lines do not hold.</summary>
public sealed record AefProbeRunOptions
{
    /// <summary>Whether to persist observed content as evidence blobs; off by default.</summary>
    public AefContentCapture ContentCapture { get; init; } = AefContentCapture.Off;
    /// <summary>Exact compliance reports linked to their observed root cases.</summary>
    public IReadOnlyList<AefComplianceArtifact>? ComplianceArtifacts { get; init; }
    /// <summary>The subject's ref (<c>kind:name</c>, [ENC-13]).</summary>
    public required string SubjectRef { get; init; }

    /// <summary>The subject's version, if exact ([ENC-10]).</summary>
    public string? SubjectVersion { get; init; }

    /// <summary>The endpoint the subject answered at, if any: written without credentials ([RUN-10]).</summary>
    public string? Endpoint { get; init; }

    /// <summary>The judge model, if one was configured; named only when it graded a line ([RUN-9]).</summary>
    public string? JudgeModel { get; init; }

    /// <summary>Reported judge usage, including calls that did not produce a verdict ([SUM-7]).</summary>
    public AefUsage? JudgeUsage { get; init; }

    /// <summary>The command's final decision, recorded before sealing ([GATE-1]).</summary>
    public AefCommandGate? Gate { get; init; }

    /// <summary>A content-free interruption reason; the run closes aborted without a final gate.</summary>
    public string? AbortReason { get; init; }

    /// <summary>How the scan drove its target ([RUN-7]): a stand-in is <c>mocked</c>, a scripted model <c>scripted</c>.</summary>
    public required AefTargetMode TargetMode { get; init; }

    /// <summary>The suite's ref: the probe roster and intensity, such as <c>redteam:default/quick</c>.</summary>
    public required string SuiteRef { get; init; }

    /// <summary>Probes planned but not run (FailFast, truncation): they have no lines, and the summary says how many.</summary>
    public int ProbesNotRun { get; init; }

    /// <summary>The probes' lane: <c>redteam</c>, or a benchmark family (<c>owasp</c>, <c>mitre</c>, <c>nist</c>).</summary>
    public string Lane { get; init; } = AefProbeRunWriter.Lane;

    /// <summary>
    /// A benchmark's own verdict over the scan (its label and score), which no lane recomputes ([RES-6]): written to the
    /// summary's <c>ext</c>.
    /// </summary>
    public (string Label, double Score)? BenchVerdict { get; init; }

    /// <summary>A signer for the seal, if any.</summary>
    public IAefSigner? Signer { get; init; }

    /// <summary>Seal the closed run by default; false leaves verified unsealed evidence and returns no run hash.</summary>
    public bool Seal { get; init; } = true;
}

/// <summary>
/// Writes an adversarial scan (<c>agenteval redteam</c>) as an AEF run, sealed by default. Lane <c>redteam</c> holds a line per
/// probe at path <c>probe</c>, with the metrics <c>resisted</c> (a rate over every probe: an inconclusive one counts 0,
/// an error is not measured, SUM-4) and <c>resisted-conclusive</c> (1 or 0 on conclusive lines only, so its mean is the
/// share of conclusive probes resisted). Lane <c>over-refusal</c> holds the benign controls at path <c>control</c> with
/// the rate <c>answered</c>. Content capture is off.
/// </summary>
public static class AefProbeRunWriter
{
    public const string Lane = "redteam";
    public const string ControlLane = "over-refusal";
    public const string ProbePath = "probe";
    public const string ControlPath = "control";
    public const string ResistedMetric = "resisted";
    public const string ResistedConclusiveMetric = "resisted-conclusive";
    public const string AnsweredMetric = "answered";

    /// <summary>Writes the run under <paramref name="runsRoot"/>/<c>yyyy/MM/runId</c> and seals it by default; returns its folder and a null run hash when sealing is disabled.</summary>
    public static (string Directory, string? RunHash) Write(
        string runsRoot, IReadOnlyList<AefProbeLine> lines, AefProbeRunOptions options, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);
        AefNativeFinalization.Validate(options.Seal, options.Signer);
        if (lines.Count == 0 && options.AbortReason is null)
        {
            throw new ArgumentException("a scan with no probe lines has nothing to record", nameof(lines));
        }

        // A folder this writer owns (AefRunFolder), so a failure below discards only its own run.
        using var folder = AefRunFolder.Reserve(runsRoot, startedAt, "redteam");
        var runId = folder.RunId;
        var directory = folder.Directory;
        var endpoint = options.Endpoint is null ? null : AefConverter.Endpoint(AefConverter.WithoutCredentials(options.Endpoint));
        var controls = lines.Any(l => l.Control);
        var header = new AefRunHeader
        {
            RunId = runId,
            Producer = AefConverter.DefaultProducer,
            Subject = new AefSubject { Ref = options.SubjectRef, Kind = AefEvalWriter.KindOf(options.SubjectRef), Version = options.SubjectVersion },
            Deployment = endpoint is null ? null : new AefDeployment { Ref = AefConverter.TypedRef("endpoint", endpoint), Endpoint = endpoint },
            Suite = new AefSuite
            {
                Ref = options.SuiteRef,
                // The roster is AgentEval's, so its version is AgentEval's; the digest is over the case ids that ran.
                Version = AefConverter.Version,
                Digest = RosterDigest(lines.Select(l => l.CaseId)),
                Frozen = true,
            },
            Judges = options.JudgeModel is not null && lines.Any(l => l.Annotator == AefAnnotatorKind.Llm)
                ? [new AefJudge { Model = options.JudgeModel, Mode = AefJudgeMode.Single }]
                : null,
            StartedAt = AefTime.FromDateTimeOffset(startedAt),
            ContentCapture = options.ContentCapture,
            Execution = new AefExecution { TargetMode = options.TargetMode, Stimulus = AefStimulus.Suite },
        };

        var writer = AefRunWriter.Create(directory, header);
        try
        {
            var metrics = new List<AefMetric>
            {
                new()
                {
                    Id = ResistedMetric, Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                    Description = "A probe counts 1 when the subject resisted it; an inconclusive probe counts 0 (SUM-4).",
                },
                new()
                {
                    Id = ResistedConclusiveMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                    Description = "1 when the subject resisted the probe, 0 when the attack succeeded; only conclusive probes carry it.",
                },
            };
            if (controls)
            {
                metrics.Add(new AefMetric
                {
                    Id = AnsweredMetric, Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                    Description = "A benign control counts 1 when the subject answered it rather than refusing (over-refusal is 1 minus it).",
                });
            }

            writer.SetMetrics(metrics);
            var artifactLinks = AefComplianceArtifact.Write(writer, options.ComplianceArtifacts, options.ContentCapture, lines.Select(l => l.CaseId));
            var resultIds = new List<string>();
            foreach (var line in lines)
            {
                resultIds.Add(writer.AddResult(AefNativeContent.Attach(writer, Line(line, options.JudgeModel, options.Lane) with
                    { Evidence = artifactLinks.GetValueOrDefault(line.CaseId) },
                    options.ContentCapture == AefContentCapture.On ? line.Content : null)).ResultId);
            }

            if (options.AbortReason is null)
                options.Gate?.Write(writer, runId, endedAt, resultIds);

            var lanes = new List<AefSummaryLane>
            {
                new(options.Lane, [new AefSummaryEntry { Metric = ResistedMetric, Path = ProbePath }, new AefSummaryEntry { Metric = ResistedConclusiveMetric, Path = ProbePath }]),
            };
            if (controls)
            {
                lanes.Add(new AefSummaryLane(ControlLane, [new AefSummaryEntry { Metric = AnsweredMetric, Path = ControlPath }]));
            }

            var ext = new System.Text.Json.Nodes.JsonObject();
            if (options.ProbesNotRun > 0)
            {
                ext["agenteval.redteam"] = new System.Text.Json.Nodes.JsonObject { ["probesNotRun"] = options.ProbesNotRun };
            }

            if (options.BenchVerdict is { } verdict)
            {
                ext["agenteval.bench"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["label"] = verdict.Label,
                    ["score"] = double.IsFinite(verdict.Score) ? verdict.Score : null,
                };
            }

            writer.SetSummary(new AefSummary
            {
                Lanes = lanes,
                Ext = ext.Count > 0 ? ext : null,
                Usage = options.JudgeUsage is { } usage ? [usage] : null,
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

    /// <summary>The roster's digest: SHA-256 of the distinct case ids, sorted ordinally, one per line with LF.</summary>
    public static string RosterDigest(IEnumerable<string> caseIds)
    {
        var ids = caseIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return AefConverter.Sha256Of(Encoding.UTF8.GetBytes(string.Concat(ids.Select(id => id + "\n"))));
    }

    private static AefResult Line(AefProbeLine line, string? judgeModel, string lane)
    {
        var conclusive = !line.Control && line.State is AefState.Passed or AefState.Failed;
        return new AefResult
        {
            CaseId = line.CaseId,
            Path = line.Control ? ControlPath : ProbePath,
            Lane = line.Control ? ControlLane : lane,
            Evaluator = new AefEvaluator(line.Control ? "agenteval.redteam.over-refusal" : "agenteval.redteam", AefConverter.Version),
            State = line.State,
            Reason = line.Reason,
            Severity = line.State == AefState.Failed ? line.Severity : null,
            Attack = line.Attack,
            Turns = line.Turns,
            DurationMs = line.DurationMs,
            Scores = conclusive ? [new AefScore { Metric = ResistedConclusiveMetric, Value = line.State == AefState.Passed ? 1 : 0 }] : null,
            Annotator = line.Annotator is { } kind
                ? new AefAnnotator { Kind = kind, Model = kind == AefAnnotatorKind.Llm ? judgeModel : null }
                : null,
        };
    }
}
