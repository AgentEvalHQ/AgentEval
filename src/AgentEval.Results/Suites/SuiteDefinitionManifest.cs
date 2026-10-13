// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Suites;

/// <summary>A validated external suite definition, distinct from any run's observed case roster.</summary>
public sealed class SuiteDefinitionManifest
{
    /// <summary>Relative producer-extension path passed to AefRunWriter.PutExtFile; included in the run seal.</summary>
    public const string ExtensionFile = "agenteval/suite-definition.json";
    private readonly byte[] _bytes;

    internal SuiteDefinitionManifest(JsonObject normalized, byte[] bytes)
    {
        _bytes = bytes;
        Kind = (string)normalized["kind"]!;
        SuiteRef = (string)normalized["suiteRef"]!;
        DefinitionVersion = (string)normalized["definitionVersion"]!;
        Frozen = (bool)normalized["frozen"]!;
        CaseIds = Array.AsReadOnly(normalized["caseIds"]!.AsArray().Select(n => (string)n!).ToArray());
        Digest = Hash(bytes);
    }

    /// <summary>Versioned manifest contract, independent of the AEF protocol version.</summary>
    public string SchemaVersion => "1.0";
    /// <summary>Producer kind: dataset, probe or composite.</summary>
    public string Kind { get; }
    /// <summary>Suite reference the definition describes.</summary>
    public string SuiteRef { get; }
    /// <summary>Declared release/definition version, distinct from producer release and content digest.</summary>
    public string DefinitionVersion { get; }
    /// <summary>Whether the declared inputs and complete case roster describe frozen suite content.</summary>
    public bool Frozen { get; }
    /// <summary>Planned case IDs in ordinal order; independent of completion order or interruption.</summary>
    public IReadOnlyList<string> CaseIds { get; }
    /// <summary>SHA-256 URI of the normalized full definition, including its schema version.</summary>
    public string Digest { get; }
    /// <summary>AEF suite content digest only when content is frozen; a dynamic recipe is not frozen content.</summary>
    public string? SuiteContentDigest => Frozen ? Digest : null;
    /// <summary>A detached JSON representation; mutating it cannot alter this definition's identity.</summary>
    public JsonObject ToJson() => AefJsonReader.ParseDocument(_bytes);
    /// <summary>A detached copy of the canonical manifest bytes, suitable for retaining as producer metadata.</summary>
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    /// <summary>Compact provenance pointing at the sealed manifest, without duplicating its potentially large roster.</summary>
    public JsonObject Metadata() => new()
    {
        ["scope"] = "definition", ["schemaVersion"] = SchemaVersion, ["digest"] = Digest,
        ["path"] = "ext/" + ExtensionFile, ["frozen"] = Frozen, ["plannedCases"] = CaseIds.Count,
    };

    /// <summary>Exact byte identity for an input artifact. No newline or Unicode normalization is implicit.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Validates a declared suite manifest and computes deterministic, outcome-independent content identity.</summary>
public interface ISuiteDefinitionReader
{
    /// <summary>Reads strict JSON, canonicalizes declared inputs and hashes the resulting versioned definition.</summary>
    SuiteDefinitionManifest Read(ReadOnlyMemory<byte> json);
}

/// <summary>Suite definition validation and canonical hashing without dependencies on producers, results or filesystem state.</summary>
public sealed class SuiteDefinitionReader : ISuiteDefinitionReader
{
    /// <summary>Maximum manifest size, including its normalized representation (4 MiB).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;
    private static readonly Lazy<AefSchemaValidator> Schema = new(() =>
    {
        using var stream = typeof(SuiteDefinitionReader).Assembly.GetManifestResourceStream("AgentEval.Results.Suites.definition.schema.json")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return AefSchemaValidator.Load([new KeyValuePair<string, byte[]>("definition.schema.json", memory.ToArray())]);
    });

    /// <inheritdoc />
    public SuiteDefinitionManifest Read(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaxBytes) throw Bad("source exceeds 4 MiB");
        JsonObject root;
        try { root = AefJsonReader.ParseDocument(json.Span); }
        catch (AefReadException e) { throw Bad(e.Message); }
        if (Schema.Value.Validate("definition", root) is { } why) throw Bad(why);
        if (root["inputs"]!.AsObject().Count > 10000 || root["definition"]!.AsObject().Count == 0)
            throw Bad("at most 10,000 named inputs and a nonempty definition are required");
        foreach (var value in new[] { (string)root["suiteRef"]!, (string)root["definitionVersion"]! }
            .Concat(root["inputs"]!.AsObject().Select(p => p.Key)).Concat(root["caseIds"]!.AsArray().Select(n => (string)n!)))
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
                throw Bad("blank, padded or control-bearing identifier");
        if ((bool)root["frozen"]! && root["caseIds"]!.AsArray().Count == 0)
            throw Bad("frozen definition has no planned cases");
        // Only the roster is a set. Definition arrays (criteria, pipeline steps, ordered prompts) retain order.
        root["caseIds"] = new JsonArray(root["caseIds"]!.AsArray().Select(n => (string)n!)
            .OrderBy(id => id, StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        var normalized = (JsonObject)Sort(root)!;
        // Reuse the existing cross-framework AEF JSON number/string rules; this is manifest-v1 canonicalization,
        // not a claim to implement a different standard such as JCS. Object keys use ordinal UTF-16 order.
        var bytes = AefJsonWriter.Document(normalized, MaxBytes);
        return new(normalized, bytes);
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        _ => node?.DeepClone(),
    };

    private static FormatException Bad(string actual) => new(
        $"Expected: a suite definition manifest 1.0. Actual: {actual}. Suggestions: declare frozen inputs, planned cases and evaluation configuration explicitly. Because: observed results cannot establish full suite identity.");
}
