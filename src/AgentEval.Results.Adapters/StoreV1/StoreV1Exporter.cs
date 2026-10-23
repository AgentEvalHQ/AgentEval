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
using AgentEval.Results.Adapters.Reading;
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
public static partial class StoreV1Exporter
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
    /// <remarks>This semantic reader API can preserve only the generic compliance evidence exposed by the
    /// reader. Use <see cref="ExportWorkspaceRun"/> for exact filesystem selection and original rich reports.</remarks>
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

    private static async Task<StoreV1Run> ReadAsync(IOutputStoreReader store, string runId, CancellationToken ct, AefWriteBudget? budget = null)
    {
        var accountant = budget is null ? null : new SemanticAccountant(budget);
        var manifest = await store.GetRunManifestAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The store has no run {runId} (no manifest.json for it under subjects/*/*/runs/).");
        ValidateManifest(manifest);
        accountant?.Account(manifest);
        var summary = await store.GetRunSummaryAsync(runId, ct).ConfigureAwait(false);
        accountant?.Account(summary);
        var scenarios = new List<ScenarioResult>();
        await foreach (var scenario in store.GetScenarioResultsAsync(runId, ct).ConfigureAwait(false))
        {
            accountant?.Account(scenario);
            scenarios.Add(scenario);
        }

        var trace = await store.GetTraceAsync(runId, ct).ConfigureAwait(false);
        accountant?.Account(trace);
        var compliance = new List<ComplianceEvidence>();
        var subject = manifest.Subject.ToIdentity();
        await foreach (var pointer in store.ListComplianceEvidenceAsync(subject: subject, ct: ct).ConfigureAwait(false))
        {
            accountant?.Account(pointer);
            var evidence = await store.GetComplianceEvidenceAsync(pointer.Regulation, subject, pointer.Timestamp, ct).ConfigureAwait(false);
            accountant?.Account(evidence);
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
        ValidateManifest(manifest);
        ValidateSummary(run.Summary);
        if (run.Summary is { } summary)
        {
            if (summary.RunId != manifest.Run.RunId)
                throw InvalidSource("The summary names a different execution than the selected manifest.");
            if (manifest.Run.Verdict != "PENDING" &&
                (manifest.Run.ScenarioCount != run.Scenarios.Count || summary.Stats.Total != run.Scenarios.Count))
                throw InvalidSource("The completed run's recorded scenario counts disagree with the supplied results.");
        }
        var capture = options.ContentCapture;
        var runId = manifest.Run.RunId;
        if (!AefConverter.IsId(runId))
        {
            throw new InvalidDataException($"The run id '{runId}' is not an AEF id (1-128 letters, digits, '.', '_', ':' or '-').");
        }

        var notes = new List<string>();
        notes.Add("Imported: not sealed when it ran. The original legacy run was not AEF-sealed during execution; any ingest seal protects the converted evidence from import onward, not its earlier history.");
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
            asserted.Add("abortReason");
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

        return Export(new ExportData(header, run.Scenarios, lane, endedAt, abortReason, notes)
        {
            Summary = run.Summary, Trace = run.Trace, ComplianceEvidence = run.ComplianceEvidence,
            ComplianceArtifacts = run.ComplianceArtifacts,
            Reports = run.Reports,
            TraceSnapshotPath = run.TraceSnapshotPath,
        }, outputDirectory, options);
    }

    private static void ValidateManifest(RunManifest manifest)
    {
        if (manifest is null || manifest.Run is null || manifest.Subject is null || manifest.Solution is null
            || manifest.Git is null || manifest.AgentEval is null || manifest.Environment is null)
            throw InvalidSource("The legacy manifest is incomplete.");
    }

    private static void ValidateSummary(RunSummary? summary)
    {
        if (summary is not null && (summary.Metrics is null || summary.Stats is null))
            throw InvalidSource("The legacy summary is incomplete.");
    }

    private static InvalidDataException InvalidSource(string actual, Exception? inner = null) => new(
        "Expected: readable legacy evidence with the fields needed for conversion. Actual: " + actual +
        " Suggestions: select the original complete files, or an ordinary scenario-only collection when its manifest is unavailable." +
        " Because: missing or malformed evidence cannot be reconstructed as recorded facts.", inner);

    // Both workspace runs and detached scenario collections use this same evidence mapping.
    private sealed record ExportData(AefRunHeader Header, IReadOnlyList<ScenarioResult> Scenarios,
        string Lane, DateTimeOffset EndedAt, string? AbortReason, List<string> Notes)
    {
        internal RunSummary? Summary { get; init; }
        internal AgentTrace? Trace { get; init; }
        internal string? TraceSnapshotPath { get; init; }
        internal IReadOnlyList<ComplianceEvidence> ComplianceEvidence { get; init; } = [];
        internal IReadOnlyList<byte[]> ComplianceArtifacts { get; init; } = [];
        internal IReadOnlyList<StoreV1ReportArtifact> Reports { get; init; } = [];
    }

    private static AefConversion Export(ExportData data, string outputDirectory, StoreV1ExportOptions options)
    {
        ValidateSummary(data.Summary);
        if (data.Scenarios.Any(s => s.Input is null || s.Output is null || s.Metrics is null
            || s.Assertions?.Any(a => a is null) == true))
            throw InvalidSource("A legacy scenario contains null input, output, metrics or assertion evidence.");
        var existed = Directory.Exists(outputDirectory);
        AefRunWriter writer;
        try
        {
            writer = AefRunWriter.Create(outputDirectory, data.Header, options.WriteBudget);
        }
        catch (ArgumentException e) when (e.ParamName != "directory")
        {
            throw new InvalidDataException($"The run's header cannot be written as AEF: {e.Message}", e);
        }

        try
        {
            writer.SetMetrics(Metrics(data.Scenarios));
            var compliance = AddCompliance(writer, data.ComplianceEvidence);
            if (options.ContentCapture == AefContentCapture.On)
            {
                for (var i = 0; i < data.ComplianceArtifacts.Count; i++)
                {
                    var id = $"E-compliance-original-{i + 1}";
                    var document = new AefComplianceArtifactReader().ReadDocument(data.ComplianceArtifacts[i], id);
                    writer.AddEvidence(new AefEvidence
                    {
                        EvidenceId = id,
                        Kind = AefEvidenceKind.ComplianceArtifact,
                        Link = AefEvidenceLink.ToBlob(writer.PutBlob(data.ComplianceArtifacts[i])),
                        Description = "Original legacy compliance document bytes; ingest custody begins at conversion.",
                    });
                    foreach (var scenario in document.Evidence.Controls.SelectMany(c => c.ScenarioRefs).Distinct(StringComparer.Ordinal))
                    {
                        if (!compliance.TryGetValue(scenario, out var ids)) compliance[scenario] = ids = [];
                        ids.Add(id);
                    }
                }
            }
            var traceEvidence = AddTrace(writer, data.Trace, data.TraceSnapshotPath, options.ContentCapture);
            AddReports(writer, data.Reports, options.ContentCapture);
            var index = 0;
            foreach (var scenario in data.Scenarios)
            {
                index++;
                AddScenario(writer, scenario, index, data.Lane, options.ContentCapture, compliance, traceEvidence);
            }

            data.Notes.Add("Severity and typed absences come only from a scenario's eval-result tree; a scenario without one is passed or failed, with no severity.");
            if (data.Scenarios.Any(s => s.Assertions.Count > 0))
            {
                data.Notes.Add("Store v1 does not record what part a scenario's assertions played in its verdict: each assertion line has component weight 0, not required, and the scenario's aggregation describes its own verdict.");
            }

            writer.SetSummary(Summary(data.Summary, data.Lane));
            return AefConverter.Finish(writer, options, data.Header.Imported!.From, data.Header.Imported.Asserted,
                data.Notes, data.AbortReason is null ? AefRunStatus.Completed : AefRunStatus.Aborted, data.EndedAt, data.AbortReason);
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
        // A generic ScenarioResult has no declared domain. Refine the shared metric only when every scenario
        // proves the EvalScore contract from its recorded result, never merely because observed values fit 0..1.
        var normalized = scenarios.Count > 0 && scenarios.All(HasNormalizedScoreContract);
        var metrics = new List<AefMetric>
        {
            new()
            {
                Id = PassMetric, Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                Description = "A scenario counts 1 when it passed (SUM-4).",
            },
            new()
            {
                Id = ScoreMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter,
                Scale = normalized ? AefScale.Between(0, 1) : AefScale.Unbounded,
                Description = normalized ? "The recorded EvalScore's normalized score (0 to 1)."
                    : "The scenario's score (AgentEval store v1 ScenarioResult.Score); no common normalized domain is established.",
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

    private static bool HasNormalizedScoreContract(ScenarioResult scenario)
    {
        var tree = Tree(scenario);
        if (tree?.Details is null || tree.Provenance is null || tree.EvaluatedAt == default ||
            string.IsNullOrWhiteSpace(tree.Metric.Key) || string.IsNullOrWhiteSpace(tree.Metric.Version) ||
            string.IsNullOrWhiteSpace(tree.Score.Label) || !Enum.IsDefined(tree.Score.Measurement) ||
            tree.Score.Value is < 0 or > 1 || scenario.Score != tree.Score.MeasuredValue() || scenario.Passed != tree.Score.Passed)
            return false;
        if (scenario.Measurement is not null && scenario.Measurement != ScenarioMeasurement.Of(tree.Score.CensusBucket()))
            return false;
        return scenario.Comparability is not { } facts ||
            facts.EvalKey == tree.Metric.Key && facts.EvalVersion == tree.Metric.Version && facts.EffectiveBar == tree.Score.Threshold;
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
    private static (string ScenarioId, string EvidenceId)? AddTrace(AefRunWriter writer, AgentTrace? trace,
        string? snapshotPath, AefContentCapture capture)
    {
        if (trace is null || capture == AefContentCapture.Off)
        {
            return null;
        }

        // Filesystem imports retain the owned snapshot's exact bytes, including
        // absent fields and unfamiliar properties. Reserializing would turn a
        // missing timestamp into the DTO's default and discard unknown evidence.
        using var input = snapshotPath is null ? null : File.OpenRead(snapshotPath);
        var blob = input is null ? writer.PutBlob(JsonSerializer.SerializeToUtf8Bytes(trace, s_store)) : writer.PutBlob(input);
        writer.AddEvidence(new AefEvidence
        {
            EvidenceId = "E-trace",
            Kind = AefEvidenceKind.Transcript,
            Link = AefEvidenceLink.ToBlob(blob),
            Description = snapshotPath is null ? "AgentEval store v1 agent trace, as supplied by the semantic store."
                : "AgentEval store v1 agent trace (traces/agent-trace.json), captured without rewriting.",
            Ext = new JsonObject { [ExtName] = new JsonObject { ["traceFormat"] = "agent-trace-v1" } },
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
            DurationMs = scenario.Duration is { } duration && duration >= TimeSpan.Zero ? duration.TotalMilliseconds : null,
            // Preserve recorded zero; only absent or invalid costs omit usage.
            Usage = scenario.EstimatedCost is double cost && cost >= 0 && double.IsFinite(cost)
                ? [new AefUsage { Role = AefUsageRole.Other, CostUsd = cost, CostSource = "AgentEval store v1 estimate (ScenarioResult.EstimatedCost)" }]
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
            if (!ScenarioMeasurement.IsMeasured(scenario))
            {
                var absence = scenario.Measurement == ScenarioMeasurement.NotApplicable ? AefState.NotApplicable : AefState.NotMeasured;
                return (absence, "AgentEval store v1: the scenario has no measured score (measurement: "
                    + (scenario.Measurement ?? "unrecorded") + ").", null);
            }
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
        if (scenario.Score is { } score && double.IsFinite(score))
        {
            scores.Add(new AefScore { Metric = ScoreMetric, Value = score });
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
            return new AefAnnotator
            {
                Kind = facts.EffectiveInstrumentKind switch
                {
                    EvaluationInstrumentKind.Model => AefAnnotatorKind.Llm,
                    EvaluationInstrumentKind.Deterministic => AefAnnotatorKind.Code,
                    _ => AefAnnotatorKind.Other,
                },
                RubricDigest = facts.EffectiveInstrumentKind == EvaluationInstrumentKind.Model
                    ? AefConverter.Sha256Uri(facts.EffectiveRubricDigest) : null,
            };
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
            Score = IsMeasured(state) && scenario.Score is { } score && double.IsFinite(score) ? score : null,
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
            ["score"] = scenario.Score is { } score && double.IsFinite(score) ? score : null,
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
    IReadOnlyList<ComplianceEvidence> ComplianceEvidence)
{
    // Exact bytes captured by the filesystem importer, after validating their generic evidence identity.
    internal IReadOnlyList<byte[]> ComplianceArtifacts { get; init; } = [];
    internal IReadOnlyList<StoreV1ReportArtifact> Reports { get; init; } = [];
    internal string? TraceSnapshotPath { get; init; }
}
