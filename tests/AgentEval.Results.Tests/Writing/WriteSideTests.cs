using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Corpus;
using AgentEval.Results.Writing;
using Driver = AgentEval.Results.Conformance.Program;

namespace AgentEval.Results.Tests.Writing;

/// <summary>
/// The write side of spec 09 (§9.2.1, §9.3): <c>summarize</c>, <c>seal-write</c> and <c>sign</c>, run in process on
/// the corpus's write-vectors and judged as the conformance runner judges them (only its result counts for a claim:
/// tools/aef_conformance.py through the driver, in the container), and the pieces of AgentEval.Results under them:
/// <see cref="AefSummaryWriter"/>, <see cref="AefSealOptions.SealedAt"/>, <see cref="AefSigningKey"/>.
/// </summary>
public class WriteSideTests
{
    private static readonly string Vectors = Path.Combine(AefCorpus.Conformance, "write-vectors");

    public static TheoryData<string> SummarizeVectors() => Names("summarize");

    public static TheoryData<string> SealWriteVectors() => Names("seal-write");

    public static TheoryData<string> SignVectors() => Names("sign");

    // ------------------------------------------------------------------ the corpus, in process

    [Theory]
    [MemberData(nameof(SummarizeVectors))]
    public void Summarize_GivesTheExpectedSummary_AndTheRunWithItVerifies(string name)
    {
        var folder = Path.Combine(Vectors, "summarize", name);
        var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;
        var (code, output, error) = Dispatch("summarize", Path.Combine(folder, "run"), Path.Combine(folder, "request.json"));
        if (expected["refused"] is not null)
        {
            Assert.True(code == 2, $"a request the Producer must refuse was summarized: {output}");  // §9.3: an input error
            return;
        }

        Assert.True(code == 0, error);

        var summary = JsonNode.Parse(output)!.AsObject();
        var want = expected["summary"]!;
        Assert.Equal(want["runId"]!.GetValue<string>(), summary["runId"]!.GetValue<string>());
        var lanes = summary["lanes"]!.AsArray();
        Assert.Equal(want["lanes"]!.AsArray().Select(l => l!["lane"]!.GetValue<string>()), lanes.Select(l => l!["lane"]!.GetValue<string>()));
        foreach (var (got, lane) in lanes.Zip(want["lanes"]!.AsArray()))
        {
            var entries = got!["metrics"]!.AsArray();
            Assert.Equal(lane!["metrics"]!.AsArray().Count, entries.Count);
            foreach (var (have, x) in entries.Zip(lane["metrics"]!.AsArray()))
            {
                foreach (var field in new[] { "metric", "path", "N", "n", "notMeasured", "verdict", "rule", "aggregate" })
                {
                    Assert.True(JsonNode.DeepEquals(have![field], x![field]) && have.AsObject().ContainsKey(field) == x.AsObject().ContainsKey(field),
                        $"{field}: {have[field]?.ToJsonString()} for {x[field]?.ToJsonString()}");
                }

                foreach (var field in new[] { "sum", "sumSq", "value" })
                {
                    var (g, w) = (have![field], x![field]);
                    Assert.True(w is null ? g is null : g is not null && AefSummaryCalculator.Matches(g.GetValue<double>(), w.GetValue<double>()),
                        $"{field}: {g?.ToJsonString()} for {w?.ToJsonString()}");
                }
            }
        }

        // §9.3: the run, with this summary.json added, verifies unsealed with no problem.
        using var copy = Copy(Path.Combine(folder, "run"));
        File.WriteAllBytes(Path.Combine(copy.Dir, "summary.json"), AefJsonWriter.Document(summary));
        var verification = AefRunVerifier.Verify(copy.Dir);
        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
        Assert.Empty(verification.Problems);
    }

