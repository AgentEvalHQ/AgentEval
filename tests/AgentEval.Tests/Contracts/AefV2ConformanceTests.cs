// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// The AEF v2 contract (contracts/aef/v2): the schemas, the conformance corpus, result ids and the seal. The corpus is
/// written by contracts/aef/tools/build_conformance.py (Python); everything here is recomputed in .NET, so the corpus
/// is checked by two implementations of the rules in v2/README.md.
/// </summary>
public class AefV2ConformanceTests
{
    private static readonly string V2 = AefSchemaSet.V2;
    private static readonly string Conformance = Path.Combine(V2, "conformance");

    private static readonly Lazy<AefSchemaSet> Writer = AefSchemaSet.Writer;
    private static readonly Lazy<AefSchemaSet> Reader = AefSchemaSet.Reader;

    // ------------------------------------------------------------------ schemas

    [Fact]
    public void EveryWriterSchema_Is2020_12_WithAnIdUnderWriter()
    {
        var files = Directory.GetFiles(Path.Combine(V2, "schemas", "writer"), "*.schema.json");

        Assert.Equal(12, files.Length);
        Assert.All(files, f =>
        {
            var node = JsonNode.Parse(File.ReadAllText(f))!;
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", (string?)node["$schema"]);
            Assert.Equal($"https://agenteval.dev/aef/v2/writer/{Path.GetFileName(f)}", (string?)node["$id"]);
        });
    }

    [Fact]
    public void ReaderSchemas_AreTheWriterSchemas_MadeTolerant()
    {
        // tools/derive_reader.py wrote them; this derives them again and compares.
        foreach (var writer in Directory.GetFiles(Path.Combine(V2, "schemas", "writer"), "*.schema.json"))
        {
            var schema = JsonNode.Parse(File.ReadAllText(writer))!.AsObject();
            var expected = Derive(schema, inCondition: false)!.AsObject();
            expected["description"] = "READER (tolerant), derived from the writer schema by tools/derive_reader.py. " + (string?)schema["description"];
            var committed = JsonNode.Parse(File.ReadAllText(Path.Combine(V2, "schemas", "reader", Path.GetFileName(writer))));

            Assert.True(JsonNode.DeepEquals(expected, committed), $"reader/{Path.GetFileName(writer)} is not derived from writer/");
        }
    }

