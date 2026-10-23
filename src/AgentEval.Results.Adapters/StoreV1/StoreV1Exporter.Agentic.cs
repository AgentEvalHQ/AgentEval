// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    /// <summary>Imports one original agentic report folder without inferring execution results, timing or custody.</summary>
    public static AefConversion ExportAgenticDirectory(string sourceDirectory, string outputDirectory,
        StoreV1ExportOptions? options = null, CancellationToken ct = default) =>
        ExportAgenticDirectoryBounded(sourceDirectory, outputDirectory, options, ct, null);

    internal static AefConversion ExportAgenticDirectoryBounded(string sourceDirectory, string outputDirectory,
        StoreV1ExportOptions? options, CancellationToken ct, AefReadBudget? budget)
    {
        options ??= new();
        var report = CaptureAgenticReport(sourceDirectory, ct, budget);
        return ExportAgenticReport(report, outputDirectory, options, ct, budget);
    }

    private static AefConversion ExportAgenticReport(AgenticReportDocument report, string outputDirectory,
        StoreV1ExportOptions options, CancellationToken ct, AefReadBudget? budget)
    {
        ct.ThrowIfCancellationRequested();
        if (budget is not null) options = options with { WriteBudget = new(budget.Limit(64L << 20), ct, budget.Charge) };
        const string reason = "Original execution timing, completion and results are unknown: only an original agentic report was supplied.";
        var header = new AefRunHeader
        {
            RunId = "import-" + Guid.NewGuid().ToString("N"), Producer = options.Producer ?? AefConverter.DefaultProducer,
            Subject = new() { Ref = report.SubjectRef, Kind = report.SubjectRef.StartsWith("workflow:", StringComparison.Ordinal) ? AefSubjectKind.Workflow : AefSubjectKind.Agent },
            StartedAt = DateTimeOffset.UnixEpoch, ContentCapture = options.ContentCapture,
            Ext = new JsonObject { [AefReportArtifact.Extension] = report.Artifact.Metadata(options.ContentCapture) },
            Execution = new() { TargetMode = options.TargetMode },
            Imported = new()
            {
                From = From + " (version unknown; agentic report only)",
                Asserted = ["runId", "startedAt", "endedAt", "status", "abortReason", "execution.targetMode", "contentCapture"],
            },
        };
        return Export(new ExportData(header, [], DefaultLane, DateTimeOffset.UnixEpoch, reason,
            [reason, "Imported: not sealed when it ran. SourceRunId and SourceManifestHash are report references, not verified execution custody."])
            { Report = report.Artifact }, outputDirectory, options);
    }

    private static AgenticReportDocument CaptureAgenticReport(string sourceDirectory, CancellationToken ct, AefReadBudget? budget)
    {
        var limits = new AefStagingOptions { MaxFiles = 1000, MaxBytes = 256L << 20, MaxFileBytes = 64L << 20 };
        using var snapshot = budget is null ? AefOwnedSnapshot.Capture(Path.GetFullPath(sourceDirectory), limits, ct)
            : budget.Capture(Path.GetFullPath(sourceDirectory), limits, ct);
        if (new[] { "run.json", "results.ndjson", "seal.json", "metrics.json" }.Any(n => Path.Exists(Path.Combine(snapshot.FullName, n))))
            throw InvalidSource("The report directory contains AEF markers and requires direct AEF verification.");
        return AgenticReportDocument.Read(File.ReadAllBytes(Path.Combine(snapshot.FullName, "agentic-result.json")));
    }

    private static StoreV1Run EnrichAgenticReport(string workspace, string subjectDirectory, StoreV1Run run,
        CancellationToken ct, AefReadBudget? budget)
    {
        if (run.Manifest.Run.Harness != "BenchAgenticCommand") return run;
        var root = Path.Combine(workspace, "benchmarks", "agentic", subjectDirectory);
        if (!Path.Exists(root)) return run;
        AefReportArtifact? selected = null;
        var paths = DiscoverLegacyDirectories(root, "agentic-result.json", ct).Take(1001).ToArray();
        if (paths.Length > 1000) throw InvalidSource("More than 1000 agentic report folders were discovered for the subject.");
        foreach (var path in paths)
        {
            var report = CaptureAgenticReport(path, ct, budget);
            if (report.Artifact.SourceRunId != run.Manifest.Run.RunId) continue;
            var expected = AefConverter.TypedRef(run.Manifest.Subject.Kind == SubjectKind.Workflow ? "workflow" : "agent", run.Manifest.Subject.Name);
            if (report.SubjectRef != expected || report.Artifact.SourceManifestHash != run.Manifest.ContentHash)
                throw InvalidSource("An agentic report disagrees with its selected source manifest identity.");
            if (selected is not null && (selected.GeneratedAt != report.Artifact.GeneratedAt || !selected.Bytes.Span.SequenceEqual(report.Artifact.Bytes.Span)))
                throw InvalidSource("Multiple different agentic reports reference the selected run.");
            selected = report.Artifact;
        }
        return run with { Report = selected };
    }
}
