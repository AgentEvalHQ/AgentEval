// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    /// <summary>
    /// Imports a copied legacy run folder or a collection of scenario JSON files. A missing manifest
    /// leaves execution identity, timing and completion unknown; required AEF fields are explicit importer claims.
    /// The source is read through a bounded snapshot and is never changed.
    /// </summary>
    /// <remarks>Workspace-level compliance evidence is unavailable in a detached folder.</remarks>
    public static AefConversion ExportDirectory(string sourceDirectory, string outputDirectory, StoreV1ExportOptions? options = null)
        => ExportDirectoryCore(sourceDirectory, outputDirectory, options ?? new(), CancellationToken.None);

    private static AefConversion ExportDirectoryCore(string sourceDirectory, string outputDirectory, StoreV1ExportOptions options,
        CancellationToken ct, Func<StoreV1Run, StoreV1Run>? enrich = null, AefReadBudget? budget = null)
    {
        var limits = new AefStagingOptions
        {
            MaxFiles = 100_000, MaxBytes = 1L << 30, MaxFileBytes = 1L << 30,
        };
        using var snapshot = budget is null ? AefOwnedSnapshot.Capture(Path.GetFullPath(sourceDirectory), limits, ct)
            : budget.Capture(Path.GetFullPath(sourceDirectory), limits, ct);
        var root = snapshot.FullName;
        if (new[] { "run.json", "results.ndjson", "seal.json", "metrics.json" }.Any(name => Path.Exists(Path.Combine(root, name))))
            throw InvalidSource("The directory contains AEF markers and requires direct AEF verification.");
        var manifest = ReadOptional<RunManifest>(Path.Combine(root, "manifest.json"));
        if (manifest is null && File.Exists(Path.Combine(root, "agentic-result.json")))
        {
            if (enrich is not null || Directory.Exists(Path.Combine(root, "scenarios"))
                || Directory.GetFiles(root, "*.json").Any(p => Path.GetFileName(p) != "agentic-result.json"))
                throw InvalidSource("An agentic report-only import cannot also claim scenario or workspace execution files.");
            return ExportAgenticReport(AgenticReportDocument.Read(File.ReadAllBytes(Path.Combine(root, "agentic-result.json"))),
                outputDirectory, options, ct, budget);
        }
        var scenarioDirectory = manifest is not null || Directory.Exists(Path.Combine(root, "scenarios")) ? Path.Combine(root, "scenarios") : root;
        var scenarios = (Directory.Exists(scenarioDirectory) ? Directory.GetFiles(scenarioDirectory, "*.json") : [])
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(ReadRequired<ScenarioResult>).ToList();
        var nativeReport = scenarios.Count == 0 ? TraceReportScenarios(root, manifest) : null;
        if (nativeReport is not null) scenarios.AddRange(nativeReport);
        if ((scenarios.Count == 0 && manifest is null) || scenarios.Any(s => string.IsNullOrWhiteSpace(s.Id)))
            throw InvalidSource("No usable scenario results with recorded scenario IDs were found.");
        var summary = ReadOptional<RunSummary>(Path.Combine(root, "summary.json"));
        ValidateSummary(summary);
        var tracePath = Path.Combine(root, "traces", "agent-trace.json");
        var trace = ReadOptional<AgentTrace>(tracePath);
        if (manifest is not null)
        {
            var run = new StoreV1Run(manifest, summary, scenarios, trace, [])
            {
                Reports = Reports(root, options.ContentCapture),
                TraceSnapshotPath = trace is null ? null : tracePath,
                NativeReportProjection = nativeReport is not null,
            };
            ct.ThrowIfCancellationRequested();
            run = enrich is null ? run : enrich(run);
            if (budget is not null) options = options with { WriteBudget = new(budget.Limit(64L << 20), ct, budget.Charge) };
            return Export(run, outputDirectory, options);
        }
        if (enrich is not null) throw InvalidSource("A workspace run requires its original manifest.");

        const string reason = "Original execution timing and completion are unknown because the legacy manifest is missing.";
        var notes = new List<string>
        {
            reason,
            "Imported: not sealed when it ran. Only conversion onward is protected by an ingest seal.",
            "The importer assigns a run ID, other:unknown subject, main lane and Unix epoch placeholder timestamps; none describes the original execution.",
            "The source AgentEval version, suite, deployment and workspace compliance evidence are unknown.",
            "Any detached summary is not used to infer execution completion without a manifest.",
        };
        var judges = Judges(scenarios, notes);
        var header = new AefRunHeader
        {
            RunId = "import-" + Guid.NewGuid().ToString("N"),
            Producer = options.Producer ?? AefConverter.DefaultProducer,
            Subject = new() { Ref = "other:unknown", Kind = AefSubjectKind.Other },
            StartedAt = DateTimeOffset.UnixEpoch,
            ContentCapture = options.ContentCapture,
            Execution = new() { TargetMode = options.TargetMode },
            Judges = judges.Count == 0 ? null : judges,
            Imported = new()
            {
                From = From + " (version unknown; manifest missing)",
                Asserted = ["runId", "subject", "startedAt", "endedAt", "status", "abortReason", "execution.targetMode", "contentCapture"],
            },
        };
        return Export(new ExportData(header, scenarios, DefaultLane, DateTimeOffset.UnixEpoch, reason, notes)
        {
            Trace = trace,
            TraceSnapshotPath = trace is null ? null : tracePath,
            Reports = Reports(root, options.ContentCapture),
        }, outputDirectory, options);
    }

    private static T? ReadOptional<T>(string path) where T : class => Path.Exists(path) ? ReadRequired<T>(path) : null;

    private static T ReadRequired<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), s_store)
                ?? throw InvalidSource($"The legacy file {Path.GetFileName(path)} contains null.");
        }
        catch (JsonException error)
        {
            throw InvalidSource($"The legacy file {Path.GetFileName(path)} is not readable JSON.", error);
        }
    }
}
