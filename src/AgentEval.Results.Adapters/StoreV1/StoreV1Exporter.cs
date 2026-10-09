// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

/// <summary>How <see cref="StoreV1Exporter"/> writes the AEF run.</summary>
public sealed record StoreV1ExportOptions : AefConversionOptions
{
    /// <summary>
    /// How the run's target was driven ([RUN-7]). Store v1 records no provider, so it cannot tell a run against the
    /// real subject from one against a stand-in: the value is the exporter's claim (listed in <c>imported.asserted</c>).
    /// Default <see cref="AefTargetMode.Mocked"/>, the reading that never passes a run off as evidence about the live
    /// subject; say <see cref="AefTargetMode.Live"/> for a run that drove the real subject.
    /// </summary>
    public AefTargetMode TargetMode { get; init; } = AefTargetMode.Mocked;
}

/// <summary>
/// Exports a run of AgentEval's output store as it was before AEF (<c>.agenteval/</c>, "store v1": its manifest,
/// summary, scenario files, agent trace and compliance evidence, read through <see cref="IOutputStoreReader"/>) to an
/// AEF 1.0 run folder, as contracts/aef/1/spec/07-versioning.md §7.5 maps it:
/// <list type="bullet">
/// <item>the run is an imported run ([RUN-15]): <c>producer</c> is the exporter, <c>imported.from</c> names store v1 and
/// the AgentEval version that wrote it, and <c>imported.asserted</c> lists every run.json field the exporter supplied;</item>
/// <item>each scenario is a root line at <see cref="ScenarioPath"/>, each of its assertions a child line at
/// <see cref="AssertionPathPrefix"/> and its 1-based position;</item>
/// <item>a scenario's state comes from its eval-result tree (<c>ScenarioResult.Output</c>, when it holds one): its
/// <c>MeasurementState</c> and label (<c>pass</c> → <c>passed</c>, <c>fail</c> → <c>failed</c>, <c>warn</c>,
/// <c>error</c>, <c>skipped</c>, <c>inapplicable</c> → <c>not_applicable</c>, not measured → <c>not_measured</c>), else
/// from <c>ScenarioResult.Passed</c>; an assertion that could not decide is <c>not_measured</c>;</item>
/// <item>compliance evidence of the run becomes evidence records with blobs, and the run is sealed anew
/// (<c>sealedBy: ingest</c>): the store's hash chain is not an AEF seal.</item>
/// </list>
/// What store v1 does not record (an exact subject version, a suite version and digest, a deployment, how the target
/// was driven, typed absences outside an eval-result tree, a severity outside one) is said in
/// <see cref="AefConversion.Notes"/>, never filled in silently.
/// </summary>
public static class StoreV1Exporter
{
    /// <summary>run.json <c>imported.from</c>, before the AgentEval version.</summary>
    public const string From = "agenteval store v1";

    /// <summary>The path of a scenario's root line.</summary>
    public const string ScenarioPath = "scenario";

    /// <summary>The path of an assertion's line, before its 1-based position in the scenario.</summary>
    public const string AssertionPathPrefix = ScenarioPath + "/assertions/";

    /// <summary>The metric of the summary's pass rate (kind <c>rate</c>: a scenario counts 1 when <c>passed</c>, [SUM-4]).</summary>
    public const string PassMetric = "pass";

    /// <summary>The metric of a scenario's score (<c>ScenarioResult.Score</c>).</summary>
    public const string ScoreMetric = "score";

    /// <summary>The <c>ext</c> member the exporter writes the store's own fields under ([ENC-19]).</summary>
    public const string ExtName = "agenteval.store-v1";

    /// <summary>The lane when the store's run kind is not an AEF id.</summary>
    public const string DefaultLane = "main";