    private static JsonNode? Derive(JsonNode? node, bool inCondition)
    {
        switch (node)
        {
            case JsonArray array:
                return new JsonArray(array.Select(item => Derive(item, inCondition)).ToArray());
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (key == "$id")
                    {
                        result[key] = ((string)value!).Replace("/aef/v2/writer/", "/aef/v2/reader/", StringComparison.Ordinal);
                    }
                    else if (inCondition)
                    {
                        result[key] = Derive(value, true);
                    }
                    else if (key == "additionalProperties" && value is JsonValue v && v.TryGetValue<bool>(out var b) && !b)
                    {
                        // unknown fields are allowed
                    }
                    else if (key == "enum")
                    {
                        result["type"] = "string";
                    }
                    else if (key == "const" && value is JsonValue c && c.TryGetValue<string>(out var s) && s == "2.0")
                    {
                        result["type"] = "string";
                        result["pattern"] = "^2\\.[0-9]+$";
                    }
                    else if (key is "if" or "not")
                    {
                        // A condition selects a rule and a prohibition forbids: relaxing either changes what it means.
                        result[key] = Derive(value, true);
                    }
                    else if (key == "oneOf" && value is JsonArray branches && branches.Count > 0 && branches.All(br => KindOf(br) is not null))
                    {
                        // A union discriminated by kind: known kinds keep their rules; an unknown kind reads as "other".
                        var anyOf = new JsonArray(branches.Select(br => Derive(br, false)).ToArray());
                        anyOf.Add(new JsonObject
                        {
                            ["type"] = "object",
                            ["required"] = new JsonArray("kind"),
                            ["properties"] = new JsonObject
                            {
                                ["kind"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["not"] = new JsonObject { ["enum"] = new JsonArray(branches.Select(br => (JsonNode?)KindOf(br)).ToArray()) },
                                },
                            },
                        });
                        result["anyOf"] = anyOf;
                    }
                    else
                    {
                        result[key] = Derive(value, false);
                    }
                }

                return result;
            default:
                return node?.DeepClone();
        }
    }

    private static string? KindOf(JsonNode? branch) =>
        branch?["properties"]?["kind"]?["const"] is JsonValue k && k.TryGetValue<string>(out var kind) ? kind : null;

    // ------------------------------------------------------------------ the corpus against the schemas

    public static TheoryData<string> ValidRuns() => new(Directory.GetDirectories(Path.Combine(Conformance, "valid")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void AValidRun_PassesTheWriterAndTheReaderSchemas_FileByFile(string name)
    {
        var run = Path.Combine(Conformance, "valid", name);

        foreach (var (schema, document, where) in Documents(run))
        {
            Assert.True(Writer.Value.IsValid(schema, document, out var writerErrors), $"{where} (writer): {writerErrors}");
            Assert.True(Reader.Value.IsValid(schema, document, out var readerErrors), $"{where} (reader): {readerErrors}");
        }
    }

    public static TheoryData<string> InvalidDocuments() => new(Directory.GetDirectories(Path.Combine(Conformance, "invalid")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void AnInvalidDocument_IsRefusedByTheWriter_AndByTheReaderWhereExpected(string name)
    {
        var dir = Path.Combine(Conformance, "invalid", name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        var schema = (string)expected["schema"]!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "document.json")));

        Assert.False(Writer.Value.IsValid(schema, document, out _), $"{name}: the writer schema accepted it ({expected["rule"]})");
        Assert.Equal((string)expected["reader"]! == "valid", Reader.Value.IsValid(schema, document, out var errors));
        _ = errors;
    }

    /// <summary>Every JSON document of a run folder and the schema it answers to.</summary>
    private static IEnumerable<(string Schema, JsonNode? Document, string Where)> Documents(string run)
    {
        IEnumerable<(string, JsonNode?, string)> Lines(string file, string schema) =>
            File.Exists(Path.Combine(run, file))
                ? File.ReadAllLines(Path.Combine(run, file), Encoding.UTF8).Select((l, i) => (schema, JsonNode.Parse(l), $"{file}:{i + 1}"))
                : [];

        yield return ("run", JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json"))), "run.json");
        foreach (var d in Lines("results.ndjson", "result")) yield return d;
        foreach (var d in Lines("evidence.ndjson", "evidence")) yield return d;
        foreach (var d in Lines("gates.ndjson", "gate-decision")) yield return d;
        foreach (var d in Lines(Path.Combine("overlays", "events.ndjson"), "overlay-event")) yield return d;
        foreach (var (file, schema) in new[] { ("summary.json", "summary"), ("metrics.json", "metrics"), ("seal.json", "seal") })
        {
            if (File.Exists(Path.Combine(run, file)))
                yield return (schema, JsonNode.Parse(File.ReadAllText(Path.Combine(run, file))), file);
        }

        if (Directory.Exists(Path.Combine(run, "overlays")))
        {
            foreach (var seal in Directory.GetFiles(Path.Combine(run, "overlays"), "seal-*.json"))
                yield return ("overlay-seal", JsonNode.Parse(File.ReadAllText(seal)), $"overlays/{Path.GetFileName(seal)}");
        }
    }

    // ------------------------------------------------------------------ result ids

    [Fact]
    public void ResultIds_MatchTheVectors()
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance, "result-ids.json")))!.AsArray();

        Assert.NotEmpty(vectors);
        foreach (var v in vectors)
        {
            Assert.Equal((string)v!["resultId"]!, ResultId((string)v["runId"]!, (string)v["caseId"]!, (string)v["path"]!, (int?)v["trial"]));
        }
    }

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void EveryResultLine_CarriesItsDeterministicId(string name)
    {
        var run = Path.Combine(Conformance, "valid", name);
        var runId = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json")))!["runId"]!;

        foreach (var line in File.ReadAllLines(Path.Combine(run, "results.ndjson"), Encoding.UTF8))
        {
            var r = JsonNode.Parse(line)!;
            Assert.Equal(ResultId(runId, (string)r["caseId"]!, (string)r["path"]!, (int?)r["trial"]), (string)r["resultId"]!);
        }
    }

    private static string ResultId(string runId, string caseId, string path, int? trial) =>
        "r_" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', runId, caseId, path, trial?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""))))[..32];

    // ------------------------------------------------------------------ the seal

    [Theory]
    [InlineData("completed-eval")]
    [InlineData("aborted-early")]
    public void ASealedRun_HasTheManifest_AndTheRunHash_TheVectorsGive(string name)
    {
        var run = Path.Combine(Conformance, "valid", name);
        var manifest = Manifest(run);
        var seal = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "seal.json")))!;

        Assert.Equal(File.ReadAllText(Path.Combine(Conformance, "seal-vectors", name, "expected-manifest.txt"), Encoding.UTF8), manifest);
        Assert.Equal(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), (string)seal["predicate"]!["runHash"]!);
        Assert.Empty(Verify(run));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("added-file")]
    [InlineData("missing-file")]
    public void AChangedRun_FailsVerification_NamingWhatChanged(string name)
    {
        var dir = Path.Combine(Conformance, "seal-vectors", name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!["mismatches"]!.AsArray()
            .Select(m => ((string)m!["path"]!, (string)m["problem"]!));

        Assert.Equal(expected, Verify(Path.Combine(dir, "run")));
    }

    [Fact]
    public void TheCorpusKeepsBytesThatAReEncoderWouldChange()
    {
        // Non-ASCII text and a CRLF inside a sealed blob: a seal over bytes must not depend on any re-encoding.
        var blob = Directory.GetFiles(Path.Combine(Conformance, "valid", "completed-eval", "blobs"), "*", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(blob);

        Assert.Contains((byte)'\r', bytes);
        Assert.Contains(bytes, b => b >= 0x80);
        Assert.Equal(Path.GetFileName(blob), Hex(SHA256.HashData(bytes)));
    }

    [Fact]
    public void OverlayBatches_FormAChainOverTheWholeEventsFile()
    {
        var overlays = Path.Combine(Conformance, "valid", "completed-eval", "overlays");
        var events = File.ReadAllBytes(Path.Combine(overlays, "events.ndjson"));
        var seals = Directory.GetFiles(overlays, "seal-*.json").Order(StringComparer.Ordinal).ToList();

        long offset = 0;
        string? previous = null;
        for (var i = 0; i < seals.Count; i++)
        {
            var p = JsonNode.Parse(File.ReadAllText(seals[i]))!;
            var predicate = p["predicate"]!;
            var length = (int)predicate["length"]!;
            Assert.Equal(i + 1, (int)predicate["batch"]!);
            Assert.Equal(offset, (long)predicate["offset"]!);
            Assert.Equal(Hex(SHA256.HashData(events.AsSpan((int)offset, length))), (string)p["subject"]![0]!["digest"]!["sha256"]!);
            Assert.Equal(previous, (string?)predicate["previous"]?["sha256"]);
            previous = Hex(SHA256.HashData(File.ReadAllBytes(seals[i])));
            offset += length;
        }

        Assert.Equal(events.Length, offset);
    }

    /// <summary>The manifest text over a run's sealed files (v2/README.md, 'Sealing').</summary>
    private static string Manifest(string run)
    {
        var sb = new StringBuilder();
        foreach (var rel in SealedFiles(run))
        {
            var bytes = File.ReadAllBytes(Path.Combine(run, rel));
            sb.Append(Hex(SHA256.HashData(bytes))).Append("  ").Append(bytes.Length).Append("  ").Append(rel).Append('\n');
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SealedFiles(string run) =>
        Directory.GetFiles(run, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(run, f).Replace('\\', '/'))
            .Where(rel => rel is not ("seal.json" or "attestation.dsse.json") && !rel.StartsWith("overlays/", StringComparison.Ordinal))
            .OrderBy(rel => rel, StringComparer.Ordinal);

    /// <summary>Recomputes a run's seal: (path, digest | not-sealed | missing) for every difference, ordered by path.</summary>
    private static List<(string Path, string Problem)> Verify(string run)
    {
        var seal = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "seal.json")))!;
        var sealedDigests = seal["subject"]!.AsArray().ToDictionary(s => (string)s!["name"]!, s => (string)s!["digest"]!["sha256"]!, StringComparer.Ordinal);
        var present = SealedFiles(run).ToHashSet(StringComparer.Ordinal);
        var problems = new List<(string, string)>();
        foreach (var rel in present.Union(sealedDigests.Keys).Order(StringComparer.Ordinal))
        {
            if (!sealedDigests.TryGetValue(rel, out var digest)) problems.Add((rel, "not-sealed"));
            else if (!present.Contains(rel)) problems.Add((rel, "missing"));
            else if (digest != Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(run, rel))))) problems.Add((rel, "digest"));
        }

        return problems;
    }

    [Fact]
    public void TheRuntimeVerdictProfile_IsTheAevpSchemaTheLibraryShips()
    {
        var root = AefSchemaSet.RepoRoot();

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(root, "src", "AgentEval.MAF.AgentHooks", "Aevp", "aevp-0.1.schema.json")),
            File.ReadAllBytes(Path.Combine(root, "contracts", "aef", "profiles", "runtime-verdict", "aevp-0.1.schema.json")));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Hex(ReadOnlySpan<byte> bytes) => Hex(bytes.ToArray());
}
