// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// The AEF 1.0 contract (contracts/aef/1): the schemas, the conformance corpus, result ids, the seal and the overlay
/// chain. The corpus is written by contracts/aef/tools/build_conformance.py (Python); everything here is recomputed in
/// .NET, so the corpus is checked by two implementations of the rules in 1/spec/.
/// </summary>
public class AefConformanceTests
{
    private static readonly string Root = AefSchemaSet.Root;
    private static readonly string Conformance = Path.Combine(Root, "conformance");

    private static readonly Lazy<AefSchemaSet> Writer = AefSchemaSet.Writer;
    private static readonly Lazy<AefSchemaSet> Reader = AefSchemaSet.Reader;

    // ------------------------------------------------------------------ schemas

    [Fact]
    public void EveryWriterSchema_Is2020_12_WithAnIdUnderWriter()
    {
        var files = Directory.GetFiles(Path.Combine(Root, "schemas", "writer"), "*.schema.json");

        Assert.Equal(15, files.Length);
        Assert.All(files, f =>
        {
            var node = JsonNode.Parse(File.ReadAllText(f))!;
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", (string?)node["$schema"]);
            Assert.Equal($"https://agenteval.dev/aef/1/writer/{Path.GetFileName(f)}", (string?)node["$id"]);
        });
    }

