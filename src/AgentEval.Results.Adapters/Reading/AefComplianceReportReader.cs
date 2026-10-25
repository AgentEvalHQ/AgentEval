// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.StoreV1;

namespace AgentEval.Results.Adapters.Reading;

/// <summary>A selected rich report and the custody of the container from which it was read.</summary>
public sealed record AefComplianceReport(AefComplianceDocument Document, AefRunReadInfo Run, string Timestamp);

/// <summary>Selects verified native reports or imports explicitly selected legacy report directories.</summary>
public interface IAefComplianceReportReader
{
    /// <summary>
    /// Reads one subject and regulation, optionally at its recorded timestamp. The caller supplies bounded,
    /// authorized source paths. Null means no captured rich report; invalid or ambiguous evidence throws.
    /// </summary>
    AefComplianceReport? Read(string nativeRunsRoot, IReadOnlyList<string> legacyDirectories,
        string subjectName, string regulation, string? timestamp = null, CancellationToken ct = default);
}

/// <summary>
/// Resolves compliance reports through the shared verified snapshot boundary. Run aliases suppress dual-written
/// legacy copies but never establish original execution provenance. All candidate native runs are verified
/// before filtering, so an edited subject header cannot redirect selection to an unverified legacy sibling.
/// </summary>
public sealed class AefComplianceReportReader : IAefComplianceReportReader
{
    /// <inheritdoc />
    public AefComplianceReport? Read(string nativeRunsRoot, IReadOnlyList<string> legacyDirectories,
        string subjectName, string regulation, string? timestamp = null, CancellationToken ct = default)
        => ReadBounded(nativeRunsRoot, legacyDirectories, subjectName, regulation, new(256L << 20), timestamp, ct);

