// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Output;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Adapters.Reading;

/// <summary>A verified compliance blob and its generic identity. Document retains the original JSON fields.</summary>
public sealed record AefComplianceDocument(string EvidenceId, ComplianceEvidence Evidence, JsonElement Document, bool IsRich);

/// <summary>Compliance artifacts read from one verified run, with its observed custody information.</summary>
public sealed record AefComplianceReadResult(AefRunReadInfo Run, IReadOnlyList<AefComplianceDocument> Documents);

/// <summary>Reads captured compliance artifacts without consulting legacy files or fetching external links.</summary>
public interface IAefComplianceArtifactReader
{
    /// <summary>Verifies one owned snapshot and reads its referenced compliance blobs.</summary>
    AefComplianceReadResult Read(string directory, CancellationToken ct = default);
}

/// <summary>Shared compliance reading boundary for renderers and workspace views.</summary>
public sealed class AefComplianceArtifactReader : IAefComplianceArtifactReader
{
    private readonly long _maxArtifactBytes;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        MaxDepth = 64,
    };

    /// <summary>Creates a reader with a bounded artifact size (64 MiB by default).</summary>
    public AefComplianceArtifactReader(long maxArtifactBytes = 64L << 20)
    {
        if (maxArtifactBytes <= 0 || maxArtifactBytes > 1L << 30)
            throw new ArgumentOutOfRangeException(nameof(maxArtifactBytes), "Expected: an artifact limit from 1 byte to 1 GiB. Actual: limit outside that range. Suggestions: use a bounded positive limit. Because: artifacts must be read within the run byte budget.");
        _maxArtifactBytes = maxArtifactBytes;
    }

    /// <inheritdoc />
    public AefComplianceReadResult Read(string directory, CancellationToken ct = default)
    {
        using var run = AefVerifiedRun.Open(directory, ct);
        return Read(run, ct);
    }

    internal AefComplianceReadResult Read(AefVerifiedRun run, CancellationToken ct)
    {
        var documents = new List<AefComplianceDocument>();
        foreach (var (_, artifact) in run.Documents.Evidence.Objects.Where(x => (string?)x.Value["kind"] == "compliance_artifact"))
        {
            ct.ThrowIfCancellationRequested();
            var id = (string)artifact["evidenceId"]!;
            var digest = (string?)artifact["link"]?["blob"];
            if (digest is null) throw Bad("Compliance artifact " + id + " is not a captured local blob.");
            var bytes = run.Folder.Read(AefRunFolder.BlobPath(digest["sha256:".Length..]), _maxArtifactBytes);
            AddDocument(documents, ReadDocument(bytes, id, run.Info.SubjectRef));
        }
        return new(run.Info, documents);
    }

    // Workspace callers already own verified bytes. Reopening paths here could mix two source revisions.
    internal AefComplianceReadResult Read(AefRunEvidence run, CancellationToken ct = default)
    {
        var documents = new List<AefComplianceDocument>();
        foreach (var artifact in run.Evidence.Where(e => e.GetProperty("kind").GetString() == "compliance_artifact"))
        {
            ct.ThrowIfCancellationRequested();
            var id = artifact.GetProperty("evidenceId").GetString()!;
            if (run.CapturedContent(id) is not { } bytes)
                throw Bad("Compliance artifact " + id + " is not a captured local blob.");
            if (bytes.Length > _maxArtifactBytes)
                throw Bad("Compliance artifact " + id + " exceeds the configured artifact byte limit.");
            AddDocument(documents, ReadDocument(bytes.ToArray(), id, run.Info.SubjectRef));
        }
        return new(run.Info, documents);
    }

    // Legacy conversion uses exactly the same shape and ambiguity checks before preserving the bytes.
    internal AefComplianceDocument ReadDocument(byte[] bytes, string id, string? subjectRef = null)
    {
        using var json = Parse(bytes, id);
        var document = json.RootElement;
        var rich = document.ValueKind == JsonValueKind.Object && document.TryGetProperty("base", out _);
        var basis = rich ? document.GetProperty("base") : document;
        ComplianceArtifactShape.Validate(basis);
        ComplianceEvidence evidence;
        try { evidence = basis.Deserialize<ComplianceEvidence>(Json) ?? throw Bad("Compliance artifact " + id + " contains null."); }
        catch (JsonException error) { throw Bad("Compliance artifact " + id + " has an unsupported evidence shape.", error); }
        Validate(evidence, document, rich, subjectRef);
        return new(id, evidence, document.Clone(), rich);
    }

    internal static void AddDocument(List<AefComplianceDocument> documents, AefComplianceDocument candidate)
    {
        var prior = documents.Where(d => SameIdentity(d.Evidence, candidate.Evidence)).ToList();
        if (prior.Any(d => !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(d.Evidence, Json), JsonSerializer.SerializeToNode(candidate.Evidence, Json))))
            throw Bad("Compliance artifacts disagree about the same recorded evidence identity.");
        var sameFormat = prior.FirstOrDefault(d => d.IsRich == candidate.IsRich);
        if (sameFormat is not null)
        {
            if (!JsonNode.DeepEquals(JsonNode.Parse(sameFormat.Document.GetRawText()), JsonNode.Parse(candidate.Document.GetRawText())))
                throw Bad("Conflicting compliance documents claim the same identity and format.");
            return;
        }
        documents.Add(candidate);
    }

    private JsonDocument Parse(byte[] bytes, string id)
    {
        try
        {
            AefJsonReader.Check(bytes, checked((int)_maxArtifactBytes));
            return JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
        }
        catch (AefEncodingException error) { throw Bad("Compliance artifact " + id + " is not unambiguous I-JSON.", error); }
        catch (JsonException error) { throw Bad("Compliance artifact " + id + " is not readable JSON.", error); }
    }

    internal static bool SameIdentity(ComplianceEvidence a, ComplianceEvidence b) =>
        a.Regulation == b.Regulation && a.Subject.Kind == b.Subject.Kind && a.Subject.Name == b.Subject.Name
        && a.GeneratedAt == b.GeneratedAt && a.SourceRun.RunId == b.SourceRun.RunId;

    private static void Validate(ComplianceEvidence evidence, JsonElement document, bool rich, string? subjectRef)
    {
        if (string.IsNullOrWhiteSpace(evidence.SchemaVersion) || string.IsNullOrWhiteSpace(evidence.Regulation)
            || evidence.Subject is null || string.IsNullOrWhiteSpace(evidence.Subject.Name) || evidence.SourceRun is null
            || string.IsNullOrWhiteSpace(evidence.SourceRun.RunId) || evidence.Controls is null
            || evidence.Controls.Any(c => c is null || c.ScenarioRefs is null) || evidence.Summary is null
            || evidence.Attestation is null || evidence.GeneratedAt == default)
            throw Bad("The compliance artifact is missing required identity or evidence fields.");
        if (subjectRef is not null && AefConverter.TypedRef(subjectRef[..subjectRef.IndexOf(':')], evidence.Subject.Name) != subjectRef)
            throw Bad("The compliance artifact names a different subject than its containing run.");
        if (subjectRef is not null)
        {
            var kind = subjectRef[..subjectRef.IndexOf(':')];
            // Model benchmark producers use the historical Agent subject kind in their report wrapper.
            var matchesKind = evidence.Subject.Kind == SubjectKind.Workflow ? kind == "workflow" : kind is "agent" or "model";
            if (!matchesKind) throw Bad("The compliance artifact subject kind disagrees with its containing run.");
        }
        if (rich && (!document.TryGetProperty("compositeTree", out var tree) || tree.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object))
            throw Bad("The rich compliance artifact has no recorded composite tree or summary.");
    }

    private static InvalidDataException Bad(string actual, Exception? inner = null) => new(
        "Expected: consistent captured compliance artifacts from a valid AEF run. Actual: " + actual +
        " Suggestions: select the original evidence or report the artifact as unavailable." +
        " Because: missing or conflicting evidence cannot support a reconstructed report.", inner);
}