    [Fact]
    public void ReaderSchemas_AreTheWriterSchemas_MadeTolerant()
    {
        // tools/derive_reader.py wrote them; this derives them again and compares.
        foreach (var writer in Directory.GetFiles(Path.Combine(Root, "schemas", "writer"), "*.schema.json"))
        {
            var schema = JsonNode.Parse(File.ReadAllText(writer))!.AsObject();
            var expected = Derive(schema, inCondition: false, Path.GetFileName(writer), "")!.AsObject();
            expected["description"] = "READER (tolerant), derived from the writer schema by tools/derive_reader.py. " + (string?)schema["description"];
            var committed = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "schemas", "reader", Path.GetFileName(writer))));

            Assert.True(JsonNode.DeepEquals(expected, committed), $"reader/{Path.GetFileName(writer)} is not derived from writer/");
        }
    }

    // The enums [VER-9] closes for major 1: the rules across files compute with them, so a reader keeps them closed.
    private static readonly HashSet<(string File, string Pointer)> ClosedEnums =
    [
        ("common.schema.json", "/$defs/state"),
        ("run.schema.json", "/properties/status"),
        ("metrics.schema.json", "/properties/metrics/items/properties/kind"),
    ];

    private static JsonNode? Derive(JsonNode? node, bool inCondition, string file, string pointer)
    {
        switch (node)
        {
            case JsonArray array:
                return new JsonArray(array.Select((item, i) => Derive(item, inCondition, file, $"{pointer}/{i}")).ToArray());
            case JsonObject when !inCondition && ClosedEnums.Contains((file, pointer)):
                return Derive(node, true, file, pointer);   // closed: as the writer has it
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (key == "$id")
                    {
                        result[key] = ((string)value!).Replace("/aef/1/writer/", "/aef/1/reader/", StringComparison.Ordinal);
                    }
                    else if (inCondition)
                    {
                        result[key] = Derive(value, true, file, $"{pointer}/{key}");
                    }
                    else if (key == "type" && obj.ContainsKey("enum"))
                    {
                        // the enum below decides the type, whatever the key order
                    }
                    else if (key == "additionalProperties" && value is JsonValue v && v.TryGetValue<bool>(out var b) && !b)
                    {
                        // unknown fields are allowed
                    }
                    else if (key == "enum")
                    {
                        // Open: an unknown value reads as "other". A nullable enum stays nullable.
                        result["type"] = obj["type"] is JsonArray types ? types.DeepClone()
                            : value is JsonArray values && values.Any(x => x is null) ? new JsonArray("string", "null")
                            : "string";
                    }
                    else if (key == "const" && value is JsonValue c && c.TryGetValue<string>(out var s) && s == "1.0")
                    {
                        // Any minor of the known major ('$' is the end of the input, [ENC-15]).
                        result["type"] = "string";
                        result["pattern"] = "^1[.][0-9]+$";
                    }
                    else if (key is "if" or "not")
                    {
                        // A condition selects a rule and a prohibition forbids: relaxing either changes what it means.
                        result[key] = Derive(value, true, file, $"{pointer}/{key}");
                    }
                    else if (key == "oneOf" && value is JsonArray branches && branches.Count > 0 && branches.All(br => !string.IsNullOrEmpty(KindOf(br))))
                    {
                        // A union discriminated by kind: known kinds keep their rules; an unknown kind reads as "other".
                        var anyOf = new JsonArray(branches.Select((br, i) => Derive(br, false, file, $"{pointer}/oneOf/{i}")).ToArray());
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
                        result[key] = Derive(value, false, file, $"{pointer}/{key}");
                    }
                }

                return result;
            default:
                return node?.DeepClone();
        }
    }

    private static string? KindOf(JsonNode? branch) =>
        branch is JsonObject && branch["properties"]?["kind"]?["const"] is JsonValue k && k.TryGetValue<string>(out var kind) ? kind : null;

    // ------------------------------------------------------------------ the corpus against the schemas

    public static TheoryData<string> ValidRuns() => new(Directory.GetDirectories(Path.Combine(Conformance, "valid")).Select(Path.GetFileName)!);

    /// <summary>A valid run's folder: valid/&lt;name&gt;/run/, its expected.json beside run/ (spec 09 §9.2.1).</summary>
    private static string ValidRun(string name) => Path.Combine(Conformance, "valid", name, "run");

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void AValidRun_PassesTheWriterAndTheReaderSchemas_FileByFile(string name)
    {
        foreach (var (schema, document, where) in Documents(ValidRun(name)))
        {
            Assert.True(Writer.Value.IsValid(schema, document, out var writerErrors), $"{where} (writer): {writerErrors}");
            Assert.True(Reader.Value.IsValid(schema, document, out var readerErrors), $"{where} (reader): {readerErrors}");
        }
    }

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void AValidRun_HasItsRequiredFiles_AndItsReferencesResolve(string name)
    {
        var run = ValidRun(name);
        var header = ReadJson(Path.Combine(run, "run.json"));
        var closed = (string)header["status"]! != "running";

        // [RUN-2]; seal.json goes with the outcome (AValidRun_IsSealedOrUnsealed_AsItsExpectationSays).
        foreach (var required in closed
                     ? new[] { "run.json", "results.ndjson", "metrics.json", "summary.json" }
                     : ["run.json", "results.ndjson", "metrics.json"])
        {
            Assert.True(File.Exists(Path.Combine(run, required)), $"{name}: {required} is required");
        }

        // The rules across files a reader checks (spec 03 §3.9).
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
                Assert.Equal(Integer(reasoning["bytes"]), new FileInfo(blob).Length);
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
    public void AValidRun_IsSealedOrUnsealed_AsItsExpectationSays(string name)
    {
        // expected.json beside run/: a run with no problems, and its outcome (spec 04 §4.5) as far as the seal decides it.
        var expected = ReadJson(Path.Combine(Conformance, "valid", name, "expected.json"));
        var run = ValidRun(name);

        Assert.Equal("run", (string?)expected["kind"]);
        Assert.Empty(expected["problems"]!.AsArray());
        switch ((string?)expected["outcome"])
        {
            case "unsealed":
                Assert.False(File.Exists(Path.Combine(run, "seal.json")), $"{name}: an unsealed run has no seal.json");
                break;
            case "intact":
                Assert.Empty(Verify(run, Policy(Path.Combine(Conformance, "valid", name), expected)));
                break;
            default:
                Assert.Fail($"{name}: a valid run is intact or unsealed, not {expected["outcome"]}");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(ValidRuns))]
    public void NdjsonFiles_AreLfOnly_WithoutBom_AndEndInANewline(string name)
    {
        // NdjsonLines asserts the rules; a U+2028 inside a string is content, not a line break.
        foreach (var file in Directory.GetFiles(ValidRun(name), "*.ndjson", SearchOption.AllDirectories))
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
        // Every state of the writer schema ([RES-1]), over the runs the corpus says are valid: valid/, and the runs/
        // vectors that are intact or unsealed.
        var lines = Directory.GetDirectories(Path.Combine(Conformance, "valid"))
            .Concat(Directory.GetDirectories(Path.Combine(Conformance, "runs")))
            .Where(d => (string?)ReadJson(Path.Combine(d, "expected.json"))["outcome"] is "intact" or "unsealed")
            .SelectMany(d => NdjsonLines(Path.Combine(d, "run", "results.ndjson"))).ToList();
        var states = ReadJson(Path.Combine(Root, "schemas", "writer", "common.schema.json"))["$defs"]!["state"]!["enum"]!.AsArray();

        Assert.Equal(
            states.Select(s => (string)s!).Order(StringComparer.Ordinal),
            lines.Select(l => (string)JsonNode.Parse(l)!["state"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(lines, l => l.Contains('\u2028'));
    }

    public static TheoryData<string> InvalidDocuments() => new(Directory.GetDirectories(Path.Combine(Conformance, "invalid")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void AnInvalidDocument_IsRefusedByTheWriter_AndByTheReaderWhereExpected(string name)
    {
        var dir = Path.Combine(Conformance, "invalid", name);
        var expected = ReadJson(Path.Combine(dir, "expected.json"));
        var schema = (string)expected["schema"]!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "document.json")));

        Assert.False(Writer.Value.IsValid(schema, document, out _), $"{name}: the writer schema accepted it ({expected["why"]})");
        Assert.Equal((string)expected["reader"]! == "valid", Reader.Value.IsValid(schema, document, out _));
    }

    public static TheoryData<string> ReaderOnlyDocuments() => new(Directory.GetDirectories(Path.Combine(Conformance, "reader-only")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(ReaderOnlyDocuments))]
    public void AReaderOnlyDocument_IsRefusedByTheWriter_AndAcceptedByTheReader(string name)
    {
        // What a later minor could write ([VER-3]); how a reader reads the unknown value (reads, §7.3) is not checked here.
        var dir = Path.Combine(Conformance, "reader-only", name);
        var expected = ReadJson(Path.Combine(dir, "expected.json"));
        var schema = (string)expected["schema"]!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "document.json")));

        Assert.Equal(("invalid", "valid"), ((string?)expected["writer"], (string?)expected["reader"]));
        Assert.False(Writer.Value.IsValid(schema, document, out _), $"{name}: the writer schema accepted it");
        Assert.True(Reader.Value.IsValid(schema, document, out var errors), $"{name} (reader): {errors}");
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
            foreach (var seal in BatchSeals(Path.Combine(run, "overlays")))
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

    private static string Blob(string run, string uri) => Path.Combine(run, BlobPath(uri["sha256:".Length..]));

    /// <summary>A blob's path in its run ([EVD-3]): blobs/sha256/&lt;first two hex&gt;/&lt;hex&gt;.</summary>
    private static string BlobPath(string hex) => $"blobs/sha256/{hex[..2]}/{hex}";

    private static JsonNode ReadJson(string file) => JsonNode.Parse(File.ReadAllText(file))!;

    /// <summary>
    /// An integer field read as [ENC-4] says: as a binary64 value, so <c>2</c>, <c>2.0</c> and <c>2e0</c> are all 2. The
    /// schema has already refused a fractional value or one beyond 2^53 − 1.
    /// </summary>
    private static long Integer(JsonNode? value) => (long)value!.GetValue<double>();

    /// <summary>
    /// A JSON text read as I-JSON ([ENC-1], [ENC-2]): an object, in UTF-8 without a byte-order mark, with no member twice
    /// in one object and no unpaired surrogate. Null when it is not.
    /// </summary>
    private static JsonObject? ReadIJson(ReadOnlySpan<byte> utf8)
    {
        if (utf8 is [0xEF, 0xBB, 0xBF, ..])
            return null;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            var members = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        members.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        members.Pop();
                        break;
                    case JsonTokenType.PropertyName when !members.Peek().Add(reader.GetString()!):
                        return null;
                    case JsonTokenType.String:
                        _ = reader.GetString();   // throws on an unpaired surrogate
                        break;
                }
            }

            return JsonNode.Parse(utf8) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    [Theory]
    [InlineData("{\"a\":1,\"b\":{\"a\":\"\\ud83d\\udc4d\"}}", true)]
    [InlineData("{\"a\":1,\"a\":1}", false)]          // a member twice
    [InlineData("{\"a\":{\"b\":1,\"b\":2}}", false)]
    [InlineData("{\"a\":\"\\ud800\"}", false)]         // an unpaired surrogate
    [InlineData("{\"\\udc00\":1}", false)]
    [InlineData("\uFEFF{\"a\":1}", false)]           // a byte-order mark
    [InlineData("[1]", false)]                       // not an object
    [InlineData("{\"a\":NaN}", false)]
    public void AJsonText_IsReadAsIJson_OrRefused(string text, bool accepted) =>
        Assert.Equal(accepted, ReadIJson(Encoding.UTF8.GetBytes(text)) is not null);

    // ------------------------------------------------------------------ the corpus itself

    [Fact]
    public void TheIndex_ListsEveryFileOfTheCorpus_WithItsSha256()
    {
        // [CONF-1]: tools/build_index.py writes it, and a conformance runner checks every file it reads against it.
        var index = ReadJson(Path.Combine(Conformance, "index.json"));
        var vectors = index["vectors"]!.AsArray();
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var vector in vectors)
        {
            var path = (string)vector!["path"]!;
            foreach (var (file, sha256) in vector["files"]!.AsObject())
            {
                // A vector is a folder of files, or one file that its path names.
                var single = File.Exists(Path.Combine(Conformance, path));
                if (single) Assert.Equal(Path.GetFileName(path), file);
                Assert.True(listed.TryAdd(single ? path : $"{path}/{file}", (string)sha256!), $"{path}/{file} is listed twice");
            }
        }

        var onDisk = Directory.GetFiles(Conformance, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Conformance, f).Replace('\\', '/'))
            .Where(rel => rel != "index.json");
        Assert.Equal("1.0", (string?)index["aef"]);
        Assert.Equal(vectors.Count, vectors.Select(v => (string)v!["id"]!).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(onDisk.Order(StringComparer.Ordinal), listed.Keys.Order(StringComparer.Ordinal));
        foreach (var (rel, sha256) in listed)
        {
            Assert.True(sha256 == Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(Conformance, rel)))), $"{rel}: its bytes are not the ones index.json lists");
        }
    }

    [Fact]
    public void Git_KeepsTheCorpusByteExact()
    {
        // Seals hash exact bytes and a CRLF file is a vector: a Windows checkout with core.autocrlf must keep every corpus
        // file as committed. So a .gitattributes on the way down marks conformance/** -text, and nothing that takes
        // precedence over that line (a later line, or a deeper file) turns text conversion back on.
        var repo = AefSchemaSet.RepoRoot();
        var lines = new List<(string Pattern, string[] Attributes)>();   // lowest precedence first
        foreach (var dir in new[] { "", "contracts/", "contracts/aef/", "contracts/aef/1/", "contracts/aef/1/conformance/" })
        {
            var file = Path.Combine(repo, dir, ".gitattributes");
            if (!File.Exists(file))
                continue;
            foreach (var line in File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#'))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                lines.Add((dir + parts[0].TrimStart('/'), parts[1..]));
            }
        }

        var guard = lines.FindLastIndex(l => l.Pattern == "contracts/aef/1/conformance/**" && l.Attributes.Any(a => a is "-text" or "binary"));
        Assert.True(guard >= 0, "no .gitattributes marks contracts/aef/1/conformance/** as -text");
        Assert.DoesNotContain(lines.Skip(guard + 1), l => l.Attributes.Any(a =>
            a == "text" || a.StartsWith("text=", StringComparison.Ordinal) || a.StartsWith("eol=", StringComparison.Ordinal)));
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
        var run = ValidRun(name);
        var runId = (string)ReadJson(Path.Combine(run, "run.json"))["runId"]!;

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
        var expected = ReadJson(Path.Combine(dir, "expected.json"));
        var run = Path.Combine(dir, (string)expected["run"]!);

        Assert.Equal(Problems(expected), Verify(run, Policy(dir, expected)));
        if ((string?)expected["manifest"] is { } file)
        {
            // A sealable run: the manifest byte for byte, and the run hash is its SHA-256.
            var manifest = Manifest(run);
            Assert.Equal(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, file))), manifest);
            Assert.Equal(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))),
                (string)ReadJson(Path.Combine(run, "seal.json"))["predicate"]!["runHash"]!);
        }
    }

    [Fact]
    public void TheSealVectors_CoverEveryKindOfDifference()
    {
        var problems = Directory.GetFiles(Path.Combine(Conformance, "seal-vectors"), "expected.json", SearchOption.AllDirectories)
            .SelectMany(f => Problems(ReadJson(f)).Select(p => p.Problem))
            .Distinct().Order(StringComparer.Ordinal);

        // Every code of [SEAL-6].
        Assert.Equal(["digest", "duplicate-subject", "limit", "missing", "not-sealed", "predicate", "run-hash", "run-id", "run-open", "seal-invalid",
                      "subject-path", "withheld"], problems);
    }

    [Fact]
    public void TheCorpusKeepsBytesThatAReEncoderWouldChange()
    {
        // Non-ASCII text and a CRLF inside a sealed blob: a seal over bytes must not depend on any re-encoding.
        var blob = Directory.GetFiles(Path.Combine(ValidRun("completed-eval"), "blobs"), "*", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(blob);

        Assert.Contains((byte)'\r', bytes);
        Assert.Contains(bytes, b => b >= 0x80);
        Assert.Equal(Path.GetFileName(blob), Hex(SHA256.HashData(bytes)));
    }

    /// <summary>A vector's trust policy ([SIG-4]): the file its expected.json names as policy, beside it; null without one.</summary>
    private static JsonNode? Policy(string dir, JsonNode expected) =>
        (string?)expected["policy"] is { } file ? ReadJson(Path.Combine(dir, file)) : null;

    /// <summary>An expected.json's problems: [path, code] pairs, compared as an ordered list ([CONF-2]).</summary>
    private static List<(string Path, string Problem)> Problems(JsonNode expected) =>
        [.. expected["problems"]!.AsArray().Select(p => ((string)p![0]!, (string)p[1]!))];

    /// <summary>The manifest over a run's sealed files, ordered by the UTF-8 bytes of their paths ([SEAL-3]).</summary>
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

    /// <summary>[SEAL-1]: every file of a run is sealed except seal.json, attestation.dsse.json and everything under overlays/.</summary>
    private static bool IsSealed(string rel) =>
        rel is not ("seal.json" or "attestation.dsse.json") && !rel.StartsWith("overlays/", StringComparison.Ordinal);

    private static List<string> SealedFiles(string run) =>
        Directory.GetFiles(run, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(run, f).Replace('\\', '/'))
            .Where(IsSealed)
            .Order(Utf8Order)
            .ToList();

    /// <summary>
    /// Verifies a run's seal (spec 04 §4.1, [SEAL-6]): every difference as (path, code), in the order of spec 03 §3.9.
    /// Verification stops after seal-invalid. A subject listed twice (duplicate-subject) or naming a file that
    /// is never sealed (subject-path) is not compared further; digest, not-sealed, missing and withheld compare the files
    /// with the other subjects; run-hash is reported only when every file matches its subject; run-id, predicate and
    /// run-open compare the statement with run.json. The trust policy decides which redactions are authorized.
    /// </summary>
    private static List<(string Path, string Problem)> Verify(string run, JsonNode? policy)
    {
        // [ENC-17]: a seal beyond 40 MiB or nested deeper than 64 is refused, and not checked further ([SEAL-6] limit).
        var sealBytes = File.ReadAllBytes(Path.Combine(run, "seal.json"));
        if (!WithinLimits(sealBytes, MaxSealBytes))
            return [("seal.json", "limit")];
        var seal = ReadIJson(sealBytes);
        if (seal is null || !Reader.Value.IsValid("seal", seal, out _))
            return [("seal.json", "seal-invalid")];
        var header = ReadJson(Path.Combine(run, "run.json"));
        var problems = new List<(string, string)>();

        var subjects = seal["subject"]!.AsArray().Select(s => (Name: (string)s!["name"]!, Digest: (string)s["digest"]!["sha256"]!)).ToList();
        var duplicated = subjects.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        // A duplicated subject is also reported with every other code whose condition holds for it (subject-path, or
        // missing when the file is gone); only its digests are not compared ([SEAL-6]).
        var unsealable = subjects.Select(s => s.Name).Where(n => !IsSealed(n)).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(duplicated.Select(n => (n, "duplicate-subject")));
        problems.AddRange(unsealable.Select(n => (n, "subject-path")));
        var sealedDigests = subjects.Where(s => !duplicated.Contains(s.Name) && !unsealable.Contains(s.Name))
            .ToDictionary(s => s.Name, s => s.Digest, StringComparer.Ordinal);

        // A sealed blob that is gone is withheld when an authorized redaction names it ([OVL-10]), and missing otherwise.
        var withheld = Withheld(run, policy);
        var present = SealedFiles(run).ToHashSet(StringComparer.Ordinal);
        var everyFileMatches = duplicated.Count == 0;
        foreach (var rel in present.Union(sealedDigests.Keys).Where(r => !duplicated.Contains(r)))
        {
            string? problem = !sealedDigests.TryGetValue(rel, out var digest) ? "not-sealed"
                : !present.Contains(rel) ? (withheld.Contains(rel) ? "withheld" : "missing")
                : digest != Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(run, rel)))) ? "digest"
                : null;
            if (problem is not null)
            {
                problems.Add((rel, problem));
                everyFileMatches = false;
            }
        }
        foreach (var rel in duplicated.Where(r => !unsealable.Contains(r) && !present.Contains(r)))
            problems.Add((rel, withheld.Contains(rel) ? "withheld" : "missing"));

        if (everyFileMatches && Hex(SHA256.HashData(Encoding.UTF8.GetBytes(Manifest(run)))) != (string)seal["predicate"]!["runHash"]!)
            problems.Add(("seal.json", "run-hash"));
        if ((string)seal["predicate"]!["runId"]! != (string)header["runId"]!)
            problems.Add(("seal.json", "run-id"));
        if (!PredicateMatches(seal["predicate"]!, header) || SealedBeforeClosed(seal["predicate"]!))
            problems.Add(("seal.json", "predicate"));
        if ((string)header["status"]! == "running")
            problems.Add(("run.json", "run-open"));

        return Ordered(problems);
    }

    /// <summary>[SEAL-6] predicate: a seal made before the run closed (sealedAt earlier than closedAt, as times).</summary>
    private static bool SealedBeforeClosed(JsonNode predicate) =>
        AgentEval.Results.AefTime.Parse((string)predicate["sealedAt"]!) < AgentEval.Results.AefTime.Parse((string)predicate["closedAt"]!);

    private const int MaxSealBytes = 40 * 1024 * 1024;   // [ENC-17]: seal.json and a batch seal
    private const int MaxJsonBytes = 4 * 1024 * 1024;    // [ENC-17]: a JSON file or an NDJSON line
    private const int MaxDepth = 64;                     // [ENC-17]: the top-level value is at depth 1

    /// <summary>[ENC-17], checked on the bytes before the content is trusted: a size, and a depth scan.</summary>
    private static bool WithinLimits(ReadOnlySpan<byte> bytes, int maxBytes) => bytes.Length <= maxBytes && Depth(bytes) <= MaxDepth;

    /// <summary>The deepest nesting of objects and arrays, scanned on the bytes (brackets inside strings do not count).</summary>
    private static int Depth(ReadOnlySpan<byte> bytes)
    {
        int depth = 0, deepest = 0;
        var inString = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (inString)
            {
                if (b == (byte)'\\') i++;
                else if (b == (byte)'"') inString = false;
            }
            else if (b == (byte)'"') inString = true;
            else if (b is (byte)'[' or (byte)'{') deepest = Math.Max(deepest, ++depth);
            else if (b is (byte)']' or (byte)'}') depth--;
        }

        return deepest;
    }

    /// <summary>
    /// The predicate says what run.json says about the producer (name, version), subject (ref, version), deployment (ref),
    /// suite (ref, version, digest), judges (model, rubric digest, in order) and closedAt (its endedAt) ([SEAL-6]). A null
    /// deployment or suite, and no judges, say the same as their absence ([SEAL-5]); times compare as times ([ENC-8]).
    /// </summary>
    private static bool PredicateMatches(JsonNode predicate, JsonNode run)
    {
        static string? S(JsonNode? n) => n?.ToJsonString();
        static bool SameTime(JsonNode? a, JsonNode? b)
        {
            try
            {
                return S(a) == S(b) || (a is not null && b is not null && AgentEval.Results.AefTime.Parse((string)a!) == AgentEval.Results.AefTime.Parse((string)b!));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        var same = S(predicate["producer"]?["name"]) == S(run["producer"]?["name"])
                   && S(predicate["producer"]?["version"]) == S(run["producer"]?["version"])
                   && S(predicate["subject"]?["ref"]) == S(run["subject"]?["ref"])
                   && S(predicate["subject"]?["version"]) == S(run["subject"]?["version"])
                   && S(predicate["deployment"]?["ref"]) == S(run["deployment"]?["ref"])
                   && S(predicate["suite"]?["ref"]) == S(run["suite"]?["ref"])
                   && S(predicate["suite"]?["version"]) == S(run["suite"]?["version"])
                   && S(predicate["suite"]?["digest"]) == S(run["suite"]?["digest"]);
        var judges = (predicate["judges"]?.AsArray() ?? []).Select(j => $"{j!["model"]}|{j["rubricDigest"]}");
        var runJudges = (run["judges"]?.AsArray() ?? []).Select(j => $"{j!["model"]}|{j["rubricDigest"]}");
        return same && SameTime(predicate["closedAt"], run["endedAt"]) && judges.SequenceEqual(runJudges, StringComparer.Ordinal);
    }

    /// <summary>
    /// The order of problems (spec 03 §3.9, which [SEAL-6] and [OVL-5] follow): by path as UTF-8 bytes, except that the
    /// &lt;file&gt;:&lt;line&gt; paths of one file go by line number as a number; then by code.
    /// </summary>
    private static List<(string Path, string Problem)> Ordered(IEnumerable<(string Path, string Problem)> problems) =>
        [.. problems.OrderBy(p => p.Path, PathOrder).ThenBy(p => p.Problem, StringComparer.Ordinal)];

    private static readonly Regex LinePath = new("^(.*):([0-9]+)\\z");

    private static readonly Comparer<string> PathOrder = Comparer<string>.Create((a, b) =>
    {
        var (lineA, lineB) = (LinePath.Match(a), LinePath.Match(b));
        return lineA.Success && lineB.Success && lineA.Groups[1].Value == lineB.Groups[1].Value
            ? long.Parse(lineA.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(long.Parse(lineB.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            : Utf8Order.Compare(a, b);
    });

    [Fact]
    public void Problems_AreOrderedByPath_WithTheLinesOfAFileByNumber()
    {
        // No seal or chain vector reaches a tenth line, so the order of §3.9 is checked here.
        (string, string)[] problems =
        [
            ("results.ndjson:10", "parent"), ("seal.json", "run-id"), ("results.ndjson:9", "result-id"), ("results.ndjson", "encoding"),
            ("results.ndjson:9", "parent"), ("ext/a.b", "digest"), ("ext/a-b", "digest"), ("ext/Z", "digest"), ("ext/a/b", "digest"),
        ];

        Assert.Equal(
            [("ext/Z", "digest"), ("ext/a-b", "digest"), ("ext/a.b", "digest"), ("ext/a/b", "digest"), ("results.ndjson", "encoding"),
             ("results.ndjson:9", "parent"), ("results.ndjson:9", "result-id"), ("results.ndjson:10", "parent"), ("seal.json", "run-id")],
            Ordered(problems));
    }

    // ------------------------------------------------------------------ the overlay chain

    [Fact]
    public void AValidRunsOverlayBatches_FormAnUnbrokenChain() =>
        Assert.Empty(VerifyChain(ValidRun("completed-eval")).Problems);

    public static TheoryData<string> ChainVectors() =>
        new(Directory.GetDirectories(Path.Combine(Conformance, "chain-vectors")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(ChainVectors))]
    public void ABrokenChain_IsReported_WithExactlyTheExpectedProblems(string name)
    {
        var dir = Path.Combine(Conformance, "chain-vectors", name);
        var expected = ReadJson(Path.Combine(dir, "expected.json"));

        Assert.Equal(Problems(expected), VerifyChain(Path.Combine(dir, (string)expected["run"]!)).Problems);
    }

    [Fact]
    public void TheChainVectors_CoverEveryKindOfProblem()
    {
        var problems = Directory.GetFiles(Path.Combine(Conformance, "chain-vectors"), "expected.json", SearchOption.AllDirectories)
            .SelectMany(f => Problems(ReadJson(f)).Select(p => p.Problem))
            .Distinct().Order(StringComparer.Ordinal);

        // Every code of [OVL-5], and encoding for an events file whose framing breaks.
        Assert.Equal(["batch-digest", "batch-invalid", "batch-number", "encoding", "event-id", "event-invalid", "limit", "line-boundary", "missing", "offset",
                      "previous", "run-hash", "run-id", "target", "uncovered", "unexpected-file"], problems);
    }

    /// <summary>
    /// Verifies the overlays (spec 04 §4.2, [OVL-5]): the files under overlays/; every batch seal from 1 to the highest
    /// present, with its number, run id, run hash, offset, line boundaries, digest and previous seal; that the batches
    /// cover the events file; and each event's validity, id and target. Also returns the events the effective view is
    /// computed from (§4.3), each with its batch: the lines of the batches that verify, from batch 1 up to the first that
    /// does not, less any line with a problem of its own.
    /// </summary>
    private static (List<(string Path, string Problem)> Problems, List<(JsonNode Event, int Batch)> Verified) VerifyChain(string run)
    {
        var overlays = Path.Combine(run, "overlays");
        var runId = (string)ReadJson(Path.Combine(run, "run.json"))["runId"]!;
        var runHash = RunHash(run);
        var events = File.Exists(Path.Combine(overlays, "events.ndjson")) ? File.ReadAllBytes(Path.Combine(overlays, "events.ndjson")) : [];
        var problems = new List<(string, string)>();

        foreach (var rel in Directory.GetFiles(overlays, "*", SearchOption.AllDirectories).Select(f => "overlays/" + Path.GetRelativePath(overlays, f).Replace('\\', '/')))
        {
            // The events file, a batch seal, or a batch's signature (seal-<nnnn>.dsse.json, [SIG-1]).
            if (rel != "overlays/events.ndjson" && !Regex.IsMatch(rel, "^overlays/seal-[0-9]{4}(\\.dsse)?\\.json\\z"))
                problems.Add((rel, "unexpected-file"));
        }

        // An events file whose framing breaks [ENC-5] or [ENC-7] (a byte-order mark, a CR, a blank line, no final LF) is
        // reported once, and the chain is not checked further: no batch of it verifies, no event of it has an effect.
        if (events.Length > 0 && (events is [0xEF, 0xBB, 0xBF, ..] || events.AsSpan().IndexOf((byte)'\r') >= 0 || events[^1] != (byte)'\n'
                                  || events[0] == (byte)'\n' || events.AsSpan().IndexOf("\n\n"u8) >= 0))
        {
            problems.Add(("overlays/events.ndjson", "encoding"));
            return (Ordered(problems), []);
        }

        var last = BatchSeals(overlays).Select(f => int.Parse(Path.GetFileName(f)[5..9], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        if (File.Exists(Path.Combine(overlays, "seal-0000.json")))
            problems.Add(("overlays/seal-0000.json", "batch-number"));   // batches are 1-based: not checked further
        var covered = new List<(long From, long To)>();
        long expectedOffset = 0;   // where the next batch starts; -1 when unknown (after a missing or an invalid seal)
        var verifiedBatches = new List<(int Batch, long From, long To)>();   // the batches that verify, from batch 1
        var verifying = true;
        for (var n = 1; n <= last; n++)
        {
            var name = $"overlays/seal-{n:D4}.json";
            var file = Path.Combine(overlays, $"seal-{n:D4}.json");
            var before = problems.Count;
            if (File.Exists(file) && !WithinLimits(File.ReadAllBytes(file), MaxSealBytes))
            {
                problems.Add((name, "limit"));   // [ENC-17]: refused, not checked further, and it ends the verified prefix
                expectedOffset = -1;
                verifying = false;
                continue;
            }

            var statement = File.Exists(file) ? ReadIJson(File.ReadAllBytes(file)) : null;
            if (statement is null || !Reader.Value.IsValid("overlay-seal", statement, out _))
            {
                problems.Add((name, File.Exists(file) ? "batch-invalid" : "missing"));   // not checked further
                expectedOffset = -1;
                verifying = false;
                continue;
            }

            var p = statement["predicate"]!;
            long offset = Integer(p["offset"]), length = Integer(p["length"]);
            if (Integer(p["batch"]) != n) problems.Add((name, "batch-number"));
            if ((string)p["runId"]! != runId) problems.Add((name, "run-id"));
            if ((string)p["runHash"]! != runHash) problems.Add((name, "run-hash"));
            if (expectedOffset >= 0 && offset != expectedOffset) problems.Add((name, "offset"));
            // Coverage is what the batches claim (clipped to the file); whether a claimed range holds its bytes is
            // batch-digest, whether it starts and ends on a line inside the file is line-boundary.
            var (from, to) = ((int)Math.Min(offset, events.Length), (int)Math.Min(offset + length, events.Length));
            covered.Add((from, to));
            if (offset + length > events.Length || events[(int)(offset + length - 1)] != (byte)'\n' || (offset > 0 && events[(int)offset - 1] != (byte)'\n'))
                problems.Add((name, "line-boundary"));
            if (Hex(SHA256.HashData(events.AsSpan(from, to - from))) != (string)statement["subject"]![0]!["digest"]!["sha256"]!)
                problems.Add((name, "batch-digest"));

            var previous = p["previous"];
            var previousFile = Path.Combine(overlays, $"seal-{n - 1:D4}.json");
            var previousOk = n == 1
                ? previous is null
                : previous is not null && (string)previous["path"]! == $"overlays/seal-{n - 1:D4}.json" && File.Exists(previousFile)
                  && (string)previous["sha256"]! == Hex(SHA256.HashData(File.ReadAllBytes(previousFile)));
            if (!previousOk) problems.Add((name, "previous"));
            expectedOffset = offset + length;
            verifying &= problems.Count == before;
            if (verifying) verifiedBatches.Add((n, offset, offset + length));
        }

        long reach = 0;   // the union of the claimed ranges, from the first byte
        foreach (var (from, to) in covered.OrderBy(c => c.From))
        {
            if (from > reach) break;
            reach = Math.Max(reach, to);
        }

        if (reach != events.Length)
            problems.Add(("overlays/events.ndjson", "uncovered"));

        // Each event: an I-JSON object valid against the reader schema (or it is not checked further), an id no earlier
        // line has ([OVL-1]), and a target inside its own run ([OVL-2]).
        var resultIds = File.ReadAllText(Path.Combine(run, "results.ndjson")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => (string?)JsonNode.Parse(l)!["resultId"]).ToHashSet(StringComparer.Ordinal);
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var verified = new List<(JsonNode Event, int Batch)>();
        var at = 0;
        for (var line = 1; at < events.Length; line++)
        {
            var start = at;
            var next = Array.IndexOf(events, (byte)'\n', at) is var lf and >= 0 ? lf + 1 : events.Length;
            var where = $"overlays/events.ndjson:{line}";
            var raw = events.AsSpan(at, next - at - 1);   // without its LF
            if (!WithinLimits(raw, MaxJsonBytes))
            {
                problems.Add((where, "limit"));   // [ENC-18]: at the line; a single event's problem
                at = next;
                continue;
            }

            var e = ReadIJson(events.AsSpan(at, next - at));
            at = next;
            if (e is null || !Reader.Value.IsValid("overlay-event", e, out _))
            {
                problems.Add((where, "event-invalid"));
                continue;
            }

            var before = problems.Count;
            if (!eventIds.Add((string)e["eventId"]!)) problems.Add((where, "event-id"));
            var target = e["target"]!;
            if ((string?)target["run"] != runId
                || (target["runHash"] is { } targetHash && (string?)targetHash != runHash)
                || (target["result"] is { } result && !resultIds.Contains((string?)result)))
                problems.Add((where, "target"));
            if (problems.Count == before && verifiedBatches.FirstOrDefault(b => b.From <= start && next <= b.To) is { Batch: > 0 } batch)
                verified.Add((e, batch.Batch));
        }

        return (Ordered(problems), verified);
    }

    /// <summary>
    /// The run hash the batches must name ([OVL-4]): seal.json's predicate.runHash when the seal is valid, which the seal
    /// verification checks against the files (recomputing it from the files would fail once an authorized redaction
    /// withholds a blob); otherwise the hash recomputed from the files ([SEAL-4], [OVL-5]).
    /// </summary>
    private static string RunHash(string run) =>
        File.Exists(Path.Combine(run, "seal.json")) && File.ReadAllBytes(Path.Combine(run, "seal.json")) is var bytes
            && WithinLimits(bytes, MaxSealBytes) && ReadIJson(bytes) is { } seal && Reader.Value.IsValid("seal", seal, out _)
            ? (string)seal["predicate"]!["runHash"]!
            : Hex(SHA256.HashData(Encoding.UTF8.GetBytes(Manifest(run))));

    /// <summary>
    /// The sealed blobs that authorized redactions withhold ([OVL-10]), as paths in the run: a redact event of a verified
    /// batch whose signature (overlays/seal-&lt;nnnn&gt;.dsse.json) verifies for the event's by.identity under the caller's
    /// trust policy, and the policy lets that identity redact ("may": ["redact"], [SIG-4]). No policy, no redaction.
    /// </summary>
    private static HashSet<string> Withheld(string run, JsonNode? policy)
    {
        var overlays = Path.Combine(run, "overlays");
        if (policy is not { } trust || !Directory.Exists(overlays))
            return [];

        bool MayRedact(string identity) => (trust["keys"]?.AsArray() ?? []).Any(k =>
            (string?)k?["identity"] == identity && (k["may"]?.AsArray() ?? []).Any(m => (string?)m == "redact"));
        bool SignedBy(int batch, string identity) =>
            File.Exists(Path.Combine(overlays, $"seal-{batch:D4}.dsse.json"))
            && VerifiesFor(File.ReadAllBytes(Path.Combine(overlays, $"seal-{batch:D4}.dsse.json")),
                File.ReadAllBytes(Path.Combine(overlays, $"seal-{batch:D4}.json")), "application/vnd.in-toto+json", trust).Contains(identity);

        return VerifyChain(run).Verified
            .Where(v => (string?)v.Event["kind"] == "redact" && v.Event["target"]?["blob"] is not null
                        && (string?)v.Event["by"]?["identity"] is { } identity && MayRedact(identity) && SignedBy(v.Batch, identity))
            .Select(v => BlobPath((string)v.Event["target"]!["blob"]!)).ToHashSet(StringComparer.Ordinal);
    }

    // Only seal-, four digits and .json is a batch seal ([OVL-5]).
    private static IEnumerable<string> BatchSeals(string overlays) =>
        Directory.GetFiles(overlays, "seal-*.json").Where(f => Regex.IsMatch(Path.GetFileName(f), "^seal-[0-9]{4}\\.json\\z"));

    // ------------------------------------------------------------------ signatures (what an overlay redaction needs)

    [Fact]
    public void ABatchSignature_VerifiesOnlyForTheBatchSealsBytes_AndAValidSignature()
    {
        // seal-vectors/withheld-blob signs its redaction's batch with the policy's key. No redaction vector has another
        // payload or a forged signature, so those are made here, from its files, in memory.
        var dir = Path.Combine(Conformance, "seal-vectors", "withheld-blob");
        var policy = ReadJson(Path.Combine(dir, "policy.json"));
        var envelope = File.ReadAllBytes(Path.Combine(dir, "run", "overlays", "seal-0003.dsse.json"));
        var seal = File.ReadAllBytes(Path.Combine(dir, "run", "overlays", "seal-0003.json"));
        const string InToto = "application/vnd.in-toto+json";
        var forged = ReadIJson(envelope)!;
        var sig = Base64((string)forged["signatures"]![0]!["sig"]!)!;
        sig[^1] ^= 1;   // the last byte of s: still DER, no longer the signature
        forged["signatures"]![0]!["sig"] = Convert.ToBase64String(sig);

        Assert.Equal([(string)policy["keys"]![0]!["identity"]!], VerifiesFor(envelope, seal, InToto, policy));
        Assert.Empty(VerifiesFor(envelope, [.. seal, (byte)'\n'], InToto, policy));   // not the file's bytes
        Assert.Empty(VerifiesFor(envelope, seal, "application/json", policy));          // not the file's type
        Assert.Empty(VerifiesFor(Encoding.UTF8.GetBytes(forged.ToJsonString()), seal, InToto, policy));
    }

    /// <summary>
    /// The identities a DSSE envelope verifies for (spec 04 §4.4), in policy order: its payload is the file's exact bytes
    /// with the type [SIG-1] gives that file, and a signature verifies with a key of the caller's trust policy ([SIG-4]),
    /// the one with its key id ([SIG-3]), or for a signature without one the first in policy order that verifies it. Only
    /// ECDSA P-256 is checked here, which is all the redaction vectors sign with; a key of another algorithm verifies
    /// nothing. The signature-vectors/ corpus is not run here.
    /// </summary>
    private static List<string> VerifiesFor(byte[] envelopeBytes, byte[] file, string payloadType, JsonNode policy)
    {
        var envelope = ReadIJson(envelopeBytes);
        var payload = Base64((string?)envelope?["payload"]);
        if (envelope?["signatures"] is not JsonArray { Count: > 0 } signatures || payload is null
            || (string?)envelope["payloadType"] != payloadType || !payload.AsSpan().SequenceEqual(file))
            return [];   // malformed, or payload-mismatch

        // PAE(type, body) = "DSSEv1" SP LEN(type) SP type SP LEN(body) SP body, lengths in ASCII decimal bytes.
        var message = Encoding.UTF8.GetBytes($"DSSEv1 {Encoding.UTF8.GetByteCount(payloadType)} {payloadType} {payload.Length} ").Concat(payload).ToArray();
        var keys = (policy["keys"]?.AsArray() ?? []).Select(TrustedKey).ToList();
        var verified = new HashSet<string>(StringComparer.Ordinal);   // key ids
        foreach (var signature in signatures)
        {
            var keyId = (string?)signature?["keyid"] ?? "";
            if (Base64((string?)signature?["sig"]) is not { } sig)
                continue;
            var candidates = keyId.Length == 0 ? keys : keys.Where(k => k.KeyId == keyId).Take(1);
            var hit = candidates.FirstOrDefault(k => k.Key?.VerifyData(message, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence) == true);
            if (hit.Key is not null)
                verified.Add(hit.KeyId);
        }

        return [.. keys.Where(k => verified.Contains(k.KeyId)).Select(k => k.Identity).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A trust-policy key: its identity, its key id (sha256: and the hex SHA-256 of the SPKI DER), and the key when it is ECDSA P-256.</summary>
    private static (string Identity, string KeyId, ECDsa? Key) TrustedKey(JsonNode? entry)
    {
        var identity = (string?)entry?["identity"] ?? "";
        var pem = (string?)entry?["publicKey"] ?? "";
        byte[] der;
        try
        {
            der = Convert.FromBase64String(pem[PemEncoding.Find(pem).Base64Data]);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return (identity, "", null);
        }

        var keyId = "sha256:" + Hex(SHA256.HashData(der));
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(der, out _);
        }
        catch (CryptographicException)
        {
            return (identity, keyId, null);   // not an EC key: unsupported-algorithm, which verifies nothing
        }

        return (identity, keyId, key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7" ? key : null);   // P-256
    }

    /// <summary>Base64 as [SIG-1] reads it: the standard or URL-safe alphabet, padded or not; null for whitespace, mixed alphabets or set unused bits.</summary>
    private static byte[]? Base64(string? text)
    {
        if (text is null || text.Any(char.IsWhiteSpace) || (text.IndexOfAny(['+', '/']) >= 0 && text.IndexOfAny(['-', '_']) >= 0))
            return null;
        var standard = text.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        try
        {
            var bytes = Convert.FromBase64String(standard + new string('=', (4 - (standard.Length % 4)) % 4));
            return Convert.ToBase64String(bytes).TrimEnd('=') == standard ? bytes : null;   // set unused bits re-encode differently
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ patterns

    [Fact]
    public void EveryPattern_HasNoLookaroundNorBackreference_AndOnlyAFinalDollar()
    {
        // [ENC-14]: written so that every common engine compiles it as written. [ENC-15] is then the validator's part:
        // AefSchemaSet compiles a final '$' as '\z', which covers a '$' only when it is the final one.
        foreach (var file in new[] { "writer", "reader" }.SelectMany(side => Directory.GetFiles(Path.Combine(Root, "schemas", side), "*.schema.json")))
        {
            foreach (var pattern in Patterns(JsonNode.Parse(File.ReadAllText(file))))
            {
                var where = $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}: {pattern}";
                var escapes = Regex.Matches(pattern, "\\\\.").Select(m => m.Value[1]).ToList();
                var bare = Regex.Replace(pattern, "\\\\.|\\[(\\\\.|[^\\]\\\\])*\\]", "x");   // without escapes and character classes
                Assert.False(Regex.IsMatch(bare, "\\(\\?<?[=!]"), $"lookaround in {where}");
                Assert.False(escapes.Any(c => c is (>= '1' and <= '9') or 'k'), $"a backreference in {where}");
                Assert.False(Regex.IsMatch(bare, "[*+?}]\\+"), $"a possessive quantifier in {where}");
                Assert.True(bare.IndexOf('$') is var dollar && (dollar < 0 || dollar == bare.Length - 1), $"a '$' before the end in {where}");
                Assert.False(escapes.Contains('s'), $"\\s in {where}");   // \s differs between regex engines: [!-~] instead
                Assert.False(escapes.Contains('d'), $"\\d in {where}");   // \d matches other scripts' digits in .NET and Python
            }
        }
    }

    [Fact]
    public void AFinalDollar_IsTheEndOfTheInput_InTheSchemaSet()
    {
        // .NET's '$' also matches before a final '\n' ([ENC-15]): the schema set compiles a final '$' as '\z'.
        Assert.Matches("^r-1$", "r-1\n");
        Assert.Equal("^r-1\\z", AefSchemaSet.AtEndOfInput("^r-1$"));
        Assert.DoesNotMatch(AefSchemaSet.AtEndOfInput("^r-1$"), "r-1\n");
        Assert.Equal("^a\\$", AefSchemaSet.AtEndOfInput("^a\\$"));            // an escaped '$' is a dollar sign
        Assert.Equal("^a\\\\\\z", AefSchemaSet.AtEndOfInput("^a\\\\$"));      // after an escaped backslash it is the end
        Assert.Equal("^a", AefSchemaSet.AtEndOfInput("^a"));
    }

    public static TheoryData<string> TrailingNewlineDocuments() =>
        new(Directory.GetDirectories(Path.Combine(Conformance, "invalid"))
            .Where(d => ReadJson(Path.Combine(d, "expected.json"))["rules"]!.AsArray().Any(r => (string?)r == "ENC-15"))
            .Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(TrailingNewlineDocuments))]
    public void AValueEndingInANewline_IsRefusedByItsAnchoredPattern(string name)
    {
        // [ENC-15]: the writer and the reader refuse the document, and accept it once the final newline is gone, so the
        // newline alone is what is refused.
        var dir = Path.Combine(Conformance, "invalid", name);
        var schema = (string)ReadJson(Path.Combine(dir, "expected.json"))["schema"]!;
        var document = ReadJson(Path.Combine(dir, "document.json"));

        Assert.False(Writer.Value.IsValid(schema, document, out _), $"{name}: the writer schema accepted it");
        Assert.False(Reader.Value.IsValid(schema, document, out _), $"{name}: the reader schema accepted it");
        Assert.Equal(1, TrimFinalNewlines(document));
        Assert.True(Writer.Value.IsValid(schema, document, out var errors), $"{name} without the newline: {errors}");
    }

    /// <summary>Removes the final '\n' of every string value that ends in one; returns how many it changed.</summary>
    private static int TrimFinalNewlines(JsonNode? node)
    {
        static string? Trimmed(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var text) && text.EndsWith('\n') ? text[..^1] : null;

        var changed = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (Trimmed(value) is { } text) { obj[key] = text; changed++; }
                    else changed += TrimFinalNewlines(value);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (Trimmed(array[i]) is { } text) { array[i] = text; changed++; }
                    else changed += TrimFinalNewlines(array[i]);
                }

                break;
        }

        return changed;
    }

    private static IEnumerable<string> Patterns(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(kv => kv.Key == "pattern" && kv.Value is JsonValue v ? [(string)v!] : Patterns(kv.Value)),
        JsonArray a => a.SelectMany(Patterns),
        _ => [],
    };

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
