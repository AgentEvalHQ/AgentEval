// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.StoreV1;

namespace AgentEval.Results.Adapters.Reading;

/// <summary>An original agentic report document with its container's observed custody.</summary>
public sealed record AefAgenticReport(JsonElement Document, AefRunReadInfo Run, string Timestamp);

/// <summary>Selects an existing agentic report through verified AEF or bounded legacy conversion.</summary>
public interface IAefAgenticReportReader
{
    /// <summary>Reads a unique subject/timestamp report. Null denotes absent captured content; invalid or ambiguous sources throw.</summary>
    AefAgenticReport? Read(string nativeRunsRoot, IReadOnlyList<string> legacyDirectories,
        string subjectName, string? timestamp = null, CancellationToken ct = default);
}

/// <summary>Verifies all discovered native candidates before filtering; native aliases suppress legacy fallback.</summary>
public sealed class AefAgenticReportReader : IAefAgenticReportReader
{
    private sealed record Candidate(AefReportArtifact Identity, AefAgenticReport? Report);

    /// <inheritdoc />
    public AefAgenticReport? Read(string nativeRunsRoot, IReadOnlyList<string> legacyDirectories,
        string subjectName, string? timestamp = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(legacyDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectName);
        if (legacyDirectories.Count > 1000) throw AgenticReportDocument.Bad("More than 1000 legacy report folders were supplied.");
        var budget = new AefReadBudget(256L << 20);
        var candidates = new List<Candidate>();
        var nativeReports = new List<Candidate>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var unavailableAt = new List<DateTimeOffset?>();
        var unidentifiedNative = false;
        var reader = new AefRunEvidenceReader();
        foreach (var directory in AefEvidenceDiscovery.Discover(nativeRunsRoot,
            new() { MaxRuns = 1000, MaxEntries = 100_000 }, ct))
        {
            var run = reader.Read(directory, budget.Limit(64L << 20), ct);
            budget.Charge(run.Bytes);
            if (!MatchesSubject(run.Info.SubjectRef, subjectName)) continue;
            var descriptor = ReportMetadata(run);
            var suite = run.Header.TryGetProperty("suite", out var s) ? s.GetProperty("ref").GetString() : null;
            if (descriptor is null && suite?.StartsWith("bench:agentic/", StringComparison.Ordinal) != true) continue;
            var ids = AefWorkspaceEvidenceReader.Aliases(run).ToHashSet(StringComparer.Ordinal);
            if (descriptor is { } candidate)
            {
                if (ids.Count > 0 && !ids.Contains(candidate.Identity.SourceRunId))
                    throw AgenticReportDocument.Bad("The report and native execution name different source run IDs.");
                ids.Add(candidate.Identity.SourceRunId);
                nativeReports.Add(candidate);
                Add(candidates, candidate);
            }
            else unavailableAt.Add(run.EndedAt ?? run.StartedAt);
            if (ids.Count == 0) unidentifiedNative = true;
            aliases.UnionWith(ids);
        }
        foreach (var directory in legacyDirectories)
        {
            ct.ThrowIfCancellationRequested();
            if (unidentifiedNative) continue;
            if (!new[] { "agentic-result.json", "run.json", "results.ndjson", "seal.json", "metrics.json" }
                .Any(name => Path.Exists(Path.Combine(directory, name)))) continue;
            var temp = Directory.CreateTempSubdirectory("ae-agentic-report-");
            try
            {
                var conversion = StoreV1Exporter.ExportAgenticDirectoryBounded(directory, Path.Combine(temp.FullName, "run"), null, ct, budget);
                var run = reader.Read(conversion.Directory, budget.Limit(64L << 20), ct);
                budget.Charge(run.Bytes);
                if (!MatchesSubject(run.Info.SubjectRef, subjectName))
                    throw AgenticReportDocument.Bad("A selected legacy folder contains a different subject.");
                var candidate = ReportMetadata(run) ?? throw AgenticReportDocument.Bad("The converted report has no descriptor.");
                if (aliases.Contains(candidate.Identity.SourceRunId))
                {
                    var known = nativeReports.Where(n => n.Identity.SourceRunId == candidate.Identity.SourceRunId).ToArray();
                    if (known.Length > 0)
                    {
                        if (known.Any(n => !SameIdentity(n.Identity, candidate.Identity)))
                            throw AgenticReportDocument.Bad("Native and legacy reports reuse an ID with different full source identities.");
                        foreach (var native in known.Where(n => n.Report is not null))
                            if (!native.Identity.Bytes.Span.SequenceEqual(candidate.Identity.Bytes.Span))
                                throw AgenticReportDocument.Bad("Native and legacy reports disagree on original document bytes.");
                    }
                    continue; // An alias does not authorize recovering omitted native content.
                }
                Add(candidates, candidate);
            }
            finally { temp.Delete(recursive: true); }
        }
        var selected = candidates.Where(c => timestamp is null || Timestamp(c.Identity) == timestamp)
            .OrderByDescending(c => c.Identity.GeneratedAt).ToArray();
        if (selected.Length == 0) return null;
        if (selected.Skip(1).Any(c => Timestamp(c.Identity) == Timestamp(selected[0].Identity)))
            throw AgenticReportDocument.Bad("Different reports share the selected second-precision timestamp.");
        if (timestamp is null && unavailableAt.Any(t => t is null || t >= selected[0].Identity.GeneratedAt)) return null;
        return selected[0].Report;
    }

