// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Ingestion;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    private static readonly StringComparer WorkspacePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Discovers exact canonical run folders in a legacy workspace. Incomplete discovery throws.</summary>
    public static IReadOnlyList<string> DiscoverWorkspaceRunDirectories(string workspaceDirectory, CancellationToken ct = default)
    {
        var workspace = Path.GetFullPath(workspaceDirectory);
        var root = Path.Combine(workspace, "subjects");
        ct.ThrowIfCancellationRequested();
        if (!Path.Exists(root)) return [];
        return Array.AsReadOnly(DiscoverLegacyDirectories(root, "manifest.json", ct)
            .Where(path => TryRunParts(workspace, path, out _)).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Resolves a legacy workspace run ID only when exactly one canonical run folder matches.
    /// Incomplete discovery or duplicate IDs require an explicit folder instead.</summary>
    public static string ResolveWorkspaceRunDirectory(string workspaceDirectory, string runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var workspace = Path.GetFullPath(workspaceDirectory);
        var candidates = DiscoverWorkspaceRunDirectories(workspace, ct)
            .Where(path => TryRunParts(workspace, path, out var parts) && parts[4] == runId).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw InvalidSource("No canonical workspace run folder matches the requested ID."),
            _ => throw InvalidSource("The run ID is ambiguous across workspace subjects; select its exact run folder."),
        };
    }

    /// <summary>Imports the exact selected workspace run and matching generic/rich compliance reports.
    /// Source files are captured through bounded snapshots and remain unchanged. Matching a recorded source
    /// reference does not verify original custody; the resulting AEF run remains imported.</summary>
    public static AefConversion ExportWorkspaceRun(string workspaceDirectory, string runDirectory, string outputDirectory,
        StoreV1ExportOptions? options = null, CancellationToken ct = default)
        => ExportWorkspaceRunBounded(workspaceDirectory, runDirectory, outputDirectory, options, ct, null);

    internal static AefConversion ExportWorkspaceRunBounded(string workspaceDirectory, string runDirectory, string outputDirectory,
        StoreV1ExportOptions? options, CancellationToken ct, AefReadBudget? budget)
    {
        var workspace = Path.GetFullPath(workspaceDirectory);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runDirectory));
        if (!TryRunParts(workspace, source, out var parts))
            throw InvalidSource("The selected folder is not subjects/<agents|workflows>/<subject>/runs/<id> in this workspace.");
        return ExportDirectoryCore(source, outputDirectory, options ?? new(), ct, run =>
        {
            ValidateManifest(run.Manifest);
            var manifest = run.Manifest;
            var kind = manifest.Subject.Kind == SubjectKind.Workflow ? "workflows" : "agents";
            if (!WorkspacePathComparer.Equals(manifest.Run.RunId, parts[4]) || !WorkspacePathComparer.Equals(kind, parts[1]))
                throw InvalidSource("The selected folder disagrees with its manifest run ID or subject kind.");
            var subjectDirectory = Path.GetDirectoryName(Path.GetDirectoryName(source)!)!;
            var history = StoreV1History.ReadFile(Path.Combine(subjectDirectory, "history.jsonl"), ct, budget);
            run = run with { HistoryRecords = history?.Where(r => r.Entry.RunId == manifest.Run.RunId).ToArray() };
            return EnrichWorkspaceRun(workspace, parts[2], run, ct, budget);
        }, budget);
    }

    private static StoreV1Run EnrichWorkspaceRun(string workspace, string subjectDirectory, StoreV1Run run, CancellationToken ct, AefReadBudget? budget)
    {
        var root = Path.Combine(workspace, "compliance");
        if (!Path.Exists(root)) return run;
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var marker in new[] { "evidence.json", "gdpr-evidence.json", "eu-ai-act-evidence.json" })
            foreach (var path in DiscoverLegacyDirectories(root, marker, ct))
            {
                var relative = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
                if (relative.Length == 3 && WorkspacePathComparer.Equals(relative[1], subjectDirectory)) paths.Add(path);
                if (paths.Count > 1000) throw InvalidSource("Workspace compliance discovery exceeds 1000 report directories.");
            }

        var documents = new List<AefComplianceDocument>();
        var artifacts = new List<byte[]>();
        var pdfs = new List<(ComplianceEvidence Evidence, byte[] Bytes)>();
        long bytes = 0;
        foreach (var path in paths)
        {
            var captured = CaptureComplianceDirectory(path, ct, budget);
            var evidence = captured[0].Document.Evidence;
            // The directory name alone is not a subject or execution identity.
            if (evidence.Subject.Name != run.Manifest.Subject.Name || evidence.Subject.Kind != run.Manifest.Subject.Kind
                || evidence.SourceRun.RunId != run.Manifest.Run.RunId) continue;
            if (evidence.SourceRun.ManifestHash != run.Manifest.ContentHash)
                throw InvalidSource("A compliance report's recorded source hash disagrees with the selected run manifest.");
            foreach (var artifact in captured)
            {
                bytes += artifact.Bytes.LongLength + (artifact.Pdf?.LongLength ?? 0);
                if (bytes > 256L << 20) throw InvalidSource("Matching workspace compliance reports exceed 256 MiB.");
                var count = documents.Count;
                AefComplianceArtifactReader.AddDocument(documents, artifact.Document);
                if (artifact.Document.IsRich && documents.Count != count) artifacts.Add(artifact.Bytes);
                if (artifact.Pdf is { } pdf) pdfs.Add((artifact.Document.Evidence, pdf));
            }
        }
        ct.ThrowIfCancellationRequested();
        var evidenceDocuments = documents.GroupBy(d => (d.Evidence.Regulation, d.Evidence.GeneratedAt))
            .Select(g => g.First().Evidence).ToArray();
        return run with
        {
            ComplianceEvidence = evidenceDocuments,
            ComplianceArtifacts = artifacts,
            CompliancePdfs = pdfs.Select(p => new StoreV1CompliancePdf("E-compliance-" +
                (Array.FindIndex(evidenceDocuments, e => AefComplianceArtifactReader.SameIdentity(e, p.Evidence)) + 1), p.Bytes)).ToArray(),
        };
    }

    internal static IReadOnlyList<string> DiscoverLegacyDirectories(string root, string marker, CancellationToken ct)
    {
        var result = AefRunDiscovery.DiscoverByMarker(root, marker,
            new() { MaxDepth = 8, MaxEntries = 100_000, MaxRuns = 1000 }, ct);
        if (!result.Complete) throw InvalidSource("Legacy workspace discovery is incomplete; refusing a partial selection.");
        return result.Candidates.Select(c => c.Directory).ToArray();
    }

    private static bool TryRunParts(string workspace, string directory, out string[] parts)
    {
        parts = Path.GetRelativePath(workspace, directory).Split(Path.DirectorySeparatorChar);
        return parts.Length == 5 && WorkspacePathComparer.Equals(parts[0], "subjects")
            && (WorkspacePathComparer.Equals(parts[1], "agents") || WorkspacePathComparer.Equals(parts[1], "workflows"))
            && WorkspacePathComparer.Equals(parts[3], "runs");
    }
}