    [Theory]
    [MemberData(nameof(SealWriteVectors))]
    public void SealWrite_WritesTheExpectedSeal_OrRefusesAndWritesNothing(string name)
    {
        var folder = Path.Combine(Vectors, "seal-write", name);
        var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;
        using var copy = Copy(Path.Combine(folder, "run"));
        var before = Files(copy.Dir);

        var (code, output, error) = Dispatch("seal-write", copy.Dir, "--sealed-by", expected["sealedBy"]!.GetValue<string>(), "--sealed-at", expected["sealedAt"]!.GetValue<string>());

        var after = Files(copy.Dir);
        if (expected["refused"] is not null)
        {
            Assert.Equal(2, code);
            Assert.Equal(before.Keys, after.Keys);
            return;
        }

        Assert.True(code == 0, error);
        var manifest = File.ReadAllBytes(Path.Combine(folder, expected["manifest"]!.GetValue<string>()));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant(), JsonNode.Parse(output)!["runHash"]!.GetValue<string>());
        Assert.Equal(before.Keys.Append("seal.json").Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, f => Assert.Equal(f.Value, after[f.Key]));

        var seal = JsonNode.Parse(after["seal.json"])!.AsObject();
        Assert.True(AefSchemas.Writer.IsValid("seal", seal));
        var want = Encoding.UTF8.GetString(manifest).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ", 3)).Select(p => (p[2], p[0]));
        Assert.Equal(want, seal["subject"]!.AsArray().Select(s => (s!["name"]!.GetValue<string>(), s["digest"]!["sha256"]!.GetValue<string>())));
        var predicate = seal["predicate"]!.AsObject();
        var wantPredicate = expected["predicate"]!.AsObject();
        Assert.Equal(wantPredicate.Select(m => m.Key).Order(StringComparer.Ordinal), predicate.Select(m => m.Key).Order(StringComparer.Ordinal));
        foreach (var (field, value) in wantPredicate)
        {
            Assert.True(field is "closedAt" or "sealedAt"
                    ? AefTime.Parse(predicate[field]!.GetValue<string>()) == AefTime.Parse(value!.GetValue<string>())
                    : JsonNode.DeepEquals(predicate[field], value),
                $"predicate.{field}: {predicate[field]?.ToJsonString()} for {value?.ToJsonString()}");
        }