    // FileSystemOutputStore's own reading options, for the parts re-serialized into blobs and ext.
    private static readonly JsonSerializerOptions s_store = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Exports run <paramref name="runId"/> of <paramref name="store"/> to <paramref name="outputDirectory"/>.</summary>
    /// <param name="store">The store v1 workspace.</param>
    /// <param name="runId">The run.</param>
    /// <param name="outputDirectory">The AEF run folder to write: it must not exist, or be empty.</param>
    /// <param name="options">How to write it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="InvalidDataException">The store has no such run, or its run cannot be written as AEF (the message says why).</exception>
    /// <exception cref="ArgumentException">The output folder is not empty.</exception>
    public static async Task<AefConversion> ExportAsync(
        IOutputStoreReader store, string runId, string outputDirectory, StoreV1ExportOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        StoreV1Run run;
        try
        {
            run = await ReadAsync(store, runId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException)
        {
            // A store file that does not read (FileSystemOutputStore's own errors, or JSON that is not what it wrote).
            throw new InvalidDataException($"The store's run {runId} cannot be read: {e.Message}", e);
        }

        return Export(run, outputDirectory, options);
    }

    private static async Task<StoreV1Run> ReadAsync(IOutputStoreReader store, string runId, CancellationToken ct)
    {
        var manifest = await store.GetRunManifestAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The store has no run {runId} (no manifest.json for it under subjects/*/*/runs/).");
        var summary = await store.GetRunSummaryAsync(runId, ct).ConfigureAwait(false);
        var scenarios = new List<ScenarioResult>();
        await foreach (var scenario in store.GetScenarioResultsAsync(runId, ct).ConfigureAwait(false))
        {
            scenarios.Add(scenario);
        }

        var trace = await store.GetTraceAsync(runId, ct).ConfigureAwait(false);
        var compliance = new List<ComplianceEvidence>();
        var subject = manifest.Subject.ToIdentity();
        await foreach (var pointer in store.ListComplianceEvidenceAsync(subject: subject, ct: ct).ConfigureAwait(false))
        {
            var evidence = await store.GetComplianceEvidenceAsync(pointer.Regulation, subject, pointer.Timestamp, ct).ConfigureAwait(false);
            if (evidence is not null && evidence.SourceRun.RunId == runId)
            {
                compliance.Add(evidence);
            }
        }

        return new StoreV1Run(manifest, summary, scenarios, trace, compliance);
    }

    /// <summary>Exports a store v1 run already read.</summary>
    /// <exception cref="InvalidDataException">The run cannot be written as AEF (the message says why).</exception>
    /// <exception cref="ArgumentException">The output folder is not empty.</exception>
    public static AefConversion Export(StoreV1Run run, string outputDirectory, StoreV1ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        options ??= new StoreV1ExportOptions();
        var manifest = run.Manifest;
        var capture = options.ContentCapture;
        var runId = manifest.Run.RunId;
        if (!AefConverter.IsId(runId))
        {
            throw new InvalidDataException($"The run id '{runId}' is not an AEF id (1-128 letters, digits, '.', '_', ':' or '-').");
        }

        var notes = new List<string>();
        var asserted = new List<string> { "execution.targetMode", "contentCapture" };
        notes.Add($"How the target was driven is not recorded in store v1: execution.targetMode is the exporter's claim ({AefNames.Of(options.TargetMode)}).");
        notes.Add($"Store v1 records no content capture policy: contentCapture is the exporter's ({AefNames.Of(capture)}).");

        // [RUN-6]: the subject. Store v1's version is free text, set only when the producer gave one.
        var subjectVersion = manifest.Subject.Version;
        if (subjectVersion is null)
        {
            notes.Add("Store v1 records no exact subject version: subject.version is absent, so the run serves no checkpoint lane (LANE-1).");
        }
        else if (!AefConverter.IsExactVersion(subjectVersion))
        {
            notes.Add($"The store's subject version '{subjectVersion}' is not an exact version ([ENC-10]): subject.version is absent.");
            subjectVersion = null;
        }

        notes.Add("Store v1 records no suite version or digest: run.json has no suite.");
        notes.Add("Store v1 records no deployment: run.json has no deployment.");

        // [RUN-5]: a run CompleteRunAsync finished has a summary and a final verdict; anything else never completed.
        var startedAt = manifest.Run.Timestamp;
        var completed = run.Summary is not null && manifest.Run.Verdict != "PENDING";
        var endedAt = completed ? startedAt + (manifest.Run.Duration > TimeSpan.Zero ? manifest.Run.Duration : TimeSpan.Zero) : startedAt;
        string? abortReason = null;
        if (!completed)
        {
            abortReason = "AgentEval store v1: the run never completed (no summary.json, or the manifest's verdict is still PENDING).";
            asserted.Add("status");
            asserted.Add("endedAt");
            notes.Add("The store's run never completed: it is written as aborted, and its endedAt (its start) is the exporter's.");
        }

        var judges = Judges(run.Scenarios, notes);
        var lane = AefConverter.IsId(manifest.Run.Kind) ? manifest.Run.Kind : DefaultLane;
        var from = $"{From} (AgentEval {manifest.AgentEval.Version})";
        var header = new AefRunHeader
        {
            RunId = runId,
            Producer = options.Producer ?? AefConverter.DefaultProducer,
            Subject = new AefSubject
            {
                Ref = AefConverter.TypedRef(manifest.Subject.Kind == SubjectKind.Workflow ? "workflow" : "agent", manifest.Subject.Name),
                Kind = manifest.Subject.Kind == SubjectKind.Workflow ? AefSubjectKind.Workflow : AefSubjectKind.Agent,
                Version = subjectVersion,
            },
            Judges = judges.Count > 0 ? judges : null,
            StartedAt = startedAt,
            ContentCapture = capture,
            Ext = new JsonObject { [ExtName] = RunExt(manifest) },
            Execution = new AefExecution { TargetMode = options.TargetMode },
            Imported = new AefImported { From = from, Asserted = asserted },
        };

        var existed = Directory.Exists(outputDirectory);
        AefRunWriter writer;
        try
        {
            writer = AefRunWriter.Create(outputDirectory, header);
        }
        catch (ArgumentException e) when (e.ParamName != "directory")
        {
            throw new InvalidDataException($"The run's header cannot be written as AEF: {e.Message}", e);
        }

        try
        {
            writer.SetMetrics(Metrics(run.Scenarios));
            var compliance = AddCompliance(writer, run.ComplianceEvidence);
            var traceEvidence = AddTrace(writer, run.Trace, capture);
            var index = 0;
            foreach (var scenario in run.Scenarios)
            {
                index++;
                AddScenario(writer, scenario, index, lane, capture, compliance, traceEvidence);
            }

            notes.Add("Severity and typed absences come only from a scenario's eval-result tree; a scenario without one is passed or failed, with no severity.");
            if (run.Scenarios.Any(s => s.Assertions.Count > 0))
            {
                notes.Add("Store v1 does not record what part a scenario's assertions played in its verdict: each assertion line has component weight 0, not required, and the scenario's aggregation describes its own verdict.");
            }

            writer.SetSummary(Summary(run.Summary, lane));
            return AefConverter.Finish(writer, options, from, asserted, notes, completed ? AefRunStatus.Completed : AefRunStatus.Aborted, endedAt, abortReason);
        }
        catch (Exception e)
        {
            AefConverter.Discard(outputDirectory, existed);
            if (e is ArgumentException)
            {
                throw new InvalidDataException($"The run cannot be written as AEF: {e.Message}", e);
            }

            throw;
        }
    }

    // ---------------------------------------------------------------- run.json

    // [RUN-9]: the judges the scenarios' comparability facts name, in order of first appearance.
    private static List<AefJudge> Judges(IReadOnlyList<ScenarioResult> scenarios, List<string> notes)
    {
        var judges = new List<AefJudge>();
        var seen = new HashSet<(string, string?)>();
        foreach (var judge in scenarios.Select(s => s.Comparability?.Judge).OfType<JudgeFingerprint>())
        {
            var digest = AefConverter.Sha256Uri(judge.RubricDigest);
            if (judge.RubricDigest is not null && digest is null)
            {
                notes.Add($"The judge {judge.ModelId}'s rubric digest is not a SHA-256: judges[].rubricDigest is absent.");
            }

            if (seen.Add((judge.ModelId, digest)) && AefConverter.Text(judge.ModelId, 256) == judge.ModelId)
            {
                judges.Add(new AefJudge { Model = judge.ModelId, RubricDigest = digest });
            }
        }

        return judges;
    }

    // run.json ext: the store's manifest fields AEF has no place for (no machine name: it identifies a person's computer).
    private static JsonObject RunExt(RunManifest manifest) => new()
    {
        ["schemaVersion"] = manifest.SchemaVersion,
        ["solution"] = new JsonObject { ["id"] = manifest.Solution.Id.ToString(), ["name"] = manifest.Solution.Name },
        ["subject"] = new JsonObject
        {
            ["name"] = manifest.Subject.Name,
            ["version"] = manifest.Subject.Version,
            ["framework"] = manifest.Subject.Framework,
            ["modelId"] = manifest.Subject.ModelId,
            ["sourceProject"] = manifest.Subject.SourceProject,
            ["sourcePath"] = manifest.Subject.SourcePath,
        },
        ["run"] = new JsonObject
        {
            ["verdict"] = manifest.Run.Verdict,
            ["kind"] = manifest.Run.Kind,
            ["scenarioCount"] = manifest.Run.ScenarioCount,
            ["evalProject"] = manifest.Run.EvalProject,
            ["evalProjectPath"] = manifest.Run.EvalProjectPath,
            ["harness"] = manifest.Run.Harness,
            ["seed"] = manifest.Run.Seed,
            ["parentInvocationId"] = manifest.Run.ParentInvocationId,
        },
        ["git"] = new JsonObject { ["commit"] = manifest.Git.Commit, ["branch"] = manifest.Git.Branch, ["dirty"] = manifest.Git.Dirty, ["tag"] = manifest.Git.Tag },
        ["agentEval"] = new JsonObject { ["version"] = manifest.AgentEval.Version, ["configurationId"] = manifest.AgentEval.ConfigurationId },
        ["environment"] = new JsonObject
        {
            ["os"] = manifest.Environment.Os,
            ["dotnetVersion"] = manifest.Environment.DotnetVersion,
            ["ci"] = manifest.Environment.Ci,
            ["ciSystem"] = manifest.Environment.CiSystem,
        },
        ["contentHash"] = manifest.ContentHash,
    };

    // ---------------------------------------------------------------- metrics.json

    private static List<AefMetric> Metrics(IReadOnlyList<ScenarioResult> scenarios)
    {
        var metrics = new List<AefMetric>
        {
            new()
            {
                Id = PassMetric, Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                Description = "A scenario counts 1 when it passed (SUM-4).",
            },
            new()
            {
                Id = ScoreMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Unbounded,
                Description = "The scenario's score (AgentEval store v1 ScenarioResult.Score).",
            },
        };
        var dimensions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in scenarios.SelectMany(s => s.Metrics.Keys).Select(DimensionMetric).OfType<string>())
        {
            if (dimensions.Add(name))
            {
                metrics.Add(new AefMetric
                {
                    Id = name, Kind = AefMetricKind.Score, Direction = AefMetricDirection.None, Scale = AefScale.Unbounded,
                    Description = "A dimension of the scenario (AgentEval store v1 ScenarioResult.Metrics); which way is better is not recorded.",
                });
            }
        }

        return metrics;
    }

