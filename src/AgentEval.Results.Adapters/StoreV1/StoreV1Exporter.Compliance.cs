// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    /// <summary>
    /// Imports the generic and rich reports in one legacy compliance timestamp directory.
    /// The reports retain their exact bytes. No execution results, original timing or completion are inferred
    /// from a report: the imported container records those fields as unknown with explicit importer claims.
    /// </summary>
    public static AefConversion ExportComplianceDirectory(string sourceDirectory, string outputDirectory,
        StoreV1ExportOptions? options = null, CancellationToken ct = default)
        => ExportComplianceDirectoryBounded(sourceDirectory, outputDirectory, options, ct, null);

    internal static AefConversion ExportComplianceDirectoryBounded(string sourceDirectory, string outputDirectory,
        StoreV1ExportOptions? options, CancellationToken ct, AefReadBudget? budget)
    {
        options ??= new();
        var captured = CaptureComplianceDirectory(sourceDirectory, ct, budget);
        if (budget is not null) options = options with { WriteBudget = new(budget.Limit(64L << 20), ct, budget.Charge) };
        var documents = captured.Select(c => c.Document).ToList();
        var artifacts = captured.Select(c => c.Bytes).ToList();
        var subject = documents[0].Evidence.Subject;
        const string reason = "Original execution timing, completion and results are unknown: only legacy compliance reports were supplied.";
        var notes = new List<string>
        {
            reason,
            "Imported: not sealed when it ran. The ingest seal protects the captured reports from conversion onward.",
            "SourceRun is a recorded report reference, not verified provenance of the original execution.",
            "The importer assigns a container run ID and Unix epoch placeholder times; they do not identify or date the original execution.",
        };
        var header = new AefRunHeader
        {
            RunId = "import-" + Guid.NewGuid().ToString("N"),
            Producer = options.Producer ?? AefConverter.DefaultProducer,
            Subject = new()
            {
                Ref = AefConverter.TypedRef(subject.Kind == SubjectKind.Workflow ? "workflow" : "agent", subject.Name),
                Kind = subject.Kind == SubjectKind.Workflow ? AefSubjectKind.Workflow : AefSubjectKind.Agent,
            },
            StartedAt = DateTimeOffset.UnixEpoch,
            ContentCapture = options.ContentCapture,
            Execution = new() { TargetMode = options.TargetMode },
            Imported = new()
            {
                From = From + " (version unknown; compliance reports only)",
                Asserted = ["runId", "startedAt", "endedAt", "status", "abortReason", "execution.targetMode", "contentCapture"],
            },
        };
        ct.ThrowIfCancellationRequested();
        return Export(new ExportData(header, [], DefaultLane, DateTimeOffset.UnixEpoch, reason, notes)
        {
            ComplianceArtifacts = artifacts,
            CompliancePdfs = captured.Select((c, i) => (c, i)).Where(x => x.c.Pdf is not null)
                .Select(x => new StoreV1CompliancePdf("E-compliance-original-" + (x.i + 1), x.c.Pdf!)).ToArray(),
        }, outputDirectory, options);
    }

    private sealed record CapturedCompliance(AefComplianceDocument Document, byte[] Bytes, byte[]? Pdf = null);

    private static List<CapturedCompliance> CaptureComplianceDirectory(string sourceDirectory, CancellationToken ct, AefReadBudget? budget = null)
    {
        var limits = new AefStagingOptions
        {
            MaxFiles = 1000, MaxBytes = 256L << 20, MaxFileBytes = 64L << 20,
        };
        using var snapshot = budget is null ? AefOwnedSnapshot.Capture(Path.GetFullPath(sourceDirectory), limits, ct)
            : budget.Capture(Path.GetFullPath(sourceDirectory), limits, ct);
        if (new[] { "run.json", "results.ndjson", "seal.json", "metrics.json" }.Any(name => Path.Exists(Path.Combine(snapshot.FullName, name))))
            throw InvalidSource("The directory contains AEF markers and requires direct AEF verification.");
        var reader = new AefComplianceArtifactReader();
        var documents = new List<AefComplianceDocument>();
        var artifacts = new List<CapturedCompliance>();
        foreach (var name in new[] { "evidence.json", "gdpr-evidence.json", "eu-ai-act-evidence.json" })
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(snapshot.FullName, name);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var document = reader.ReadDocument(bytes, name);
            var first = documents.FirstOrDefault()?.Evidence;
            if (first is not null && !AefComplianceArtifactReader.SameIdentity(first, document.Evidence))
                throw InvalidSource("Reports in the selected compliance directory name different evidence identities.");
            AefComplianceArtifactReader.AddDocument(documents, document);
            artifacts.Add(new(document, bytes));
        }
        if (documents.Count == 0) throw InvalidSource("No supported compliance documents were found.");
        var pdf = Path.Combine(snapshot.FullName, "report.pdf");
        if (File.Exists(pdf)) artifacts[0] = artifacts[0] with { Pdf = File.ReadAllBytes(pdf) };
        return artifacts;
    }
}