        var verification = AefRunVerifier.Verify(copy.Dir);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
    }

    [Theory]
    [MemberData(nameof(SignVectors))]
    public void Sign_GivesAnEnvelopeThatVerifiesForTheKeysIdentity_AndRefusesAnEd25519Key(string name)
    {
        var folder = Path.Combine(Vectors, "sign", name);
        var expected = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "expected.json")))!;
        var file = Path.Combine(folder, expected["file"]!.GetValue<string>());
        var payloadType = expected["payloadType"]!.GetValue<string>();

        var (code, output, error) = Dispatch("sign", file, Path.Combine(folder, expected["key"]!.GetValue<string>()), "--payload-type", payloadType);

        if (expected["sig"] is not null)
        {
            // Ed25519: AgentEval signs with ECDSA P-256 only ([SIG-2]: a signer uses one of the two).
            Assert.Equal(2, code);
            Assert.Contains("algorithm not supported for signing", error, StringComparison.Ordinal);
            return;
        }

        Assert.True(code == 0, error);
        var envelope = JsonNode.Parse(output)!;
        var signatures = envelope["signatures"]!.AsArray();
        Assert.Single(signatures);
        Assert.Equal(expected["keyid"]!.GetValue<string>(), signatures[0]!["keyid"]!.GetValue<string>());
        foreach (var text in new[] { envelope["payload"]!.GetValue<string>(), signatures[0]!["sig"]!.GetValue<string>() })
        {
            Assert.Equal(text, Convert.ToBase64String(Convert.FromBase64String(text)));   // standard alphabet, padded ([SIG-1])
        }

        var policy = TrustPolicy.Parse(File.ReadAllBytes(Path.Combine(folder, expected["policy"]!.GetValue<string>())));
        var verification = DsseVerifier.Verify(Encoding.UTF8.GetBytes(output), File.ReadAllBytes(file), payloadType, policy);
        Assert.Equal([expected["identity"]!.GetValue<string>()], verification.VerifiesFor);
    }

    // ------------------------------------------------------------------ the driver's refusals

    [Fact]
    public void Summarize_RefusesAnUndeclaredMetric_ADuplicate_AndAProducersMethodWithoutItsValue()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var request = Path.Combine(run.Root, "request.json");

        foreach (var body in new[]
        {
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "undeclared", "path": "q"}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q"}, {"metric": "m", "path": "q"}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": []}, {"lane": "a", "metrics": []}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q", "aggregate": {"method": "f1"}}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q", "verdict": "excellent"}]}]}""",
            """{"lanes": [{"lane": "a", "metrics": [{"metric": "m", "path": "q", "value": 0.5}]}]}""",
            """{"lanes": "a"}""",
        })
        {
            File.WriteAllText(request, body);
            var (code, output, _) = Dispatch("summarize", run.Dir, request);
            Assert.True(code == 2, body);
            Assert.Empty(output);
        }
    }

    [Fact]
    public void Summarize_GivesWhatTheWriterWroteAtClose()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var request = Path.Combine(run.Root, "request.json");
        File.WriteAllText(request, """{"lanes": [{"lane": "main", "metrics": [{"metric": "m", "path": "q"}]}]}""");

        var (code, output, error) = Dispatch("summarize", run.Dir, request);

        Assert.True(code == 0, error);
        Assert.True(JsonNode.DeepEquals(run.Json("summary.json"), JsonNode.Parse(output)), output);
    }

    [Fact]
    public void SealWrite_RefusesASealedAtBeforeTheEnd_AtFullPrecision_AndAnOpenRun()
    {
        using var run = new WriterRun();
        run.WriteSmall();   // ended at 2026-10-01T10:05:00Z

        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "producer", "--sealed-at", "2026-10-01T10:04:59.999999999Z").Code);
        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "producer", "--sealed-at", "yesterday").Code);
        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "host", "--sealed-at", "2026-10-02T00:00:00Z").Code);
        Assert.Equal(2, Dispatch("seal-write", run.Dir, "--sealed-by", "producer").Code);
        Assert.False(File.Exists(run.Full("seal.json")));

        var (code, output, _) = Dispatch("seal-write", run.Dir, "--sealed-by", "producer", "--sealed-at", "2026-10-01T10:05:00.000000001Z");
        Assert.Equal(0, code);
        Assert.Equal(AefRunFolder.Open(run.Dir).ComputeRunHash(), JsonNode.Parse(output)!["runHash"]!.GetValue<string>());
        Assert.Equal("2026-10-01T10:05:00.000000001Z", run.Json("seal.json")["predicate"]!["sealedAt"]!.GetValue<string>());

        using var open = new WriterRun();
        open.Create();
        Assert.Equal(2, Dispatch("seal-write", open.Dir, "--sealed-by", "producer", "--sealed-at", "2026-10-02T00:00:00Z").Code);
        Assert.False(File.Exists(open.Full("seal.json")));
    }

    [Fact]
    public void Sign_NeedsAPayloadType_AndAPkcs8Key()
    {
        using var run = new WriterRun();
        Directory.CreateDirectory(run.Root);
        var file = Path.Combine(run.Root, "file.json");
        File.WriteAllText(file, "{}\n");
        var key = Path.Combine(run.Root, "key.pem");
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(key, ecdsa.ExportPkcs8PrivateKeyPem());
        }

        Assert.Equal(2, Dispatch("sign", file, key).Code);
        Assert.Equal(0, Dispatch("sign", file, key, "--payload-type", Dsse.InTotoPayloadType).Code);
        File.WriteAllText(key, "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n");
        Assert.Equal(2, Dispatch("sign", file, key, "--payload-type", Dsse.InTotoPayloadType).Code);
    }

    // ------------------------------------------------------------------ AefSummaryWriter

    [Fact]
    public void TheExactSum_SurvivesCancellation_Sum5()
    {
        var results = new[] { 1e20, 1, -1e20 }.Select((v, i) => Line($"k{i}", v)).ToList();

        var summary = AefSummaryWriter.Build("r", results, Kinds(), Request(new AefSummaryEntry { Metric = "m", Path = "q" }));

        var entry = summary["lanes"]![0]!["metrics"]![0]!;
        Assert.Equal(1.0, entry["sum"]!.GetValue<double>());   // 1e20 + 1 − 1e20 summed in order is 0
        Assert.Equal(1.0 / 3, entry["value"]!.GetValue<double>());
        Assert.Equal(2e40, entry["sumSq"]!.GetValue<double>());
    }

    [Fact]
    public void AProducersMethod_WithNothingMeasured_NeedsNoValue_AndAnAefMethod_TakesNone()
    {
        var results = new List<JsonObject> { Line("k1", null, "skipped") };
        var entry = new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("f1"), Decide = _ => throw new InvalidOperationException("not called") };

        var summary = AefSummaryWriter.Build("r", results, Kinds(), Request(entry));

        Assert.Null(summary["lanes"]![0]!["metrics"]![0]!["value"]);
        Assert.Equal("not_measured", summary["lanes"]![0]!["metrics"]![0]!["verdict"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => AefSummaryWriter.Build("r", [Line("k1", 0.5)], Kinds(), Request(new AefSummaryEntry
        {
            Metric = "m", Path = "q", Aggregate = new AefAggregate("median"), Decide = _ => new AefSummaryDecision(AefSummaryVerdict.Passed, Value: 1),
        })));
        Assert.Throws<ArgumentException>(() => AefSummaryWriter.Build("r", [], Kinds(), Request(new AefSummaryEntry { Metric = "nope", Path = "q" })));
    }

    // ------------------------------------------------------------------ AefSigningKey

    [Fact]
    public void AP256Key_InPkcs8_SignsUnderItsKeyId()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = AefSigningKey.FromPkcs8Pem(ecdsa.ExportPkcs8PrivateKeyPem());

        Assert.Equal(PublicKeyInfo.FromDer(ecdsa.ExportSubjectPublicKeyInfo()).KeyId, signer.KeyId);
        var envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, "x"u8, signer);
        Assert.Equal(["me"], DsseVerifier.Verify(envelope, "x"u8, Dsse.InTotoPayloadType, new TrustPolicy([new TrustedKey("me", signer.PublicKey)])).VerifiesFor);
    }

    [Fact]
    public void AnEd25519Key_IsNotSupportedForSigning_AndOtherKeysAreRefused()
    {
        // PKCS#8 for Ed25519 (RFC 8410): version 0, id-Ed25519, and the 32-byte seed in an OCTET STRING.
        var ed25519 = Convert.FromHexString("302e020100300506032b657004220420" + new string('1', 64));
        Assert.Throws<NotSupportedException>(() => AefSigningKey.FromPkcs8Pem(PemEncoding.WriteString("PRIVATE KEY", ed25519)));

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem(p384.ExportPkcs8PrivateKeyPem()));
        using var rsa = RSA.Create(2048);
        Assert.Throws<NotSupportedException>(() => AefSigningKey.FromPkcs8Pem(rsa.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem("not a key"));
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem(PemEncoding.WriteString("EC PRIVATE KEY", [0x30, 0x00])));
        Assert.Throws<ArgumentException>(() => AefSigningKey.FromPkcs8Pem(PemEncoding.WriteString("PRIVATE KEY", [0x30, 0x03, 0x02, 0x01])));
    }

    // ------------------------------------------------------------------ helpers

    private static (int Code, string Output, string Error) Dispatch(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = Driver.Dispatch(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static TheoryData<string> Names(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var folder in Directory.GetDirectories(Path.Combine(Vectors, kind)).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(folder));
        }

        return data;
    }

    private static WriterRun Copy(string source)
    {
        var copy = new WriterRun();
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy.Dir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Directory.CreateDirectory(copy.Dir);
        return copy;
    }

    private static Dictionary<string, byte[]> Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    private static JsonObject Line(string caseId, double? value, string state = "scored")
    {
        var line = new JsonObject { ["caseId"] = caseId, ["path"] = "q", ["state"] = state };
        if (value is { } v)
        {
            line["scores"] = new JsonArray(new JsonObject { ["metric"] = "m", ["value"] = v });
        }

        return line;
    }

    private static Dictionary<string, AefMetricKind> Kinds() => new(StringComparer.Ordinal) { ["m"] = AefMetricKind.Score };

    private static AefSummary Request(AefSummaryEntry entry) => new() { Lanes = [new AefSummaryLane("main", [entry])] };
}
