using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Integrity;

/// <summary>§4.1: each code of [SEAL-6], and "the run's run hash" of [SEAL-4].</summary>
public class SealVerifierTests
{
    [Fact]
    public void ASealOverTheFiles_Verifies_AndTheRunIsIntact()
    {
        using var run = new TestRun().Write().Seal();

        var verification = run.Verify();

        Assert.Empty(verification.Problems);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Equal(new AefRunHash(AefRunFolder.Open(run.Dir).ComputeRunHash(), true), verification.RunHash);
    }

    [Fact]
    public void TheRunsRunHash_IsTheSealedValue_WhenSealJsonIsValidAgainstTheReaderSchema_EvenWhenTheFilesChanged()
    {
        using var run = new TestRun().Write().Seal();
        var sealedHash = AefRunFolder.Open(run.Dir).ComputeRunHash();
        run.WriteText("ext/added", "x");

        var runHash = SealVerifier.RunHashOf(AefRunFolder.Open(run.Dir));

        Assert.Equal(new AefRunHash(sealedHash, true), runHash);
        Assert.NotEqual(sealedHash, AefRunFolder.Open(run.Dir).ComputeRunHash());
    }

    [Theory]
    [InlineData("sealedAt")]     // a required member of the predicate
    [InlineData("runHash")]
    public void TheRunsRunHash_IsRecomputed_WhenSealJsonIsNotValidAgainstTheReaderSchema(string removed)
    {
        using var run = new TestRun().Write().Seal(s => s["predicate"]!.AsObject().Remove(removed));

        var runHash = SealVerifier.RunHashOf(AefRunFolder.Open(run.Dir));

        // seal.json is not a sealed file: the recomputed hash is the same with or without it.
        Assert.Equal(new AefRunHash(AefRunFolder.Open(run.Dir).ComputeRunHash(), false), runHash);
        Assert.Equal(["seal.json seal-invalid"], run.Problems());
    }

    [Fact]
    public void TheRunsRunHash_IsRecomputed_WithoutASeal()
    {
        using var run = new TestRun().Write();

        Assert.Equal(new AefRunHash(AefRunFolder.Open(run.Dir).ComputeRunHash(), false), SealVerifier.RunHashOf(AefRunFolder.Open(run.Dir)));
    }

    [Fact]
    public void SealInvalid_NotIJson_StopsTheVerification()
    {
        using var run = new TestRun().Write();
        run.WriteText("seal.json", """{"_type": "a", "_type": "b"}""");
        run.WriteText("notes.txt", "not sealed, but nothing more is checked");

        Assert.Equal(["seal.json seal-invalid"], run.Problems());
    }

    [Fact]
    public void Limit_ASealNestedDeeperThan64_IsNotRead_SoTheRunsRunHashIsRecomputed()
    {
        using var run = new TestRun().Write().Seal();
        var seal = Encoding.UTF8.GetString(run.ReadBytes("seal.json"));
        run.WriteText("seal.json", seal.Replace("\"predicate\": {", "\"predicate\": {\"deep\": " + OverlayChainTests.Deep(63) + ",", StringComparison.Ordinal));

        Assert.Equal(["seal.json limit"], run.Problems());
        Assert.False(SealVerifier.RunHashOf(AefRunFolder.Open(run.Dir)).Sealed);
    }

    [Fact]
    public void ASealNested64Deep_IsWithinTheLimit()
    {
        using var run = new TestRun().Write().Seal();
        var seal = Encoding.UTF8.GetString(run.ReadBytes("seal.json"));
        run.WriteText("seal.json", seal.Replace("\"predicate\": {", "\"predicate\": {\"deep\": " + OverlayChainTests.Deep(62) + ",", StringComparison.Ordinal));

        Assert.Empty(run.Problems());   // the reader schema ignores a member it does not know
    }

    [Fact]
    public void DuplicateSubject_ItsDigestsAreNotCompared()
    {
        using var run = new TestRun().Write();
        run.Seal(s => s["subject"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "metrics.json",
            ["digest"] = new JsonObject { ["sha256"] = new string('0', 64) },
        }));

