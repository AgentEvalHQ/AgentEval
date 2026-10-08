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

        Assert.Equal(15, files.Length);
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
                        // Open: an unknown value reads as "other". A nullable enum stays nullable.
                        result["type"] = obj["type"] is JsonArray types ? types.DeepClone() : "string";
                    }
                    else if (key == "const" && value is JsonValue c && c.TryGetValue<string>(out var s) && s == "2.0")
                    {
                        result["type"] = "string";
                        result["pattern"] = "^2\\.[0-9]+(?!\\n)$";
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

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void AValidRun_HasItsRequiredFiles_AndItsReferencesResolve(string name)
    {
        var run = Path.Combine(Conformance, "valid", name);
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json")))!;
        var closed = (string)header["status"]! != "running";

        foreach (var required in closed
                     ? new[] { "run.json", "results.ndjson", "metrics.json", "summary.json", "seal.json" }
                     : ["run.json", "results.ndjson", "metrics.json"])
        {
            Assert.True(File.Exists(Path.Combine(run, required)), $"{name}: {required} is required");
        }

        // The cross-file rules a reader checks (v2/README.md, 'Rules across files').
        var results = NdjsonLines(Path.Combine(run, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        var ids = results.Select(r => (string)r["resultId"]!).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        var evidence = File.Exists(Path.Combine(run, "evidence.ndjson"))
            ? NdjsonLines(Path.Combine(run, "evidence.ndjson")).Select(l => JsonNode.Parse(l)!).ToList()
            : [];
        var evidenceIds = evidence.Select(e => (string)e["evidenceId"]!).ToHashSet(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if ((string?)r["parentResultId"] is { } parent) Assert.Contains(parent, ids);
            foreach (var cited in r["evidence"]?.AsArray() ?? []) Assert.Contains((string)cited!, evidenceIds);
            foreach (var decisive in r["aggregation"]?["decisive"]?.AsArray() ?? []) Assert.Contains((string)decisive!, ids);
            if (r["reasoning"] is { } reasoning)
            {
                var blob = Blob(run, (string)reasoning["blob"]!);
                Assert.Equal((long)reasoning["bytes"]!, new FileInfo(blob).Length);
            }
        }

        if (File.Exists(Path.Combine(run, "gates.ndjson")))
        {
            foreach (var gate in NdjsonLines(Path.Combine(run, "gates.ndjson")).Select(l => JsonNode.Parse(l)!))
            {
                foreach (var named in (gate["inputs"]?["results"]?.AsArray() ?? []).Concat(gate["decisive"]?.AsArray() ?? []))
                    Assert.Contains((string)named!, ids);
            }
        }

        foreach (var e in evidence.Where(e => e["link"]?["blob"] is not null))
        {
            Assert.True(File.Exists(Blob(run, (string)e["link"]!["blob"]!)), $"{name}: evidence {e["evidenceId"]} blob");
        }
    }

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void NdjsonFiles_AreLfOnly_WithoutBom_AndEndInANewline(string name)
    {
        // NdjsonLines asserts the rules; a U+2028 inside a string is content, not a line break.
        var run = Path.Combine(Conformance, "valid", name);
        foreach (var file in Directory.GetFiles(run, "*.ndjson", SearchOption.AllDirectories))
        {
            foreach (var line in NdjsonLines(file))
            {
                Assert.NotNull(JsonNode.Parse(line));
            }
        }
    }

    [Fact]
    public void TheCorpus_HoldsEveryResultState_AndALineSeparatorInsideAString()
    {
        var lines = Directory.GetFiles(Path.Combine(Conformance, "valid"), "results.ndjson", SearchOption.AllDirectories)
            .SelectMany(NdjsonLines).ToList();

        Assert.Equal(
            ["error", "failed", "inconclusive", "not_applicable", "not_measured", "passed", "pending", "skipped", "warn"],
            lines.Select(l => (string)JsonNode.Parse(l)!["state"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(lines, l => l.Contains('\u2028'));
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
        Assert.Equal((string)expected["reader"]! == "valid", Reader.Value.IsValid(schema, document, out _));
    }

    /// <summary>Every JSON document of a run folder and the schema it answers to.</summary>
    private static IEnumerable<(string Schema, JsonNode? Document, string Where)> Documents(string run)
    {
        IEnumerable<(string, JsonNode?, string)> Lines(string file, string schema) =>
            File.Exists(Path.Combine(run, file))
                ? NdjsonLines(Path.Combine(run, file)).Select((l, i) => (schema, JsonNode.Parse(l), $"{file}:{i + 1}"))
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

    /// <summary>An NDJSON file's lines, split on LF only, asserting the format's rules.</summary>
    private static List<string> NdjsonLines(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length == 0)
            return [];

        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], $"{file}: a byte-order mark");
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        var lines = Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1).Split('\n');
        Assert.DoesNotContain(lines, l => l.Length == 0);
        return [.. lines];
    }

    private static string Blob(string run, string uri)
    {
        var hex = uri["sha256:".Length..];
        return Path.Combine(run, "blobs", "sha256", hex[..2], hex);
    }

    // ------------------------------------------------------------------ result ids

    [Fact]
    public void ResultIds_MatchTheVectors()
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance, "result-ids.json")))!.AsArray();

        Assert.Contains(vectors, v => v!["trial"] is JsonValue t && t.ToJsonString().Contains('.'));   // a trial written as 3.0
        foreach (var v in vectors)
        {
            Assert.Equal((string)v!["resultId"]!, ResultId((string)v["runId"]!, (string)v["caseId"]!, (string)v["path"]!, v["trial"]));
        }
    }

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void EveryResultLine_CarriesItsDeterministicId(string name)
    {
        var run = Path.Combine(Conformance, "valid", name);
        var runId = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json")))!["runId"]!;

        foreach (var line in NdjsonLines(Path.Combine(run, "results.ndjson")))
        {
            var r = JsonNode.Parse(line)!;
            Assert.Equal(ResultId(runId, (string)r["caseId"]!, (string)r["path"]!, r["trial"]), (string)r["resultId"]!);
        }
    }

    /// <summary>The trial's plain integer digits: a reader normalises 3.0 to "3".</summary>
    private static string ResultId(string runId, string caseId, string path, JsonNode? trial)
    {
        var digits = trial is null ? "" : decimal.Parse(trial.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) is var d && d == decimal.Truncate(d)
            ? decimal.Truncate(d).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new FormatException($"trial {trial} is not an integer");
        return "r_" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', runId, caseId, path, digits))))[..32];
    }

    // ------------------------------------------------------------------ the seal

    public static TheoryData<string> SealVectors() =>
        new(Directory.GetDirectories(Path.Combine(Conformance, "seal-vectors")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(SealVectors))]
    public void ASealVector_VerifiesWithExactlyTheExpectedDifferences(string name)
    {
        var dir = Path.Combine(Conformance, "seal-vectors", name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        var run = Path.Combine(Conformance, ((string)expected["run"]!).Replace('/', Path.DirectorySeparatorChar));
        var mismatches = expected["mismatches"]!.AsArray().Select(m => ((string)m!["path"]!, (string)m["problem"]!)).ToList();

        Assert.Equal(mismatches, Verify(run));
        Assert.Equal((string)expected["verdict"]! == "match", mismatches.Count == 0);
        if (File.Exists(Path.Combine(dir, "expected-manifest.txt")))
        {
            var manifest = Manifest(run);
            Assert.Equal(File.ReadAllText(Path.Combine(dir, "expected-manifest.txt"), Encoding.UTF8), manifest);
            Assert.Equal(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))),
                (string)JsonNode.Parse(File.ReadAllText(Path.Combine(run, "seal.json")))!["predicate"]!["runHash"]!);
        }
    }

    [Fact]
    public void TheSealVectors_CoverEveryKindOfDifference()
    {
        var problems = Directory.GetFiles(Path.Combine(Conformance, "seal-vectors"), "expected.json", SearchOption.AllDirectories)
            .SelectMany(f => JsonNode.Parse(File.ReadAllText(f))!["mismatches"]!.AsArray().Select(m => (string)m!["problem"]!))
            .Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(["digest", "duplicate-subject", "missing", "not-sealed", "run-hash", "run-id", "run-open"], problems);
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

    /// <summary>The manifest over a run's sealed files, ordered by the UTF-8 bytes of their paths (v2/README.md, 'Sealing').</summary>
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

    private static readonly Comparer<string> Utf8Order = Comparer<string>.Create((a, b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)));

    private static List<string> SealedFiles(string run) =>
        Directory.GetFiles(run, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(run, f).Replace('\\', '/'))
            .Where(rel => rel is not ("seal.json" or "attestation.dsse.json") && !rel.StartsWith("overlays/", StringComparison.Ordinal))
            .Order(Utf8Order)
            .ToList();

    /// <summary>
    /// Verifies a run's seal (v2/README.md, 'Sealing', step 6): every difference as (path, problem), ordered by path then
    /// problem. digest / not-sealed / missing compare the files with the subjects; run-hash is reported when the files
    /// match but the run hash does not; run-id, duplicate-subject and run-open are the statement's own errors.
    /// </summary>
    private static List<(string Path, string Problem)> Verify(string run)
    {
        var seal = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "seal.json")))!;
        Assert.True(Reader.Value.IsValid("seal", seal, out var errors), $"seal.json: {errors}");
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json")))!;
        var problems = new List<(string, string)>();

        var names = seal["subject"]!.AsArray().Select(s => (string)s!["name"]!).ToList();
        foreach (var duplicate in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add((duplicate.Key, "duplicate-subject"));
        var sealedDigests = seal["subject"]!.AsArray()
            .GroupBy(s => (string)s!["name"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (string)g.First()!["digest"]!["sha256"]!, StringComparer.Ordinal);

        var present = SealedFiles(run).ToHashSet(StringComparer.Ordinal);
        var fileProblems = 0;
        foreach (var rel in present.Union(sealedDigests.Keys))
        {
            string? problem = !sealedDigests.TryGetValue(rel, out var digest) ? "not-sealed"
                : !present.Contains(rel) ? "missing"
                : digest != Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(run, rel)))) ? "digest"
                : null;
            if (problem is not null)
            {
                problems.Add((rel, problem));
                fileProblems++;
            }
        }

        if (fileProblems == 0 && Hex(SHA256.HashData(Encoding.UTF8.GetBytes(Manifest(run)))) != (string)seal["predicate"]!["runHash"]!)
            problems.Add(("seal.json", "run-hash"));
        if ((string)seal["predicate"]!["runId"]! != (string)header["runId"]!)
            problems.Add(("seal.json", "run-id"));
        if ((string)header["status"]! == "running")
            problems.Add(("run.json", "run-open"));

        return [.. problems.OrderBy(p => p.Item1, Utf8Order).ThenBy(p => p.Item2, StringComparer.Ordinal)];
    }

    // ------------------------------------------------------------------ the overlay chain

    [Fact]
    public void AValidRunsOverlayBatches_FormAnUnbrokenChain() =>
        Assert.Empty(VerifyChain(Path.Combine(Conformance, "valid", "completed-eval")));

    public static TheoryData<string> ChainVectors() =>
        new(Directory.GetDirectories(Path.Combine(Conformance, "chain-vectors")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(ChainVectors))]
    public void ABrokenChain_IsReported_WithExactlyTheExpectedProblems(string name)
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance, "chain-vectors", name, "expected.json")))!;
        var run = Path.Combine(Conformance, ((string)expected["run"]!).Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected["problems"]!.AsArray().Select(p => ((string)p!["path"]!, (string)p["problem"]!)), VerifyChain(run));
    }

    /// <summary>
    /// Verifies the overlay batches (v2/README.md, 'Overlays'): every seal from 1 to the last present, each batch's
    /// number, run id, offset, digest, line boundary and previous seal, and that the batches cover the events file.
    /// </summary>
    private static List<(string Path, string Problem)> VerifyChain(string run)
    {
        var overlays = Path.Combine(run, "overlays");
        var runId = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(run, "run.json")))!["runId"]!;
        var events = File.ReadAllBytes(Path.Combine(overlays, "events.ndjson"));
        var last = Directory.GetFiles(overlays, "seal-*.json").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)[5..], System.Globalization.CultureInfo.InvariantCulture)).Max();
        var problems = new List<(string, string)>();
        var covered = new List<(long From, long To)>();
        long expectedOffset = 0;
        for (var n = 1; n <= last; n++)
        {
            var name = $"overlays/seal-{n:D4}.json";
            var file = Path.Combine(overlays, $"seal-{n:D4}.json");
            if (!File.Exists(file))
            {
                problems.Add((name, "missing"));
                expectedOffset = -1;   // unknown until the next batch says where it starts
                continue;
            }

            var statement = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.True(Reader.Value.IsValid("overlay-seal", statement, out var errors), $"{name}: {errors}");
            var p = statement["predicate"]!;
            long offset = (long)p["offset"]!, length = (long)p["length"]!;
            if ((int)p["batch"]! != n) problems.Add((name, "batch-number"));
            if ((string)p["runId"]! != runId) problems.Add((name, "run-id"));
            if (expectedOffset >= 0 && offset != expectedOffset) problems.Add((name, "offset"));
            // Coverage is what the batches claim; whether a claimed range still holds its bytes is batch-digest.
            if (offset + length > events.Length || events[(int)(offset + length - 1)] != (byte)'\n' || (offset > 0 && events[(int)offset - 1] != (byte)'\n'))
            {
                problems.Add((name, "line-boundary"));
            }
            else
            {
                covered.Add((offset, offset + length));
                if (Hex(SHA256.HashData(events.AsSpan((int)offset, (int)length).ToArray())) != (string)statement["subject"]![0]!["digest"]!["sha256"]!)
                    problems.Add((name, "batch-digest"));
            }

            var previous = p["previous"];
            var previousFile = Path.Combine(overlays, $"seal-{n - 1:D4}.json");
            var previousOk = n == 1
                ? previous is null
                : previous is not null && (string)previous["path"]! == $"overlays/seal-{n - 1:D4}.json" && File.Exists(previousFile)
                  && (string)previous["sha256"]! == Hex(SHA256.HashData(File.ReadAllBytes(previousFile)));
            if (!previousOk) problems.Add((name, "previous"));
            expectedOffset = offset + length;
        }

        long reach = 0;
        foreach (var (from, to) in covered.OrderBy(c => c.From))
        {
            if (from != reach) break;
            reach = to;
        }

        if (reach != events.Length)
            problems.Add(("overlays/events.ndjson", "uncovered"));

        return [.. problems.OrderBy(p => p.Item1, Utf8Order).ThenBy(p => p.Item2, StringComparer.Ordinal)];
    }

    // ------------------------------------------------------------------ profiles

    [Fact]
    public void TheRuntimeVerdictProfile_IsTheAevpSchemaTheLibraryShips()
    {
        var root = AefSchemaSet.RepoRoot();

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(root, "src", "AgentEval.MAF.AgentHooks", "Aevp", "aevp-0.1.schema.json")),
            File.ReadAllBytes(Path.Combine(root, "contracts", "aef", "profiles", "runtime-verdict", "aevp-0.1.schema.json")));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
