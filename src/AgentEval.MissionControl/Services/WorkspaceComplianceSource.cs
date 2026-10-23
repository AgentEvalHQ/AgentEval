// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using AgentEval.Output;
using AgentEval.Results.Adapters;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using Path = System.IO.Path;

namespace AgentEval.MissionControl.Services;

internal sealed record WorkspaceComplianceReport(AefComplianceDocument Document, AefRunReadInfo Container, string Timestamp,
    bool SourceAvailable, string? SourceId)
{
    internal ComplianceEvidence Evidence => Document.Evidence;
    internal string? CustodyWarning => Container.Imported
        ? SourceAvailable ? "imported-not-sealed-when-run" : "source-run-not-found"
        : Container.Integrity == "intact" ? null : "unsealed-evidence";
}

// Report discovery/selection only; matrix grouping and UI presentation live in their existing consumers.
internal sealed class WorkspaceComplianceSource(IOutputStoreReader store, IWorkspaceRunSource source)
{
    private readonly List<(AefWorkspaceRun Run, string Regulation)> _unavailable = [];
    private Task<IReadOnlyList<WorkspaceComplianceReport>>? _read;
    private readonly object _sync = new();
    internal Task<IReadOnlyList<WorkspaceComplianceReport>> ReadAsync(CancellationToken ct)
    {
        lock (_sync) return _read ??= LoadAsync(ct);
    }

    private async Task<IReadOnlyList<WorkspaceComplianceReport>> LoadAsync(CancellationToken ct)
    {
        var runs = await source.ReadAsync(ct);
        var reader = new AefComplianceArtifactReader();
        var reports = new List<WorkspaceComplianceReport>();
        var documents = new List<AefComplianceDocument>();
        var budget = new AefReadBudget(256L << 20);
        foreach (var run in runs)
        {
            budget.Charge(run.Evidence.Bytes);
            var captured = reader.Read(run.Evidence, ct).Documents;
            foreach (var document in captured)
                Add(document, run.Evidence.Info, Timestamp(document.Evidence.GeneratedAt), true, run.SourceId);
            if (!run.Evidence.Info.Imported && run.Evidence.Header.TryGetProperty("suite", out var suite)
                && suite.TryGetProperty("ref", out var reference) && reference.GetString() is { } suiteRef
                && suiteRef.StartsWith("bench:", StringComparison.Ordinal) && suiteRef.IndexOf('/') is var slash && slash > 6)
            {
                var regulation = suiteRef[6..slash];
                if (!captured.Any(d => SameRegulation(d.Evidence.Regulation, regulation))) _unavailable.Add((run, regulation));
            }
        }

        if (store.WorkspaceRoot is { } root)
        {
            var compliance = Path.Combine(root, "compliance");
            if (Path.Exists(compliance))
            {
                var directories = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var marker in new[] { "evidence.json", "gdpr-evidence.json", "eu-ai-act-evidence.json" })
                    directories.UnionWith(StoreV1Exporter.DiscoverLegacyDirectories(compliance, marker, ct));
                if (directories.Count > 1000) throw Bad("More than 1000 legacy report directories.");
                foreach (var directory in directories)
                {
                    var parts = Path.GetRelativePath(compliance, directory).Split(Path.DirectorySeparatorChar);
                    if (parts.Length != 3) throw Bad("A legacy compliance report has an unsupported directory layout.");
                    Import(directory, parts[2]);
                }
            }
        }
        else if (store.IsAvailable)
        {
            // Semantic stores are converter inputs only. Their indexes can include orphan reports.
            var subjects = new List<SubjectIdentity>();
            await foreach (var subject in store.ListSubjectsAsync(ct: ct))
            {
                if (subjects.Count == 1000) throw Bad("More than 1000 subjects in the semantic report inventory.");
                subjects.Add(subject.Identity);
            }
            var count = 0;
            await foreach (var pointer in store.ListComplianceEvidenceAsync(ct: ct))
            {
                if (++count > 1000) throw Bad("More than 1000 semantic report pointers.");
                var matching = subjects.Where(s => s.Name == pointer.SubjectName).ToArray();
                if (matching.Length != 1) throw Bad("A semantic report pointer has no unique subject identity.");
                var evidence = await store.GetComplianceEvidenceAsync(pointer.Regulation, matching[0], pointer.Timestamp, ct)
                    ?? throw Bad("An indexed compliance report is unavailable.");
                var temporary = Directory.CreateTempSubdirectory("ae-semantic-compliance-");
                try
                {
                    var accountant = new StoreV1Exporter.SemanticAccountant(budget, ct);
                    accountant.Account(evidence);
                    var json = StoreV1Exporter.SerializeComplianceEvidence(evidence);
                    File.WriteAllBytes(Path.Combine(temporary.FullName, "evidence.json"), json);
                    Import(temporary.FullName, pointer.Timestamp);
                }
                finally { temporary.Delete(true); }
            }
        }
        return reports;