    // A dimension's metric id: its name, unless the store lifted it from the tree (_lifted.*: the severity and confidence,
    // mapped elsewhere), it names one of the exporter's metrics, or it is longer than an id may be.
    private static string? DimensionMetric(string name) =>
        name.StartsWith("_lifted.", StringComparison.Ordinal) || name.Length is 0 or > 256 ? null
        : name is PassMetric or ScoreMetric ? "dimension:" + name
        : name;

    // ---------------------------------------------------------------- evidence

    // Each compliance evidence document of the run: a blob (as the store reads it, re-serialized) and a record; the
    // scenarios each control names cite it.
    private static Dictionary<string, List<string>> AddCompliance(AefRunWriter writer, IReadOnlyList<ComplianceEvidence> documents)
    {
        var cited = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var index = 0;
        foreach (var document in documents)
        {
            index++;
            var id = $"E-compliance-{index}";
            var blob = writer.PutBlob(JsonSerializer.SerializeToUtf8Bytes(document, s_store));
            writer.AddEvidence(new AefEvidence
            {
                EvidenceId = id,
                Kind = AefEvidenceKind.ComplianceArtifact,
                Link = AefEvidenceLink.ToBlob(blob),
                Description = AefConverter.Text(
                    $"AgentEval store v1 compliance evidence: {document.Regulation}, generated {document.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)}, {document.Summary.OverallStatus}. " +
                    "Re-serialized from the store's reader, not the original file's bytes (spec 07 §7.5).", 1024),
            });
            foreach (var scenario in document.Controls.SelectMany(c => c.ScenarioRefs).Distinct(StringComparer.Ordinal))
            {
                if (!cited.TryGetValue(scenario, out var ids))
                {
                    cited[scenario] = ids = [];
                }

                ids.Add(id);
            }
        }

        return cited;
    }