        Assert.Equal(["metrics.json duplicate-subject"], run.Problems());
    }

    [Fact]
    public void SubjectPath_ASubjectThatNamesTheSealItsSignatureOrAnOverlay()
    {
        using var run = new TestRun().Write();
        run.Seal(s =>
        {
            foreach (var name in new[] { "seal.json", "attestation.dsse.json", "overlays/events.ndjson" })
            {
                s["subject"]!.AsArray().Add(new JsonObject { ["name"] = name, ["digest"] = new JsonObject { ["sha256"] = new string('0', 64) } });
            }
        });

        Assert.Equal(
            ["attestation.dsse.json subject-path", "overlays/events.ndjson subject-path", "seal.json subject-path"],
            run.Problems());
    }

    [Fact]
    public void Digest_ASealedFileWhoseBytesChanged()
    {
        using var run = new TestRun().Write().Seal();
        var metrics = run.ReadBytes("metrics.json");
        run.WriteBytes("metrics.json", [.. metrics[..^1], (byte)' ', (byte)'\n']);   // the same JSON, other bytes

        Assert.Equal(["metrics.json digest"], run.Problems());
    }

    [Fact]
    public void NotSealed_AFilePresentButNotSealed_AndAnOverlayNeverIs()
    {
        using var run = new TestRun().Write().Seal();
        run.WriteText("ext/later", "x");
        run.AppendBatch([TestRun.Event("ov_1", "annotate", new JsonObject())]);

        Assert.Equal(["ext/later not-sealed"], run.Problems());
    }

    [Fact]
    public void Missing_ASealedFileThatIsGone_AndNoRunHashCheck()
    {
        using var run = new TestRun().Write().Seal();
        run.Delete("summary.json");

        // A closed run without summary.json is also a schema problem (§3.9).
        Assert.Equal(["summary.json missing", "summary.json schema"], run.Problems());
    }

    [Fact]
    public void RunHash_EveryFileMatches_ButTheSealedRunHashIsAnother()
    {
        using var run = new TestRun().Write().Seal(s => s["predicate"]!["runHash"] = new string('a', 64));

        Assert.Equal(["seal.json run-hash"], run.Problems());
    }

    [Fact]
    public void RunId_ThePredicatesRunIdIsNotRunJsons()
    {
        using var run = new TestRun().Write().Seal(s => s["predicate"]!["runId"] = "another-run");

        Assert.Equal(["seal.json run-id"], run.Problems());
    }

    [Theory]
    [InlineData("producer.version", "\"2.0\"")]
    [InlineData("subject.version", "\"2\"")]
    [InlineData("deployment", """{"ref": "env:prod"}""")]
    [InlineData("suite", """{"ref": "suite:a", "version": "1"}""")]
    [InlineData("judges", """[{"model": "judge-1"}]""")]
    [InlineData("closedAt", "\"2026-10-01T10:05:01Z\"")]
    public void Predicate_DiffersFromRunJson(string field, string value)
    {
        using var run = new TestRun().Write().Seal(s =>
        {
            var steps = field.Split('.');
            var holder = s["predicate"]!;
            foreach (var step in steps[..^1])
            {
                holder = holder[step]!;
            }

            holder[steps[^1]] = JsonNode.Parse(value);
        });

        Assert.Equal(["seal.json predicate"], run.Problems());
    }

    [Fact]
    public void Predicate_TimesCompareAsTimes_AndNullOrEmptyEqualsAbsence()
    {
        // run.json has no deployment, suite or judges; the predicate says null, null and [].
        using var run = new TestRun().Write().Seal(s => s["predicate"]!["closedAt"] = "2026-10-01T10:05:00.000Z");

        Assert.Empty(run.Problems());
    }

    [Theory]
    [InlineData("2026-10-01T10:04:59.999999999Z", true)]   // a nanosecond before the run closed
    [InlineData("2026-10-01T10:05:00.000Z", false)]        // the same time, another form
    public void Predicate_ASealMadeBeforeTheRunClosed(string sealedAt, bool problem)
    {
        using var run = new TestRun().Write().Seal(s => s["predicate"]!["sealedAt"] = sealedAt);

        Assert.Equal(problem ? new[] { "seal.json predicate" } : Array.Empty<string>(), run.Problems());
    }

    [Fact]
    public void RunOpen_ASealedRunThatSaysRunning()
    {
        using var run = new TestRun();
        run.Run["status"] = "running";
        run.Run.Remove("endedAt");
        run.Summary = null;
        run.Write().Seal(s => s["predicate"]!["closedAt"] = "2026-10-01T10:05:00Z");

        // closedAt is no longer run.json's endedAt (it has none).
        Assert.Equal(["run.json run-open", "seal.json predicate"], run.Problems());
    }

    [Fact]
    public void Withheld_IsNotAProblemForTheOutcome_AndMissingIs()
    {
        using var signer = EcdsaP256Signer.Generate();
        using var run = new TestRun().Write().Seal();
        run.AppendBatch([TestRun.Event("ov_1", "redact", new JsonObject { ["blob"] = TestRun.ReasoningSha }, edit: e => e["reason"] = "personal data")], signer);
        run.Delete(TestRun.ReasoningPath);

        var authorized = run.Verify(TestRun.Policy(signer, redact: true));
        Assert.Equal(AefOutcome.Intact, authorized.Outcome);
        Assert.Equal([$"{TestRun.ReasoningPath} withheld"], authorized.Problems.Select(p => $"{p.Path} {p.Code}"));
        Assert.Equal(1, authorized.Withheld);

        var unauthorized = run.Verify();
        Assert.Equal(AefOutcome.Invalid, unauthorized.Outcome);
        Assert.Equal([$"{TestRun.ReasoningPath} missing", "evidence.ndjson:1 blob", "results.ndjson:3 blob"], unauthorized.Problems.Select(p => $"{p.Path} {p.Code}"));
        Assert.Equal(0, unauthorized.Withheld);
    }

    [Fact]
    public void TheSealOperation_ReportsOnlySeal6sProblems_AndTheManifestOfTheFilesPresent()
    {
        using var run = new TestRun().Write().Seal();
        run.Results[1]["parentResultId"] = "r_00000000000000000000000000000000";
        run.Write();   // results.ndjson changed after sealing; also a parent problem, which is §3.9's

        var stdout = new StringWriter();
        var code = AgentEval.Results.Conformance.Program.Dispatch(["seal", run.Dir], stdout, new StringWriter());
        var output = JsonNode.Parse(stdout.ToString())!;

        Assert.Equal(0, code);
        Assert.Equal("""[["results.ndjson","digest"]]""", output["problems"]!.ToJsonString());
        Assert.Equal(AefRunFolder.Open(run.Dir).Manifest().Text, (string?)output["manifest"]);
        Assert.Equal(AefRunFolder.Open(run.Dir).ComputeRunHash(), (string?)output["runHash"]);
        Assert.Contains("  results.ndjson\n", (string)output["manifest"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("seal.json", Encoding.UTF8.GetString(AefRunFolder.Open(run.Dir).Manifest().Bytes), StringComparison.Ordinal);
    }
}