    internal AefComplianceReport? ReadBounded(string nativeRunsRoot, IReadOnlyList<string> legacyDirectories,
        string subjectName, string regulation, AefReadBudget budget, string? timestamp = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(legacyDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(regulation);
        if (legacyDirectories.Count > 1000) throw Bad("More than 1000 legacy report directories were supplied.");
        var reports = new List<AefComplianceReport>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var nativeDocuments = new List<AefComplianceDocument>();
        var unavailableAt = new List<DateTimeOffset?>();
        var unidentifiedNative = false;
        var reader = new AefComplianceArtifactReader();
        foreach (var directory in AefEvidenceDiscovery.Discover(nativeRunsRoot, ct: ct))
        {
            using var run = AefVerifiedRun.Open(directory, ct, budget.Limit(1L << 30));
            budget.Charge(run.Bytes);
            if (!MatchesSubject(run.Info.SubjectRef, subjectName)) continue;
            var read = reader.Read(run, ct);
            var relevant = read.Documents.Where(d => SameRegulation(d.Evidence.Regulation, regulation)).ToArray();
            var suite = (string?)run.Documents.Run!["suite"]?["ref"];
            if (relevant.Length == 0 && suite is not null && !suite.StartsWith("bench:" + regulation.ToLowerInvariant() + "/", StringComparison.Ordinal)) continue;
            if (!relevant.Any(d => d.IsRich))
            {
                var recordedTime = (string?)run.Documents.Run["endedAt"] ?? (string?)run.Documents.Run["startedAt"];
                unavailableAt.Add(relevant.Length > 0 ? relevant.Max(d => d.Evidence.GeneratedAt)
                    : !read.Run.Imported && DateTimeOffset.TryParse(recordedTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null);
            }
            var knownIds = relevant.Select(d => d.Evidence.SourceRun.RunId).ToHashSet(StringComparer.Ordinal);
            var link = run.Documents.Run["ext"]?["agenteval.store-v1"];
            if (link?["runId"] is JsonValue single && single.TryGetValue<string>(out var id)) knownIds.Add(id);
            if (link?["trialRunIds"] is JsonArray trials)
                foreach (var trial in trials)
                    if (trial is JsonValue value && value.TryGetValue<string>(out var trialId)) knownIds.Add(trialId);
            if (knownIds.Count == 0) unidentifiedNative = true;
            aliases.UnionWith(knownIds);
            nativeDocuments.AddRange(relevant);
            foreach (var document in relevant.Where(d => d.IsRich)) Add(reports, document, read.Run);
        }

        foreach (var directory in legacyDirectories)
        {
            ct.ThrowIfCancellationRequested();
            if (!new[] { "evidence.json", "gdpr-evidence.json", "eu-ai-act-evidence.json",
                    "run.json", "results.ndjson", "seal.json", "metrics.json" }
                .Any(name => Path.Exists(Path.Combine(directory, name)))) continue;
            // An unlinked native invocation may be this report's source. Absence is safer than a legacy backdoor.
            if (unidentifiedNative) continue;
            var temporary = Directory.CreateTempSubdirectory("ae-compliance-read-");
            try
            {
                var conversion = StoreV1Exporter.ExportComplianceDirectoryBounded(directory, Path.Combine(temporary.FullName, "run"), null, ct, budget);
                using var run = AefVerifiedRun.Open(conversion.Directory, ct, budget.Limit(1L << 30));
                budget.Charge(run.Bytes);
                var read = reader.Read(run, ct);
                foreach (var document in read.Documents.Where(d => d.IsRich))
                {
                    if (document.Evidence.Subject.Name != subjectName || !SameRegulation(document.Evidence.Regulation, regulation))
                        throw Bad("A selected legacy directory contains a report for a different subject or regulation.");
                    if (aliases.Contains(document.Evidence.SourceRun.RunId))
                    {
                        var sameId = nativeDocuments.Where(d => d.Evidence.SourceRun.RunId == document.Evidence.SourceRun.RunId).ToArray();
                        if (sameId.Length == 0) continue; // Native capture omitted the report: the alias cannot authorize recovering it.
                        var sameIdentity = sameId.Where(d => AefComplianceArtifactReader.SameIdentity(d.Evidence, document.Evidence)
                            && d.Evidence.SourceRun == document.Evidence.SourceRun).ToArray();
                        if (sameIdentity.Length == 0) throw Bad("A native and legacy report reuse a run ID without agreeing on the full recorded source identity.");
                        var checkedDocuments = sameIdentity.ToList();
                        AefComplianceArtifactReader.AddDocument(checkedDocuments, document);
                        if (!sameIdentity.Any(d => d.IsRich)) continue; // Generic evidence does not establish captured rich content.
                    }
                    Add(reports, document, read.Run);
                }
            }
            finally { temporary.Delete(recursive: true); }
        }
        var selected = reports.Where(r => timestamp is null || r.Timestamp == timestamp)
            .OrderByDescending(r => r.Document.Evidence.GeneratedAt).ToArray();
        if (selected.Length == 0) return null;
        // "Latest" must not silently mean "latest with convenient captured content".
        // Explicit timestamps still permit selecting an older captured report.
        if (timestamp is null && unavailableAt.Any(time => time is null || time >= selected[0].Document.Evidence.GeneratedAt)) return null;
        // The public selector is second precision. Never choose arbitrarily between reports sharing that selector.
        if (selected.Skip(1).Any(r => r.Timestamp == selected[0].Timestamp))
            throw Bad("Multiple distinct reports share the selected timestamp.");
        return selected[0];
    }

    private static void Add(List<AefComplianceReport> reports, AefComplianceDocument document, AefRunReadInfo run)
    {
        var duplicate = reports.FirstOrDefault(r => AefComplianceArtifactReader.SameIdentity(r.Document.Evidence, document.Evidence));
        if (duplicate is not null)
        {
            if (!JsonNode.DeepEquals(JsonNode.Parse(duplicate.Document.Document.GetRawText()), JsonNode.Parse(document.Document.GetRawText())))
                throw Bad("Different rich reports claim the same evidence identity.");
            return;
        }
        reports.Add(new(document, run, document.Evidence.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)));
    }

    private static bool MatchesSubject(string subjectRef, string name) =>
        AefConverter.TypedRef(subjectRef[..subjectRef.IndexOf(':')], name) == subjectRef;
    private static bool SameRegulation(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static InvalidDataException Bad(string actual) => new("Expected: a unique report from completely discovered verified evidence. Actual: " + actual +
        " Suggestions: select an unambiguous original report and resolve invalid evidence. Because: report selection must not silently fall back or invent missing evidence.");
}
