// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Runner;

/// <summary>
/// A run folder as [STRM-4] (contracts/aef/1/spec/06-runners.md) finds it: by its run.json ([RUN-1], spec 03), with
/// its run hash (spec 04, [SEAL-4]). Results and summary are read when asked.
/// </summary>
public sealed class RunFolder
{
    /// <summary>Strings by their UTF-8 bytes, the order AEF sorts paths in (spec 03, §3.9).</summary>
    internal static readonly Comparer<string> Utf8Order = Comparer<string>.Create((a, b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));

    private string? _runHash;

    private RunFolder(string path, JsonNode run)
    {
        Path = path;
        Run = run;
    }

    /// <summary>The folder.</summary>
    public string Path { get; }

    /// <summary>The run's run.json.</summary>
    public JsonNode Run { get; }

    /// <summary>run.json's runId.</summary>
    public string RunId => (string)Run["runId"]!;

    /// <summary>
    /// The run's run hash ([SEAL-4]): its seal.json's predicate.runHash, or, for a run without a seal, the hash
    /// recomputed from its files. The seal is read, not verified: a run verifier checks it against the reader seal
    /// schema and the files (§4.1), and a run whose seal fails that is not intact, whichever hash is taken here.
    /// </summary>
    public string RunHash => _runHash ??= SealedRunHash() ?? ComputeRunHash(Path);

    /// <summary>
    /// Every run under <paramref name="root"/>, found by its run.json, in path order. A folder that holds a run holds
    /// no other; a run.json that is not a JSON object with a string runId names no run.
    /// </summary>
    public static IReadOnlyList<RunFolder> Find(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<RunFolder>();
        Walk(root, found);
        return [.. found.OrderBy(f => f.Path.Replace('\\', '/'), Utf8Order)];
    }

    /// <summary>[SEAL-1]: every file of the run except seal.json, attestation.dsse.json and everything under overlays/, by the UTF-8 bytes of its path.</summary>
    public static IReadOnlyList<string> SealedFiles(string folder) =>
        [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(rel => rel is not ("seal.json" or "attestation.dsse.json") && !rel.StartsWith("overlays/", StringComparison.Ordinal))
            .Order(Utf8Order)];

    /// <summary>[SEAL-3], [SEAL-4]: the SHA-256 (lower-case hex) of the manifest, one "&lt;sha256&gt;  &lt;size&gt;  &lt;path&gt;\n" line per sealed file.</summary>
    public static string ComputeRunHash(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var manifest = new StringBuilder();
        foreach (var rel in SealedFiles(folder))
        {
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(folder, rel));
            manifest.Append(Hex(SHA256.HashData(bytes))).Append("  ").Append(bytes.Length).Append("  ").Append(rel).Append('\n');
        }

        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString())));
    }

    /// <summary>The lines of results.ndjson (none when it is absent).</summary>
    public IReadOnlyList<JsonNode> Results()
    {
        var file = System.IO.Path.Combine(Path, "results.ndjson");
        return File.Exists(file)
            ? [.. Encoding.UTF8.GetString(File.ReadAllBytes(file)).Split('\n').Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)]
            : [];
    }

    /// <summary>summary.json, or null when it is absent.</summary>
    public JsonNode? Summary()
    {
        var file = System.IO.Path.Combine(Path, "summary.json");
        return File.Exists(file) ? JsonNode.Parse(File.ReadAllBytes(file)) : null;
    }

    private static void Walk(string dir, List<RunFolder> found)
    {
        var header = System.IO.Path.Combine(dir, "run.json");
        if (File.Exists(header))
        {
            if (Read(header) is { } run) found.Add(new RunFolder(dir, run));
            return;
        }

        foreach (var sub in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            Walk(sub, found);
    }

    private string? SealedRunHash()
    {
        var file = System.IO.Path.Combine(Path, "seal.json");
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(file)) is JsonObject seal && seal["predicate"] is JsonObject predicate
                   && predicate["runHash"] is JsonValue hash && hash.TryGetValue<string>(out var text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Read(string header)
    {
        try
        {
            var run = JsonNode.Parse(File.ReadAllBytes(header));
            return run is JsonObject && run["runId"] is JsonValue id && id.TryGetValue<string>(out _) ? run : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
