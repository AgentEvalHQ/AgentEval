using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Writing;

/// <summary>The sealer (spec 04 §4.1, §4.4): what seal.json and attestation.dsse.json hold, and what it refuses to seal.</summary>
public class AefSealerTests
{
    [Fact]
    public void SealJson_IsAnInTotoStatement_OneSubjectPerSealedFile_InTheManifestsByteOrder()
    {
        using var run = new WriterRun();
        var writer = run.Create();
        writer.AddResult(WriterRun.Leaf("k1"));
        foreach (var path in new[] { "a/b", "a.b", "Z", "a-b" })
        {
            writer.PutExtFile(path, Encoding.UTF8.GetBytes(path));
        }

        writer.Close(AefRunStatus.Completed, WriterRun.Start.AddMinutes(1));
        var result = AefSealer.Seal(run.Dir, WriterRun.Options());

        var seal = run.Json("seal.json");
        Assert.Equal(AefSealer.StatementType, seal["_type"]!.GetValue<string>());
        Assert.Equal(AefSealer.EvidencePredicateType, seal["predicateType"]!.GetValue<string>());
        var names = seal["subject"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["ext/Z", "ext/a-b", "ext/a.b", "ext/a/b", "metrics.json", "results.ndjson", "run.json", "summary.json"], names);   // [SEAL-3]'s example order
        var folder = AefRunFolder.Open(run.Dir);
        Assert.All(seal["subject"]!.AsArray(), s => Assert.Equal(folder.Sha256(s!["name"]!.GetValue<string>()), s!["digest"]!["sha256"]!.GetValue<string>()));
        Assert.Equal(folder.ComputeRunHash(), seal["predicate"]!["runHash"]!.GetValue<string>());
        Assert.Equal(result.RunHash, seal["predicate"]!["runHash"]!.GetValue<string>());
        Assert.Equal(result.Manifest.Text, folder.Manifest().Text);
        Assert.False(result.Signed);
        Assert.False(File.Exists(run.Full("attestation.dsse.json")));
    }

    [Fact]
    public void ThePredicate_IsAProjectionOfRunJson_Seal5()
    {
        using var run = new WriterRun();
        var header = WriterRun.Header() with
        {
            Producer = new AefProducer { Name = "p", Version = "2", Runtime = new AefRuntime { Name = ".NET" } },
            Deployment = new AefDeployment { Ref = "deployment:d", Environment = "prod" },
            Suite = new AefSuite { Ref = "suite:s", Version = "4", Digest = "sha256:" + new string('f', 64), Frozen = true },
            Judges = [new AefJudge { Model = "j1", Provider = "x", RubricDigest = "sha256:" + new string('1', 64) }, new AefJudge { Model = "j2", Mode = AefJudgeMode.Shadow }],
        };
        AefRunWriter.Create(run.Dir, header).Close(AefRunStatus.Completed, WriterRun.Start.AddSeconds(90.5));

        AefSealer.Seal(run.Dir, WriterRun.Options(by: AefSealedBy.Ingest, at: new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)));