    // The run's agent trace (one file per run in store v1): a transcript blob, cited by the scenario it names. Content:
    // written only when the run keeps content ([RUN-11]).
    private static (string ScenarioId, string EvidenceId)? AddTrace(AefRunWriter writer, AgentTrace? trace, AefContentCapture capture)
    {
        if (trace is null || capture == AefContentCapture.Off)
        {
            return null;
        }

        var blob = writer.PutBlob(JsonSerializer.SerializeToUtf8Bytes(trace, s_store));
        writer.AddEvidence(new AefEvidence
        {
            EvidenceId = "E-trace",
            Kind = AefEvidenceKind.Transcript,
            Link = AefEvidenceLink.ToBlob(blob),
            Description = "AgentEval store v1 agent trace (traces/agent-trace.json), as the store reads it.",
        });
        return (trace.ScenarioId, "E-trace");
    }

    // ---------------------------------------------------------------- results.ndjson

    private static void AddScenario(
        AefRunWriter writer, ScenarioResult scenario, int index, string lane, AefContentCapture capture,
        Dictionary<string, List<string>> compliance, (string ScenarioId, string EvidenceId)? trace)
    {
        if (!AefConverter.IsResultText(scenario.Id, 256))
        {
            throw new InvalidDataException($"The scenario id '{scenario.Id}' is not an AEF case id (1-256 characters, no control character).");
        }

        var tree = Tree(scenario);
        var (state, reason, severity) = ScenarioState(scenario, tree, capture);
        var evidence = new List<string>();
        if (capture == AefContentCapture.On)
        {
            if (scenario.Input.Length > 0)
            {
                evidence.Add(Content(writer, $"E-s{index}-input", AefEvidenceKind.Input, scenario.Input, "The scenario's input (AgentEval store v1 ScenarioResult.Input)."));
            }

            if (scenario.Output.Length > 0)
            {
                evidence.Add(tree is null
                    ? Content(writer, $"E-s{index}-output", AefEvidenceKind.Output, scenario.Output, "The agent's output (AgentEval store v1 ScenarioResult.Output).")
                    : Content(writer, $"E-s{index}-eval", AefEvidenceKind.Other, scenario.Output, "The scenario's eval-result tree (AgentEval store v1 ScenarioResult.Output), which may hold content."));
            }
        }

        evidence.AddRange(compliance.GetValueOrDefault(scenario.Id) ?? []);
        if (trace is { } t && t.ScenarioId == scenario.Id)
        {
            evidence.Add(t.EvidenceId);
        }

        // The assertions, as children: Passed → passed, Failed → failed, Inconclusive → not_measured.
        var children = scenario.Assertions.Select((a, i) => Assertion(scenario.Id, a, i, lane)).ToList();
        var measured = IsMeasured(state);
        var facts = scenario.Comparability;
        var root = new AefResult
        {
            CaseId = scenario.Id,
            Path = ScenarioPath,
            Evaluator = Evaluator(scenario, tree),
            State = state,
            Reason = reason,
            Scores = measured ? Scores(scenario) : null,
            VerdictRule = facts?.EffectiveBar is { } bar
                ? new AefVerdictRule("the eval's own rule against its threshold", bar, "AgentEval eval threshold (store v1 comparability.effectiveBar)")
                : null,
            Annotator = Annotator(facts, tree),
            Aggregation = children.Count == 0 ? null : Aggregation(scenario, state, children, facts),
            Evidence = evidence.Count > 0 ? evidence : null,
            Ext = new JsonObject { [ExtName] = ScenarioExt(scenario, capture) },
            Lane = lane,
            Severity = severity,
            DurationMs = scenario.Duration > TimeSpan.Zero ? scenario.Duration.TotalMilliseconds : null,
            Usage = scenario.EstimatedCost > 0 && double.IsFinite(scenario.EstimatedCost)
                ? [new AefUsage { Role = AefUsageRole.Other, CostUsd = scenario.EstimatedCost, CostSource = "AgentEval store v1 estimate (ScenarioResult.EstimatedCost)" }]
                : null,
        };

        AefResultHandle handle;
        try
        {
            handle = writer.AddResult(root);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"Scenario '{scenario.Id}' cannot be written as AEF: {e.Message}", e);
        }

