// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Runner;
using Xunit;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// AEF 1.0 run plans, runner capability manifests, runner event streams, and the runs a stream names. The expected
/// problems of each stream ([STRM-3]) and of each job's runs ([STRM-4]) are written by hand from
/// contracts/aef/1/spec/06-runners.md; the Python reference (tools/aef_stream.py) and the .NET verifier
/// (AgentEval.Results) both have to reproduce them.
/// </summary>
public class AefProtocolTests
{
    private static readonly string Protocol = Path.Combine(AefSchemaSet.Root, "conformance", "protocol");

    public static TheoryData<string, string> Documents()
    {
        var data = new TheoryData<string, string>();
        foreach (var folder in new[] { "plans", "runners" })
        {
            foreach (var dir in Directory.GetDirectories(Path.Combine(Protocol, folder)))
                data.Add(folder, Path.GetFileName(dir));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void APlanOrRunnerManifest_IsAcceptedOrRefused_AsItsExpectationSays(string folder, string name)
    {
        var dir = Path.Combine(Protocol, folder, name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "document.json")));
        var schema = (string)expected["schema"]!;

        Assert.Equal((string)expected["writer"]! == "valid", AefSchemaSet.Writer.Value.IsValid(schema, document, out var w));
        Assert.Equal((string)expected["reader"]! == "valid", AefSchemaSet.Reader.Value.IsValid(schema, document, out var r));
        _ = (w, r);
    }

