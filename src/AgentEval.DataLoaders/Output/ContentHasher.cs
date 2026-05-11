// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentEval.Output;

internal static class ContentHasher
{
    /// <summary>
    /// Computes a SHA-256 hash over the canonical run files for deterministic content
    /// verification. The hash domain covers:
    /// <list type="number">
    ///   <item>The run's <c>manifest.json</c> serialised canonically with the
    ///         <c>contentHash</c> field zeroed (so the hash binds operator / host /
    ///         git provenance to the run, not just the raw scenario bytes).</item>
    ///   <item>The run's <c>summary.json</c> raw bytes.</item>
    ///   <item>Every <c>scenarios/*.json</c> file, sorted by filename for determinism.</item>
    ///   <item>Every <c>traces/*.json</c> file, sorted by filename for determinism
    ///         (previously only <c>agent-trace.json</c>, which silently excluded
    ///         per-test trace artefacts written by <c>TraceArtifactManager</c>).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Backwards compatibility</b> — workspaces written by pre-pass-3 code did not
    /// include the manifest in their <c>contentHash</c>. After this change, those
    /// workspaces will fail <see cref="VerifyAsync"/>. Per the v0.8.1-beta release
    /// notes, no migration tooling ships in this version: re-run <c>agenteval bench …</c>
    /// to regenerate. This is acceptable because the pre-pass-3 chain is a known
    /// audit-coverage gap that v0.8.1-beta closes.
    /// </para>
    /// </remarks>
    public static async Task<string> HashRunAsync(
        FileSystemLayout layout,
        SubjectIdentity subject,
        string runId,
        CancellationToken ct)
        => await HashRunAsync(layout, subject, runId, manifestOverride: null, ct);

    /// <summary>
    /// Like <see cref="HashRunAsync(FileSystemLayout, SubjectIdentity, string, CancellationToken)"/>
    /// but accepts an in-memory manifest to hash AGAINST instead of reading from disk.
    /// Used by <c>CompleteRunAsync</c> to hash the final manifest (with all run-completion
    /// fields populated) BEFORE writing it to disk — the on-disk manifest is the
    /// PENDING placeholder at the moment the hash is computed, so reading from disk
    /// would produce a hash that no later <see cref="VerifyAsync"/> can reproduce.
    /// </summary>
    public static async Task<string> HashRunAsync(
        FileSystemLayout layout,
        SubjectIdentity subject,
        string runId,
        RunManifest? manifestOverride,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // 1. Canonical manifest — binds provenance fields (operator, git, env)
        //    to the run. Strip the contentHash field for the hash domain (so the
        //    hash is computed over what's UNDER the hash itself, not including it).
        var manifestFile = layout.ManifestFile(subject, runId);
        RunManifest? manifest = manifestOverride;
        if (manifest is null && File.Exists(manifestFile))
        {
            await using var stream = File.OpenRead(manifestFile);
            manifest = await JsonSerializer.DeserializeAsync<RunManifest>(stream, s_readOpts, ct);
        }
        if (manifest is not null)
        {
            var canonical = manifest with { ContentHash = "" };
            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(canonical, s_canonicalOpts);
            hash.AppendData(canonicalBytes);
        }

        // 2. Summary
        var summaryFile = layout.SummaryFile(subject, runId);
        if (File.Exists(summaryFile))
        {
            var bytes = await File.ReadAllBytesAsync(summaryFile, ct);
            hash.AppendData(bytes);
        }

        // 3. Scenarios (sorted by filename for determinism)
        var scenariosDir = layout.ScenariosDir(subject, runId);
        if (Directory.Exists(scenariosDir))
        {
            var scenarioFiles = Directory.GetFiles(scenariosDir, "*.json")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray();

            foreach (var file in scenarioFiles)
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                hash.AppendData(bytes);
            }
        }

        // 4. Every JSON file under traces/ — previously only agent-trace.json
        //    was hashed, so per-test artefacts written by TraceArtifactManager
        //    sat outside the audit chain. Now they're covered.
        var tracesDir = layout.TracesDir(subject, runId);
        if (Directory.Exists(tracesDir))
        {
            var traceFiles = Directory.GetFiles(tracesDir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray();

            foreach (var file in traceFiles)
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                hash.AppendData(bytes);
            }
        }