        foreach (var child in children)
        {
            handle.AddChild(child);
        }
    }

    private static string Content(AefRunWriter writer, string id, AefEvidenceKind kind, string text, string description)
    {
        var blob = writer.PutBlob(Encoding.UTF8.GetBytes(text));
        writer.AddEvidence(new AefEvidence { EvidenceId = id, Kind = kind, Link = AefEvidenceLink.ToBlob(blob), Description = description });
        return id;
    }

    // The scenario's eval-result tree, when ScenarioResult.Output holds one (EvalResultPersistence writes it there).
    private static EvalResult? Tree(ScenarioResult scenario)
    {
        var output = scenario.Output.AsSpan().TrimStart();
        if (output.Length == 0 || output[0] != '{')
        {
            return null;
        }

        try
        {
            var tree = EvalResultPersistence.FromScenarioResult(scenario);
            return tree?.Metric is not null && tree.Score is not null ? tree : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or JsonException)
        {
            return null;   // not a tree this version reads: the scenario's own pass flag decides
        }
    }

    // §7.5: MeasurementState and labels → state. An unknown label is read by the score's pass flag (as AgentEval's own
    // ReportStatus reads it). Without a tree, ScenarioResult.Passed decides and nothing gives a severity.
    private static (AefState State, string? Reason, AefSeverity? Severity) ScenarioState(ScenarioResult scenario, EvalResult? tree, AefContentCapture capture)
    {
        if (tree is null)
        {
            return (scenario.Passed ? AefState.Passed : AefState.Failed, null, null);
        }

        var score = tree.Score;
        var state = score.Measurement switch
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

        AefSeverity? severity = state is AefState.Failed or AefState.Warn && AefNames.TryParse<AefSeverity>(score.Severity, out var s) ? s : null;

        // The eval's own words are judge reasoning, which a run that keeps no content does not keep ([RUN-11]): then a
        // typed absence says only where its state came from.
        var why = tree.Details?.Summary ?? tree.Details?.Recommendations?.FirstOrDefault();
        string? reason = capture == AefContentCapture.On ? AefConverter.Text(why, 4096) : null;
        if (!IsMeasured(state) && reason is null)
        {
            reason = $"AgentEval store v1: the eval's label is '{score.Label}' and its measurement state {score.Measurement}"
                + (capture == AefContentCapture.On ? "; it records no reason." : " (its reason is not kept: contentCapture off).");
        }

        return (state, reason, severity);
    }

    private static AefEvaluator Evaluator(ScenarioResult scenario, EvalResult? tree)
    {
        var (key, version) = scenario.Comparability is { } facts ? (facts.EvalKey, facts.EvalVersion)
            : tree is not null ? (tree.Metric.Key, tree.Metric.Version)
            : ("agenteval.scenario", null);
        return new AefEvaluator(AefConverter.Text(key, 256) ?? "agenteval.scenario", AefConverter.Text(version, 64));
    }

    private static List<AefScore> Scores(ScenarioResult scenario)
    {
        var scores = new List<AefScore>();
        if (double.IsFinite(scenario.Score))
        {
            scores.Add(new AefScore { Metric = ScoreMetric, Value = scenario.Score });
        }

        foreach (var (name, value) in scenario.Metrics)
        {
            if (DimensionMetric(name) is { } metric && double.IsFinite(value))
            {
                scores.Add(new AefScore { Metric = metric, Value = value });
            }
        }

        return scores;
    }

    // Who graded: the judge the comparability facts name (an LLM), a deterministic eval (code), or, without facts, the
    // tree's judge model.
    private static AefAnnotator? Annotator(ComparabilityFacts? facts, EvalResult? tree)
    {
        if (facts?.Judge is { } judge)
        {
            return new AefAnnotator { Kind = AefAnnotatorKind.Llm, Model = AefConverter.Text(judge.ModelId, 256), RubricDigest = AefConverter.Sha256Uri(judge.RubricDigest) };
        }

        if (facts is not null)
        {
            return new AefAnnotator { Kind = AefAnnotatorKind.Code };
        }

        return tree?.Provenance?.JudgeModel is { Length: > 0 } model ? new AefAnnotator { Kind = AefAnnotatorKind.Llm, Model = AefConverter.Text(model, 256) } : null;
    }

    // [RES-5]: a scenario with assertions is a composite. Store v1 records the scenario's own verdict and its assertions
    // beside it, not how one led to the other: the aggregation describes the scenario's own score against its threshold
    // (or nothing measured), its children weigh nothing and are not required, and no child is named decisive.
    private static AefAggregation Aggregation(ScenarioResult scenario, AefState state, List<AefResult> children, ComparabilityFacts? facts)
    {
        var count = (AefState s) => (long)children.Count(c => c.State == s);
        var unmeasured = new AefUnmeasured
        {
            NotMeasured = Positive(count(AefState.NotMeasured)),
            NotApplicable = Positive(count(AefState.NotApplicable)),
            Skipped = Positive(count(AefState.Skipped)),
            Error = Positive(count(AefState.Error)),
        };
        var measured = children.Count(c => IsMeasured(c.State));
        return new AefAggregation
        {
            Strategy = AefAggregationStrategy.Own,
            Threshold = facts?.EffectiveBar,
            Score = IsMeasured(state) && double.IsFinite(scenario.Score) ? scenario.Score : null,
            RulePath = IsMeasured(state) ? AefRulePath.Threshold : AefRulePath.NothingMeasured,
            Measured = measured,
            Total = children.Count,
            Unmeasured = measured < children.Count ? unmeasured : null,
        };
    }

    private static long? Positive(long count) => count > 0 ? count : null;

    private static AefResult Assertion(string caseId, AssertionResult assertion, int index, string lane)
    {
        var state = assertion.Outcome switch
        {
            AssertionOutcome.Passed => AefState.Passed,
            AssertionOutcome.Failed => AefState.Failed,
            _ => AefState.NotMeasured,
        };
        var reason = AefConverter.Text(assertion.Message, 4096);
        if (state == AefState.NotMeasured && reason is null)
        {
            reason = "The assertion could not decide (AgentEval store v1: Inconclusive); it records no reason.";
        }

        return new AefResult
        {
            CaseId = caseId,
            Path = AssertionPathPrefix + (index + 1).ToString(CultureInfo.InvariantCulture),
            Evaluator = new AefEvaluator(AefConverter.Text(assertion.Assertion, 256) ?? "assertion"),
            State = state,
            Reason = reason,
            Component = new AefComponent(0, false),
            Lane = lane,
        };
    }

    private static JsonObject ScenarioExt(ScenarioResult scenario, AefContentCapture capture)
    {
        var ext = new JsonObject
        {
            ["name"] = scenario.Name,
            ["passed"] = scenario.Passed,
            ["score"] = double.IsFinite(scenario.Score) ? scenario.Score : null,
        };
        if (capture == AefContentCapture.On && scenario.StimulusHash is { } stimulus)
        {
            ext["stimulusHash"] = stimulus;   // a digest of the input: kept only with content ([RUN-11])
        }

        if (scenario.Comparability is { } facts)
        {
            ext["comparability"] = JsonSerializer.SerializeToNode(facts, s_store);
        }

        return ext;
    }

    // ---------------------------------------------------------------- summary.json

    // One lane, the run's kind: the pass rate and the mean score of the scenarios. Store v1's verdict is the run's, not an
    // entry's, so no entry carries a rule: each is scored ([SUM-6]); the store's own summary travels in ext.
    private static AefSummary Summary(RunSummary? summary, string lane)
    {
        JsonObject? ext = null;
        if (summary is not null)
        {
            var metrics = new JsonObject();
            foreach (var (name, value) in summary.Metrics)
            {
                metrics[name] = double.IsFinite(value) ? value : null;
            }

            ext = new JsonObject
            {
                [ExtName] = new JsonObject
                {
                    ["verdict"] = summary.Verdict,
                    ["stats"] = new JsonObject
                    {
                        ["total"] = summary.Stats.Total,
                        ["passed"] = summary.Stats.Passed,
                        ["failed"] = summary.Stats.Failed,
                        ["warnings"] = summary.Stats.Warnings,
                        ["skipped"] = summary.Stats.Skipped,
                    },
                    ["metrics"] = metrics,
                },
            };
        }

        var cost = summary?.Cost;
        return new AefSummary
        {
            Lanes = [new AefSummaryLane(lane, [new AefSummaryEntry { Metric = PassMetric, Path = ScenarioPath }, new AefSummaryEntry { Metric = ScoreMetric, Path = ScenarioPath }])],
            Cost = cost is not null && double.IsFinite(cost.EstimatedCost) ? new AefCost(cost.EstimatedCost, "AgentEval store v1 estimate (summary.json cost)") : null,
            Usage = cost is null ? null :
            [
                new AefUsage
                {
                    Role = AefUsageRole.Other,   // store v1 does not say whose tokens they are
                    InputTokens = cost.PromptTokens,
                    OutputTokens = cost.CompletionTokens,
                },
            ],
            Ext = ext,
        };
    }

    private static bool IsMeasured(AefState state) =>
        state is AefState.Passed or AefState.Failed or AefState.Warn or AefState.Inconclusive or AefState.Scored;
}

/// <summary>A store v1 run as <see cref="IOutputStoreReader"/> reads it.</summary>
/// <param name="Manifest">Its manifest.json.</param>
/// <param name="Summary">Its summary.json, when the run completed.</param>
/// <param name="Scenarios">Its scenario results.</param>
/// <param name="Trace">Its agent trace, when it has one.</param>
/// <param name="ComplianceEvidence">The compliance evidence documents generated from it.</param>
public sealed record StoreV1Run(
    RunManifest Manifest,
    RunSummary? Summary,
    IReadOnlyList<ScenarioResult> Scenarios,
    AgentTrace? Trace,
    IReadOnlyList<ComplianceEvidence> ComplianceEvidence);