        void Import(string directory, string timestamp)
        {
            var temporary = Directory.CreateTempSubdirectory("ae-workspace-compliance-");
            try
            {
                var converted = StoreV1Exporter.ExportComplianceDirectoryBounded(directory,
                    Path.Combine(temporary.FullName, "run"), null, ct, budget);
                var snapshot = new AefRunEvidenceReader().Read(converted.Directory, 64L << 20, ct);
                foreach (var document in reader.Read(snapshot, ct).Documents)
                {
                    var e = document.Evidence;
                    var linked = runs.Where(r => MatchesSubject(r.Evidence.Info.SubjectRef, e.Subject)
                        && (r.Evidence.Info.RunId == e.SourceRun.RunId || r.RunAliases.Contains(e.SourceRun.RunId, StringComparer.Ordinal))).ToArray();
                    if (linked.Length > 1) throw Bad("Multiple sources claim a legacy report's execution.");
                    if (linked.Length == 1)
                    {
                        var captured = reports.Where(r => AefComplianceArtifactReader.SameIdentity(r.Evidence, e)).ToArray();
                        // A linked native capture-off source cannot authorize recovery through its legacy sibling.
                        if (captured.Length == 0) continue;
                        AefComplianceArtifactReader.AddDocument(documents, document);
                        continue;
                    }
                    // Match the existing report reader: an unidentified native invocation cannot authorize legacy recovery.
                    if (_unavailable.Any(u => u.Run.RunAliases.Count == 0
                        && MatchesSubject(u.Run.Evidence.Info.SubjectRef, e.Subject) && SameRegulation(u.Regulation, e.Regulation))) continue;
                    Add(document, snapshot.Info, timestamp, false);
                }
            }
            finally { temporary.Delete(true); }
        }

        void Add(AefComplianceDocument document, AefRunReadInfo info, string timestamp, bool sourceAvailable, string? sourceId = null)
        {
            AefComplianceArtifactReader.AddDocument(documents, document);
            if (reports.Any(r => AefComplianceArtifactReader.SameIdentity(r.Evidence, document.Evidence))) return;
            reports.Add(new(document, info, timestamp, sourceAvailable, sourceId));
        }
    }

    internal void EnsureLatestAvailable(WorkspaceComplianceReport report)
    {
        if (_unavailable.Any(u => MatchesSubject(u.Run.Evidence.Info.SubjectRef, report.Evidence.Subject)
            && SameRegulation(u.Regulation, report.Evidence.Regulation)
            && ((u.Run.Evidence.EndedAt ?? u.Run.Evidence.StartedAt) is not { } at || at >= report.Evidence.GeneratedAt)))
            throw Bad("The newest matching compliance report is unavailable; an older report cannot stand in for it.");
    }

    private static bool SameRegulation(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool MatchesSubject(string subjectRef, SubjectIdentity subject)
    {
        var kind = subjectRef[..subjectRef.IndexOf(':')];
        return (subject.Kind == SubjectKind.Workflow ? kind == "workflow" : kind is "agent" or "model")
            && AefConverter.TypedRef(kind, subject.Name) == subjectRef;
    }

    internal static string Timestamp(DateTimeOffset time) => time.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
    internal static InvalidDataException Bad(string actual) => new("Expected: complete unambiguous compliance evidence. Actual: " + actual
        + " Suggestions: inspect the original reports and select an exact source. Because: compliance views must not silently omit or choose evidence.");
}