        var finalHash = hash.GetHashAndReset();
        return Convert.ToHexString(finalHash).ToLowerInvariant();
    }

    /// <summary>Returns true when the hash of the canonical run files matches <paramref name="expectedHash"/>.</summary>
    public static async Task<bool> VerifyAsync(
        FileSystemLayout layout,
        SubjectIdentity subject,
        string runId,
        string expectedHash,
        CancellationToken ct)
    {
        var actual = await HashRunAsync(layout, subject, runId, ct);
        return $"sha256:{actual}" == expectedHash;
    }

    private static readonly JsonSerializerOptions s_readOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Hand-written canonical serializer for manifest hashing. Pinning property
    /// order alphabetically (rather than relying on declared record order, which
    /// is stable today but not guaranteed to remain so across .NET versions)
    /// means a new manifest field forces an intentional update to this converter
    /// — protecting the hash format from accidental drift.
    /// </summary>
    private static readonly JsonSerializerOptions s_canonicalOpts = new()
    {
        WriteIndented = false,
        Converters =
        {
            new CanonicalRunManifestConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private sealed class CanonicalRunManifestConverter : System.Text.Json.Serialization.JsonConverter<RunManifest>
    {
        public override RunManifest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("CanonicalRunManifestConverter is write-only (hash-domain serialisation).");

        public override void Write(Utf8JsonWriter w, RunManifest m, JsonSerializerOptions options)
        {
            // Keys in alphabetical order — stable across releases. Adding a new
            // manifest field requires editing this method (intentional hash change).
            w.WriteStartObject();

            // agentEval
            w.WritePropertyName("agentEval");
            w.WriteStartObject();
            w.WriteString("configurationId", m.AgentEval.ConfigurationId);
            w.WriteString("version", m.AgentEval.Version);
            w.WriteEndObject();

            // contentHash — zeroed for the canonical domain (the whole point).
            w.WriteString("contentHash", "");

            // environment
            w.WritePropertyName("environment");
            w.WriteStartObject();
            w.WriteBoolean("ci", m.Environment.Ci);
            w.WriteString("ciSystem", m.Environment.CiSystem);
            w.WriteString("dotnetVersion", m.Environment.DotnetVersion);
            w.WriteString("machine", m.Environment.Machine);
            w.WriteString("os", m.Environment.Os);
            w.WriteString("runner", m.Environment.Runner);
            w.WriteEndObject();

            // git
            w.WritePropertyName("git");
            w.WriteStartObject();
            w.WriteString("branch", m.Git.Branch);
            w.WriteString("commit", m.Git.Commit);
            w.WriteBoolean("dirty", m.Git.Dirty);
            w.WriteString("tag", m.Git.Tag);
            w.WriteEndObject();

            // run
            w.WritePropertyName("run");
            w.WriteStartObject();
            w.WriteString("duration", m.Run.Duration.ToString());
            w.WriteString("evalProject", m.Run.EvalProject);
            w.WriteString("evalProjectPath", m.Run.EvalProjectPath);
            w.WriteString("harness", m.Run.Harness);
            w.WriteString("kind", m.Run.Kind);
            w.WriteString("parentInvocationId", m.Run.ParentInvocationId);
            w.WriteString("runId", m.Run.RunId);
            w.WriteNumber("scenarioCount", m.Run.ScenarioCount);
            if (m.Run.Seed.HasValue) w.WriteNumber("seed", m.Run.Seed.Value);
            else w.WriteNull("seed");
            w.WriteString("timestamp", m.Run.Timestamp.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("verdict", m.Run.Verdict);
            w.WriteEndObject();

            // schemaVersion
            w.WriteString("schemaVersion", m.SchemaVersion);

            // solution
            w.WritePropertyName("solution");
            w.WriteStartObject();
            w.WriteString("id", m.Solution.Id.ToString());
            w.WriteString("name", m.Solution.Name);
            w.WriteString("workspaceTag", m.Solution.WorkspaceTag);
            w.WriteEndObject();

            // subject
            w.WritePropertyName("subject");
            w.WriteStartObject();
            w.WriteString("framework", m.Subject.Framework);
            w.WriteString("kind", m.Subject.Kind.ToString());
            w.WriteString("modelId", m.Subject.ModelId);
            w.WriteString("name", m.Subject.Name);
            w.WriteString("sourcePath", m.Subject.SourcePath);
            w.WriteString("sourceProject", m.Subject.SourceProject);
            w.WriteString("version", m.Subject.Version);
            w.WriteEndObject();

            w.WriteEndObject();
        }
    }
}