    private static Candidate? ReportMetadata(AefRunEvidence run)
    {
        if (!run.Header.TryGetProperty("ext", out var ext) || !ext.TryGetProperty(AefReportArtifact.Extension, out var meta)) return null;
        try
        {
            if (AgenticReportDocument.Text(meta, "type") != "agentic-result") return null;
            if (meta.GetProperty("version").GetInt32() != 1 || AgenticReportDocument.Text(meta, "evidenceId") != AefReportArtifact.EvidenceId)
                throw AgenticReportDocument.Bad("Unsupported report descriptor version or evidence identity.");
            var captured = meta.GetProperty("captured").GetBoolean();
            if (captured != (run.Info.ContentCapture == "on")) throw AgenticReportDocument.Bad("Report availability contradicts capture policy.");
            var identity = new AefReportArtifact
            {
                Type = "agentic-result", SourceRunId = AgenticReportDocument.Text(meta, "sourceRunId"),
                SourceManifestHash = AgenticReportDocument.Text(meta, "sourceManifestHash"),
                GeneratedAt = meta.GetProperty("generatedAt").GetDateTimeOffset(), Bytes = ReadOnlyMemory<byte>.Empty,
            };
            identity.Metadata(Writing.AefContentCapture.Off);
            var bytes = run.CapturedContent(AefReportArtifact.EvidenceId);
            if (!captured)
            {
                if (bytes is not null) throw AgenticReportDocument.Bad("An unavailable report carries captured bytes.");
                return new(identity, null);
            }
            if (bytes is null || !run.Evidence.Any(e => e.GetProperty("evidenceId").GetString() == AefReportArtifact.EvidenceId
                && e.GetProperty("kind").GetString() == "document")) throw AgenticReportDocument.Bad("The original report document is absent.");
            var report = AgenticReportDocument.Read(bytes.Value);
            if (report.SubjectRef != run.Info.SubjectRef || !SameIdentity(identity, report.Artifact))
                throw AgenticReportDocument.Bad("The original report contradicts its subject or descriptor identity.");
            return new(report.Artifact, new(report.Document, run.Info, report.Timestamp));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { throw AgenticReportDocument.Bad("The report descriptor is malformed.", error); }
    }

    private static void Add(List<Candidate> candidates, Candidate candidate)
    {
        var duplicate = candidates.FirstOrDefault(c => SameIdentity(c.Identity, candidate.Identity));
        if (duplicate is null) { candidates.Add(candidate); return; }
        if ((duplicate.Report is null) != (candidate.Report is null) || !duplicate.Identity.Bytes.Span.SequenceEqual(candidate.Identity.Bytes.Span))
            throw AgenticReportDocument.Bad("Reports claim the same identity with different availability or bytes.");
    }
    private static bool SameIdentity(AefReportArtifact a, AefReportArtifact b) =>
        a.Type == b.Type && a.SourceRunId == b.SourceRunId && a.SourceManifestHash == b.SourceManifestHash && a.GeneratedAt == b.GeneratedAt;
    private static string Timestamp(AefReportArtifact artifact) => artifact.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
    private static bool MatchesSubject(string subjectRef, string name) => AefConverter.TypedRef(subjectRef[..subjectRef.IndexOf(':')], name) == subjectRef;
}
