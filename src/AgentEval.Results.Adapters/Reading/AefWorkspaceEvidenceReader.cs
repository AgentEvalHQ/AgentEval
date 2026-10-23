// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Output;

namespace AgentEval.Results.Adapters.Reading;

/// <summary>One verified workspace source. Source identity is distinct from the execution's recorded run ID.</summary>
public sealed record AefWorkspaceRun(string SourceId, AefRunEvidence Evidence, IReadOnlyList<string> RunAliases)
{
    // Temporary conversions are not independent stored snapshots of the subject's history index.
    internal bool ConvertedOnRead { get; init; }
}

/// <summary>Complete bounded workspace evidence. Inventory and solution configuration are separate concerns.</summary>
public sealed class AefWorkspaceEvidence
{
    internal AefWorkspaceEvidence(IEnumerable<AefWorkspaceRun> runs) => Runs = Array.AsReadOnly(runs.ToArray());

    /// <summary>Native evidence and converted legacy evidence, with explicitly linked legacy siblings suppressed.</summary>
    public IReadOnlyList<AefWorkspaceRun> Runs { get; }

    /// <summary>Selects an unambiguous recorded ID or alias, optionally restricted to an opaque source identity.</summary>
    public AefWorkspaceRun? FindRun(string runId, string? sourceId = null)
    {
        var matches = Runs.Where(r => (sourceId is null || r.SourceId == sourceId)
            && (r.Evidence.Info.RunId == runId || r.RunAliases.Contains(runId, StringComparer.Ordinal))).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException("Expected: one workspace source for the requested run. Actual: ambiguous run ID or alias. " +
                "Suggestions: select the source identity. Because: duplicate execution IDs must not select an arbitrary run."),
        };
    }
}

/// <summary>Reads a complete workspace through verified AEF, converting exact legacy folders when necessary.</summary>
public interface IAefWorkspaceEvidenceReader
{
    /// <summary>Reads an authorized .agenteval directory without modifying it or creating an index. The byte budget
    /// includes captured legacy inputs and cumulative conversion writes, including rewrites and sealing.
    /// Native snapshots count their captured bytes. Throws on partial or invalid evidence.</summary>
    AefWorkspaceEvidence Read(string workspaceDirectory, long maxBytes = 256L << 20, CancellationToken ct = default);

    /// <summary>Reads a configured store. Filesystem stores use exact directory discovery; semantic stores are
    /// converted with a cumulative input and output work budget, including discovery, rewrites and sealing.
    /// Semantic serialization also checks conservative token scratch and buffer capacity before allocation;
    /// a record may be refused even when its final serialized bytes alone would fit.</summary>
    Task<AefWorkspaceEvidence> ReadAsync(IOutputStoreReader store, long maxBytes = 256L << 20, CancellationToken ct = default);
}

/// <summary>
/// Verifies every native candidate before considering legacy input. Alias matching is subject-scoped and only
/// suppresses duplicate sources; it never establishes original execution custody or recovers uncaptured content.
/// </summary>
public sealed partial class AefWorkspaceEvidenceReader : IAefWorkspaceEvidenceReader
{
    /// <inheritdoc />
    public AefWorkspaceEvidence Read(string workspaceDirectory, long maxBytes = 256L << 20, CancellationToken ct = default)
    {
        if (maxBytes is < 1 or > (1L << 30)) throw new ArgumentOutOfRangeException(nameof(maxBytes),
            "Expected: a workspace byte budget of 1..1 GiB. Actual: invalid budget. Suggestions: use the default. Because: workspace reads must remain bounded.");
        var workspace = Path.GetFullPath(workspaceDirectory);
        ct.ThrowIfCancellationRequested();
        var reader = new AefRunEvidenceReader();
        var runs = new List<AefWorkspaceRun>();
        var budget = new AefReadBudget(maxBytes);
        AefRunEvidence ReadRun(string directory)
        {
            var evidence = reader.Read(directory, budget.Limit(64L << 20), ct);
            budget.Charge(evidence.Bytes);
            return evidence;
        }

        foreach (var directory in AefEvidenceDiscovery.Discover(Path.Combine(workspace, "aef", "runs"), ct: ct))
        {
            var evidence = ReadRun(directory);
            runs.Add(new(SourceId(workspace, directory), evidence, Aliases(evidence)));
        }

        var native = runs.ToArray();
        var legacy = StoreV1Exporter.DiscoverWorkspaceRunDirectories(workspace, ct);
        if (runs.Count + legacy.Count > 1000) throw Bad("More than 1000 native and legacy source folders were found.");
        foreach (var directory in legacy)
        {
            ct.ThrowIfCancellationRequested();
            var temporary = Directory.CreateTempSubdirectory("ae-workspace-read-");
            try
            {
                // Exact-directory conversion avoids the legacy store's first-ID-match lookup.
                var conversion = StoreV1Exporter.ExportWorkspaceRunBounded(workspace, directory, Path.Combine(temporary.FullName, "run"), null, ct, budget);
                // Conversion writes were charged before writing, including the seal;
                // verification captures the same output without charging it a second time.
                var evidence = reader.Read(conversion.Directory, 64L << 20, ct);
                var linked = native.Where(n => n.Evidence.Info.SubjectRef == evidence.Info.SubjectRef
                    && n.RunAliases.Contains(evidence.Info.RunId, StringComparer.Ordinal)).ToArray();
                if (linked.Length > 1) throw Bad("Multiple native sources claim the same legacy execution.");
                if (linked.Length == 1) continue;
                runs.Add(new(SourceId(workspace, directory), evidence, []) { ConvertedOnRead = true });
            }
            finally { temporary.Delete(recursive: true); }
        }
        ct.ThrowIfCancellationRequested();
        return new(runs);
    }

    private static IReadOnlyList<string> Aliases(AefRunEvidence evidence)
    {
        if (!evidence.Header.TryGetProperty("ext", out var ext) || !ext.TryGetProperty("agenteval.store-v1", out var link)) return [];
        if (link.ValueKind != JsonValueKind.Object) throw Bad("The legacy source link is not an object.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw Bad("A legacy execution alias is not a nonempty string.");
            ids.Add(value.GetString()!);
        }
        if (link.TryGetProperty("runId", out var single)) Add(single);
        if (link.TryGetProperty("trialRunIds", out var trials))
        {
            if (trials.ValueKind != JsonValueKind.Array) throw Bad("Legacy trial aliases are not an array.");
            foreach (var trial in trials.EnumerateArray()) Add(trial);
        }
        return Array.AsReadOnly(ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // Root-relative identity remains stable after moving a workspace; no physical paths leave the catalog.
    private static string SourceId(string workspace, string directory)
    {
        var relative = Path.GetRelativePath(workspace, directory).Replace(Path.DirectorySeparatorChar, '/');
        if (OperatingSystem.IsWindows()) relative = relative.ToUpperInvariant();
        return "source-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))).ToLowerInvariant();
    }

    private static InvalidDataException Bad(string actual) => new("Expected: unambiguous bounded workspace evidence. Actual: " + actual +
        " Suggestions: narrow the source or correct its recorded identities. Because: selection must not hide invalid evidence or invent custody.");
}