    public static TheoryData<string> Streams() =>
        new(Directory.GetDirectories(Path.Combine(Protocol, "streams")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Streams))]
    public void EveryEvent_IsValidAgainstTheReaderSchema_AndTheWriterUnlessReaderOnly(string name)
    {
        var readerOnly = (bool?)Expected(name)["readerOnly"] == true;
        var writerValid = Events(name).All(e => AefSchemaSet.Writer.Value.IsValid("runner-event", e, out _));

        Assert.Equal(!readerOnly, writerValid);
        foreach (var (e, i) in Events(name).Select((e, i) => (e, i + 1)))
            Assert.True(AefSchemaSet.Reader.Value.IsValid("runner-event", e, out var r), $"{name} event {i} (reader): {r}");
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void TheVerifier_ReportsExactlyTheExpectedProblems(string name)
    {
        var expected = Expected(name);
        var planFile = Path.GetFullPath(Path.Combine(Protocol, "streams", name, (string)expected["plan"]!));
        var plan = JsonNode.Parse(File.ReadAllText(planFile));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(planFile))).ToLowerInvariant();

        Assert.Equal(
            expected["problems"]!.AsArray().Select(p => ((string)p!["where"]!, (string)p["problem"]!)),
            RunnerEventStream.Verify(Events(name), plan, digest));
    }

    public static TheoryData<string> Matching() =>
        new(Directory.GetDirectories(Path.Combine(Protocol, "matching")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Matching))]
    public void ARunnerMatchesAPlan_AsTheVectorSays(string name)
    {
        JsonNode Load(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Protocol, "matching", name, file)))!;

        // A reader-only vector holds a value a later minor may add ([VER-8]): only the reader schemas accept it.
        var readerOnly = (bool?)Load("expected.json")["readerOnly"] == true;
        var schemas = readerOnly ? AefSchemaSet.Reader.Value : AefSchemaSet.Writer.Value;
        Assert.True(schemas.IsValid("run-plan", Load("plan.json"), out var p), $"{name} plan: {p}");
        Assert.True(schemas.IsValid("runner", Load("runner.json"), out var r), $"{name} runner: {r}");
        if (readerOnly)
            Assert.False(AefSchemaSet.Writer.Value.IsValid("run-plan", Load("plan.json"), out _)
                         && AefSchemaSet.Writer.Value.IsValid("runner", Load("runner.json"), out _), $"{name}: a writer would write it");
        Assert.Equal((bool)Load("expected.json")["matches"]!, RunnerEventStream.Matches(Load("plan.json"), Load("runner.json")));
    }

    private static JsonNode Expected(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Protocol, "streams", name, "expected.json")))!;

    [Fact]
    public void TheStreamVectors_CoverEveryProblem()
    {
        var problems = Directory.GetFiles(Path.Combine(Protocol, "streams"), "expected.json", SearchOption.AllDirectories)
            .SelectMany(f => JsonNode.Parse(File.ReadAllText(f))!["problems"]!.AsArray().Select(p => (string)p!["problem"]!))
            .Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(["accepted-twice", "after-terminal", "estimate", "first", "job-id", "no-terminal", "over-budget", "over-cases", "over-time",
                      "plan-digest", "plan-id", "run-hash-changed", "seq", "spend-decreased", "time", "unannounced-run", "unsealed-run"],
            problems);
    }

    [Fact]
    public void AnUnknownEventKind_IsRefusedByTheWriter_AndReadAsOtherByAReader()
    {
        var e = JsonNode.Parse("""{"schemaVersion":"1.0","seq":2,"kind":"job.paused","jobId":"job-7","at":"2026-10-08T12:00:01Z"}""");

        Assert.False(AefSchemaSet.Writer.Value.IsValid("runner-event", e, out _));
        Assert.True(AefSchemaSet.Reader.Value.IsValid("runner-event", e, out var errors), errors);
    }

    private static List<JsonNode> Events(string name) => ReadStream(Path.Combine(Protocol, "streams", name, "events.ndjson"));

    /// <summary>A stream's finished lines: a last line without LF is still being written, and is not read ([STRM-2]).</summary>
    private static List<JsonNode> ReadStream(string file)
    {
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
        return [.. text[..(text.LastIndexOf('\n') + 1)].Split('\n').Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)];
    }

    [Fact]
    public void AStreamStillBeingWritten_IsReadUpToItsLastLf()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Protocol, "streams", "last-line-without-lf", "events.ndjson"));

        Assert.NotEqual((byte)'\n', bytes[^1]);
        Assert.Equal(["job.accepted", "spend.updated"], Events("last-line-without-lf").Select(e => (string)e["kind"]!));
    }

    [Fact]
    public void AnEventsExt_IsAnObject_ForTheWriterAndTheReader()
    {
        static JsonNode Event(string ext) =>
            JsonNode.Parse($$"""{"schemaVersion":"1.0","seq":2,"kind":"spend.updated","jobId":"job-7","at":"2026-10-08T12:00:01Z","spentUsd":0.1,"ext":{{ext}}}""")!;

        Assert.True(AefSchemaSet.Writer.Value.IsValid("runner-event", Event("""{"com.example.note":"x"}"""), out var w), w);
        Assert.True(AefSchemaSet.Reader.Value.IsValid("runner-event", Event("{}"), out var r), r);
        foreach (var notAnObject in new[] { "\"x\"", "1", "[]", "null" })
        {
            Assert.False(AefSchemaSet.Writer.Value.IsValid("runner-event", Event(notAnObject), out _), notAnObject);
            Assert.False(AefSchemaSet.Reader.Value.IsValid("runner-event", Event(notAnObject), out _), notAnObject);
        }
    }

    [Fact]
    public void EveryProtocolVector_NamesItsKindAndTheRulesItTests()
    {
        foreach (var file in Directory.GetFiles(Protocol, "expected.json", SearchOption.AllDirectories))
        {
            var expected = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.Contains((string?)expected["kind"], new[] { "plan", "matching", "stream", "plan-conformance" });
            Assert.NotEmpty(expected["rules"]!.AsArray());
        }
    }

    // ------------------------------------------------------------------ [STRM-4]: the runs a stream names keep to its plan

    private static readonly string PlanConformance = Path.Combine(Protocol, "plan-conformance");

    public static TheoryData<string> PlanConformanceVectors() =>
        new(Directory.GetDirectories(PlanConformance).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(PlanConformanceVectors))]
    public void TheRunsAStreamNames_KeepToThePlan_OrTheExpectedProblemsAreReported(string name)
    {
        var dir = Path.Combine(PlanConformance, name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        var events = ReadStream(Path.Combine(dir, (string)expected["events"]!));
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, (string)expected["plan"]!)))!;
        var runs = RunFolder.Find(Path.Combine(dir, (string)expected["runs"]!));
        var policy = expected["policy"] is { } file ? JsonNode.Parse(File.ReadAllText(Path.Combine(dir, (string)file!))) : null;

        Assert.Equal(
            expected["problems"]!.AsArray().Select(p => ((string)p![0]!, (string)p[1]!)),
            RunnerEventStream.Conform(events, plan, runs, run => SealVerifies(run, policy)));
    }

    [Theory]
    [MemberData(nameof(PlanConformanceVectors))]
    public void APlanConformanceVector_IsWrittenAsAWriterWould_AndItsStreamKeepsStrm3(string name)
    {
        // So that its problems are [STRM-4]'s alone.
        var dir = Path.Combine(PlanConformance, name);
        var planFile = Path.Combine(dir, "plan.json");
        var plan = JsonNode.Parse(File.ReadAllText(planFile))!;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(planFile))).ToLowerInvariant();
        var events = ReadStream(Path.Combine(dir, "events.ndjson"));

        Assert.True(AefSchemaSet.Writer.Value.IsValid("run-plan", plan, out var p), $"{name} plan: {p}");
        foreach (var (e, i) in events.Select((e, i) => (e, i + 1)))
            Assert.True(AefSchemaSet.Writer.Value.IsValid("runner-event", e, out var w), $"{name} event {i}: {w}");
        Assert.Empty(RunnerEventStream.Verify(events, plan, digest));
        foreach (var run in RunFolder.Find(Path.Combine(dir, "runs")))
            Assert.True(AefSchemaSet.Writer.Value.IsValid("run", run.Run, out var r), $"{name} {run.Path}: {r}");
    }

    [Fact]
    public void ThePlanConformanceVectors_CoverEveryProblem()
    {
        var problems = Directory.GetFiles(PlanConformance, "expected.json", SearchOption.AllDirectories)
            .SelectMany(f => JsonNode.Parse(File.ReadAllText(f))!["problems"]!.AsArray().Select(p => (string)p![1]!))
            .Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(["content-capture", "deployment", "judges", "no-cost", "over-budget", "over-cases", "provenance", "run-hash", "run-missing",
                      "subject", "suite", "target-mode", "time"],
            problems);
    }

    [Theory]
    [InlineData("https://api.example.com/v1?api-key=sk-live-0123456789abcdef", false)]
    [InlineData("https://api.example.com/v1#sk-live-0123456789abcdef", false)]
    [InlineData("https://user:hunter2@api.example.com/v1", false)]
    [InlineData("http://localhost:5080/v1", true)]
    public void APlansEndpoint_IsSchemeHostAndPathOnly_AsInRunJson(string endpoint, bool valid)
    {
        // [PLAN-1], [PLAN-4], [RUN-10]: the plan's subject.endpoint and run.json's deployment.endpoint accept the same values.
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(PlanConformance, "valid", "plan.json")))!;
        plan["subject"]!["endpoint"] = endpoint;
        var run = JsonNode.Parse(File.ReadAllText(Path.Combine(PlanConformance, "valid", "runs", "R-1", "run.json")))!;
        run["deployment"]!["endpoint"] = endpoint;

        foreach (var set in new[] { AefSchemaSet.Writer.Value, AefSchemaSet.Reader.Value })
        {
            Assert.Equal(valid, set.IsValid("run-plan", plan, out _));
            Assert.Equal(valid, set.IsValid("run", run, out _));
        }
    }

    [Fact]
    public void ARunHash_IsItsSealsRunHash_TheSha256OfTheManifestOfItsSealedFiles()
    {
        // [SEAL-4]: for a sealed run, its seal's runHash, which is the manifest's hash while every sealed file is there.
        var run = RunFolder.Find(Path.Combine(PlanConformance, "valid", "runs")).Single(r => r.RunId == "R-1");
        var seal = JsonNode.Parse(File.ReadAllText(Path.Combine(run.Path, "seal.json")))!;

        Assert.Equal((string)seal["predicate"]!["runHash"]!, run.RunHash);
        Assert.Equal(run.RunHash, RunFolder.ComputeRunHash(run.Path));
        Assert.DoesNotContain("seal.json", RunFolder.SealedFiles(run.Path));

        // A redacted blob is gone: only the seal's value can be known. A run without a seal has the recomputed one.
        var redacted = RunFolder.Find(Path.Combine(PlanConformance, "redacted-run", "runs")).Single();
        Assert.NotEqual(redacted.RunHash, RunFolder.ComputeRunHash(redacted.Path));
        var unsealed = RunFolder.Find(Path.Combine(PlanConformance, "run-hash-unsealed", "runs")).Single();
        Assert.Equal(RunFolder.ComputeRunHash(unsealed.Path), unsealed.RunHash);
    }

    /// <summary>
    /// Whether a run's seal verifies (spec 04 §4.1), as far as the plan-conformance vectors need: seal.json lists exactly
    /// the sealed files with their digests, a file that is gone only when an authorized redaction withholds it; its
    /// predicate names the run; and, when nothing is withheld, its runHash is the one recomputed from the files. The .NET
    /// tests have no verifier of the rules across files (§3.9); the Python reference decides intact with the whole of §4.5.
    /// </summary>
    private static bool SealVerifies(RunFolder run, JsonNode? policy)
    {
        var file = Path.Combine(run.Path, "seal.json");
        if (!File.Exists(file))
            return false;
        var seal = JsonNode.Parse(File.ReadAllText(file))!;
        var subjects = seal["subject"]!.AsArray().ToDictionary(s => (string)s!["name"]!, s => (string)s!["digest"]!["sha256"]!, StringComparer.Ordinal);
        var present = RunFolder.SealedFiles(run.Path).ToHashSet(StringComparer.Ordinal);
        var withheld = Withheld(run, policy);
        return present.All(subjects.ContainsKey)
               && subjects.All(s => present.Contains(s.Key) ? s.Value == Sha256(File.ReadAllBytes(Path.Combine(run.Path, s.Key))) : withheld.Contains(s.Key))
               && (string?)seal["predicate"]?["runId"] == run.RunId
               && (withheld.Count > 0 || (string?)seal["predicate"]?["runHash"] == RunFolder.ComputeRunHash(run.Path));
    }

    /// <summary>
    /// The blobs an authorized redaction withholds ([OVL-10]), as far as these vectors need: a redact event in an overlay
    /// batch whose seal covers its bytes and names the run's run hash, and whose envelope (seal-&lt;nnnn&gt;.dsse.json)
    /// verifies for the event's identity under a policy key that may redact. ECDSA P-256 keys only; the chain itself
    /// ([OVL-5]) is not checked here.
    /// </summary>
    private static HashSet<string> Withheld(RunFolder run, JsonNode? policy)
    {
        var withheld = new HashSet<string>(StringComparer.Ordinal);
        var overlays = Path.Combine(run.Path, "overlays");
        if (policy is null || !Directory.Exists(overlays))
            return withheld;
        var events = File.ReadAllBytes(Path.Combine(overlays, "events.ndjson"));
        foreach (var sealFile in Directory.GetFiles(overlays, "seal-*.json").Where(f => !f.EndsWith(".dsse.json", StringComparison.Ordinal)))
        {
            var envelopeFile = sealFile[..^".json".Length] + ".dsse.json";
            if (!File.Exists(envelopeFile))
                continue;
            var batchBytes = File.ReadAllBytes(sealFile);
            var batch = JsonNode.Parse(batchBytes)!;
            var chunk = events.AsSpan((int)batch["predicate"]!["offset"]!, (int)batch["predicate"]!["length"]!).ToArray();
            var envelope = JsonNode.Parse(File.ReadAllText(envelopeFile))!;
            if ((string?)batch["predicate"]!["runHash"] != run.RunHash || (string?)batch["subject"]![0]!["digest"]!["sha256"] != Sha256(chunk)
                || !Convert.FromBase64String((string)envelope["payload"]!).AsSpan().SequenceEqual(batchBytes))
                continue;
            var pae = Pae((string)envelope["payloadType"]!, batchBytes);
            var redactors = policy["keys"]!.AsArray()
                .Where(k => (k!["may"]?.AsArray() ?? []).Any(m => (string?)m == "redact") && envelope["signatures"]!.AsArray().Any(s => Verifies(k, s!, pae)))
                .Select(k => (string)k!["identity"]!).ToHashSet(StringComparer.Ordinal);
            foreach (var e in Encoding.UTF8.GetString(chunk).Split('\n').Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!))
            {
                if ((string?)e["kind"] == "redact" && redactors.Contains((string?)e["by"]?["identity"] ?? "") && (string?)e["target"]?["blob"] is { } blob)
                    withheld.Add($"blobs/sha256/{blob[..2]}/{blob}");
            }
        }

        return withheld;
    }

    private static bool Verifies(JsonNode key, JsonNode signature, byte[] pae)
    {
        try
        {
            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportFromPem((string)key["publicKey"]!);
            return ecdsa.VerifyData(pae, Convert.FromBase64String((string)signature["sig"]!), System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return false;   // not an ECDSA P-256 key, or not a signature it can read
        }
    }

    /// <summary>DSSE's pre-authentication encoding: "DSSEv1 &lt;len(type)&gt; &lt;type&gt; &lt;len(body)&gt; &lt;body&gt;", lengths in bytes.</summary>
    private static byte[] Pae(string payloadType, byte[] body) =>
        [.. Encoding.UTF8.GetBytes($"DSSEv1 {Encoding.UTF8.GetByteCount(payloadType)} {payloadType} {body.Length} "), .. body];

    private static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