        var predicate = run.Json("seal.json")["predicate"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"schemaVersion": "1.0", "runId": "run-w", "runHash": "HASH",
             "producer": {"name": "p", "version": "2"}, "subject": {"ref": "agent:t", "version": "1"},
             "deployment": {"ref": "deployment:d"},
             "suite": {"ref": "suite:s", "version": "4", "digest": "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"},
             "judges": [{"model": "j1", "rubricDigest": "sha256:1111111111111111111111111111111111111111111111111111111111111111"}, {"model": "j2"}],
             "closedAt": "2026-10-01T10:01:30.5Z", "sealedBy": "ingest", "sealedAt": "2026-10-02T00:00:00Z"}
            """.Replace("HASH", predicate["runHash"]!.GetValue<string>(), StringComparison.Ordinal)), predicate), predicate.ToJsonString());
        Assert.Empty(run.Problems());
    }

    [Fact]
    public void WithoutDeploymentSuiteOrJudges_ThePredicateHasNullNullAndAnEmptyList()
    {
        using var run = new WriterRun();
        run.WriteSmall();

        AefSealer.Seal(run.Dir, WriterRun.Options());

        var predicate = run.Json("seal.json")["predicate"]!.AsObject();
        Assert.Null(predicate["deployment"]);
        Assert.True(predicate.ContainsKey("deployment"));
        Assert.Null(predicate["suite"]);
        Assert.Empty(predicate["judges"]!.AsArray());
    }

    [Fact]
    public void ASignedSeal_VerifiesForTheSignersIdentity_AndForNoOther()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        using var alice = EcdsaP256Signer.Generate();
        using var mallory = EcdsaP256Signer.Generate();

        AefSealer.Seal(run.Dir, WriterRun.Options(alice));

        var envelope = run.Json("attestation.dsse.json");
        Assert.Equal(Dsse.InTotoPayloadType, envelope["payloadType"]!.GetValue<string>());
        Assert.Equal(run.Read("seal.json"), Convert.FromBase64String(envelope["payload"]!.GetValue<string>()));   // seal.json's exact bytes ([SIG-1])
        Assert.Equal(alice.KeyId, envelope["signatures"]![0]!["keyid"]!.GetValue<string>());
        Assert.Equal([WriterRun.Alice], run.Verify(WriterRun.Policy(alice)).SignedBy);
        Assert.Empty(run.Verify(WriterRun.Policy(mallory)).SignedBy!);
    }

    [Fact]
    public void AChangeAfterSealing_IsDetected_AndAResealWithoutTheKey_IsNotSignedByAnyone()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        using var alice = EcdsaP256Signer.Generate();
        AefSealer.Seal(run.Dir, WriterRun.Options(alice));
        var attestation = run.Read("attestation.dsse.json");
        var results = Encoding.UTF8.GetString(run.Read("results.ndjson"));
        File.WriteAllText(run.Full("results.ndjson"), results.Replace("\"state\":\"passed\"", "\"state\":\"failed\"", StringComparison.Ordinal));

        Assert.Equal(["results.ndjson digest"], run.Problems());

        // Anyone can re-seal ([SIG-7]); the old signature does not cover the new seal.
        File.Delete(run.Full("seal.json"));
        File.Delete(run.Full("attestation.dsse.json"));
        AefSealer.Seal(run.Dir, WriterRun.Options());
        File.WriteAllBytes(run.Full("attestation.dsse.json"), attestation);
        var verification = run.Verify(WriterRun.Policy(alice));
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.SignedBy!);
    }

    [Fact]
    public void TheSealer_SignsOnce_WithTheSignerItIsGiven()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var signer = new CountingSigner(EcdsaP256Signer.Generate());

        AefSealer.Seal(run.Dir, WriterRun.Options(signer));

        Assert.Equal(1, signer.Calls);
        Assert.Equal(["git:alice@example.com"], run.Verify(WriterRun.Policy(signer)).SignedBy);
    }

    [Fact]
    public void ARunningRun_IsNotSealed()
    {
        using var run = new WriterRun();
        run.Create().AddResult(WriterRun.Leaf("k1"));

        Assert.Throws<InvalidOperationException>(() => AefSealer.Seal(run.Dir, WriterRun.Options()));
        Assert.False(File.Exists(run.Full("seal.json")));
    }

    [Fact]
    public void ASealedRun_IsNotSealedAgain()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        AefSealer.Seal(run.Dir, WriterRun.Options());
        var seal = run.Read("seal.json");

        Assert.Throws<InvalidOperationException>(() => AefSealer.Seal(run.Dir, WriterRun.Options(at: WriterRun.Start.AddDays(1))));
        Assert.Equal(seal, run.Read("seal.json"));
    }

    [Fact]
    public void ASealedAtBeforeTheRunsEnd_IsRefused()
    {
        using var run = new WriterRun();
        run.WriteSmall();

        Assert.Throws<InvalidOperationException>(() => AefSealer.Seal(run.Dir, WriterRun.Options(at: WriterRun.Start.AddMinutes(4))));
        Assert.False(File.Exists(run.Full("seal.json")));
    }

    [Fact]
    public void AProducer_SealsOnlyARunWithoutProblems()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var summary = Encoding.UTF8.GetString(run.Read("summary.json")).Replace("\"N\": 2", "\"N\": 3", StringComparison.Ordinal);
        File.WriteAllText(run.Full("summary.json"), summary);

        var refused = Assert.Throws<AefWriteException>(() => AefSealer.Seal(run.Dir, WriterRun.Options()));

        Assert.Contains(refused.Problems, p => p is { Path: "summary.json", Code: "summary" });
        Assert.False(File.Exists(run.Full("seal.json")));
    }

    [Fact]
    public void AHost_SealsARunTheReaderSchemasAccept_EvenWithProblemsAcrossFiles_AndItsSealVerifies()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var summary = Encoding.UTF8.GetString(run.Read("summary.json")).Replace("\"N\": 2", "\"N\": 3", StringComparison.Ordinal);
        File.WriteAllText(run.Full("summary.json"), summary);

        AefSealer.Seal(run.Dir, WriterRun.Options(by: AefSealedBy.Ingest));

        Assert.Equal(["summary.json summary"], run.Problems());   // the seal itself verifies; the run is as it was received
        Assert.Equal("ingest", run.Json("seal.json")["predicate"]!["sealedBy"]!.GetValue<string>());
    }

    [Fact]
    public void AHost_DoesNotSealARunTheReaderSchemasRefuse_Seal1()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        File.WriteAllText(run.Full("results.ndjson"), "{\"schemaVersion\": \"1.0\"}\n");

        var refused = Assert.Throws<InvalidOperationException>(() => AefSealer.Seal(run.Dir, WriterRun.Options(by: AefSealedBy.Ingest)));

        Assert.Contains("results.ndjson:1 schema", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(run.Full("seal.json")));
    }

    [Theory]
    [InlineData(AefSealedBy.Producer)]
    [InlineData(AefSealedBy.Ingest)]
    public void NeitherSealsAPathItsSubjectsCannotName(AefSealedBy by)
    {
        using var run = new WriterRun();
        run.WriteSmall();
        Directory.CreateDirectory(run.Full("ext"));
        File.WriteAllText(run.Full("ext/.DS_Store"), "x");

        Assert.Throws<InvalidOperationException>(() => AefSealer.Seal(run.Dir, WriterRun.Options(by: by)));
        Assert.False(File.Exists(run.Full("seal.json")));
    }

    [Fact]
    public void AnUnsealedRunWithOverlays_KeepsItsChainWhenSealed()
    {
        using var run = new WriterRun();
        run.WriteSmall();
        var overlay = AefOverlayWriter.Open(run.Dir);
        overlay.Append(AefOverlayWriterTests.Approve(WriterRun.Alice));
        overlay.SealBatch();

        AefSealer.Seal(run.Dir, WriterRun.Options());

        Assert.Empty(run.Problems());
        Assert.Equal(AefOutcome.Intact, run.Verify().Outcome);
        var folder = AefRunFolder.Open(run.Dir);
        Assert.Empty(OverlayChain.Verify(folder, AefRunDocuments.Read(folder)).Problems);   // overlays are not sealed: the run hash is the same
        Assert.DoesNotContain(run.Json("seal.json")["subject"]!.AsArray(), s => s!["name"]!.GetValue<string>().StartsWith("overlays/", StringComparison.Ordinal));
    }

    private sealed class CountingSigner(EcdsaP256Signer inner) : IAefSigner, IDisposable
    {
        public int Calls { get; private set; }

        public PublicKeyInfo PublicKey => inner.PublicKey;

        public string KeyId => inner.KeyId;

        public byte[] Sign(ReadOnlySpan<byte> message)
        {
            Calls++;
            return inner.Sign(message);
        }

        public void Dispose() => inner.Dispose();
    }
}
