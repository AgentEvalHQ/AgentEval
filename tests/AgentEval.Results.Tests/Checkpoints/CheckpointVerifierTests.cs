using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Json;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Checkpoints;

/// <summary>
/// §5.5: the checkpoint verifier against the runs ([CKP-8]: found, intact, each lane recomputed and compared with the
/// recorded input) and its signature ([CKP-9]: a verified checkpoint with no problem anchors its runs), and the manifest
/// rules ([CKP-7]) this package changed.
/// </summary>
public sealed class CheckpointVerifierTests : IDisposable
{
    private const string At = "2026-10-08T00:00:00Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aef-checkpoint-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ADecidedCheckpoint_WhoseRecordedResultsAreRecomputed_HasNoProblem_AndASignedOneAnchorsItsRuns()
    {
        var (manifest, hashes) = Decided();
        using var signer = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy([new TrustedKey("git:alice@example.com", signer.PublicKey, null)]);
        var envelope = DsseEnvelope.Create(Dsse.CheckpointPayloadType, manifest, signer).ToJson();

        var verification = CheckpointVerifier.Verify(manifest, AefRunStore.Open(_root), new CheckpointVerifyOptions { At = At, Policy = policy, Envelope = envelope });

        Assert.Empty(verification.ManifestProblems);
        Assert.Empty(verification.Problems);
        Assert.Equal(["git:alice@example.com"], verification.SignedBy);
        Assert.Equal(hashes.Order(StringComparer.Ordinal), verification.Anchors.Order(StringComparer.Ordinal));
        Assert.Equal(["quality", "regression"], verification.Lanes.Select(l => l.Lane));
    }

    [Fact]
    public void ACheckpointWithAProblem_OrSignedByNoTrustedKey_AnchorsNothing()
    {
        var (manifest, _) = Decided();
        using var signer = EcdsaP256Signer.Generate();
        using var stranger = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy([new TrustedKey("git:alice@example.com", signer.PublicKey, null)]);

        var untrusted = CheckpointVerifier.Verify(manifest, AefRunStore.Open(_root),
            new CheckpointVerifyOptions { At = At, Policy = policy, Envelope = DsseEnvelope.Create(Dsse.CheckpointPayloadType, manifest, stranger).ToJson() });
        Assert.Empty(untrusted.SignedBy);
        Assert.Empty(untrusted.Anchors);

        // The payload type is the checkpoint's ([SIG-1]): an in-toto envelope over the same bytes is a mismatch.
        var wrongType = CheckpointVerifier.Verify(manifest, AefRunStore.Open(_root),
            new CheckpointVerifyOptions { At = At, Policy = policy, Envelope = DsseEnvelope.Create(Dsse.InTotoPayloadType, manifest, signer).ToJson() });
        Assert.Empty(wrongType.Anchors);

        Directory.Delete(Path.Combine(_root, "C"), recursive: true);   // the candidate is gone
        var missing = CheckpointVerifier.Verify(manifest, AefRunStore.Open(_root),
            new CheckpointVerifyOptions { At = At, Policy = policy, Envelope = DsseEnvelope.Create(Dsse.CheckpointPayloadType, manifest, signer).ToJson() });
        Assert.Contains(new AefProblem("lanes/regression/runs/C", "run-missing"), missing.Problems);
        Assert.Equal(["git:alice@example.com"], missing.SignedBy);
        Assert.Empty(missing.Anchors);

        var unsigned = CheckpointVerifier.Verify(manifest, AefRunStore.Open(_root), new CheckpointVerifyOptions { At = At });
        Assert.Null(unsigned.Signature);
        Assert.Empty(unsigned.Anchors);
    }

    [Theory]
    [InlineData("2026-10-01T12:00:00.000Z", null)]                 // the same time, written otherwise ([ENC-8])
    [InlineData("2026-10-01T12:00:00.000000001Z", "oldest-closed")]   // a nanosecond later
    [InlineData("2026-10-01T11:59:59.999999999Z", "oldest-closed")]
    public void ARecordedAge_IsComparedAsATime_AtFullPrecision(string recorded, string? problem)
    {
        var manifest = Corpus("threshold");
        Lane(manifest, "pass")["result"]!["oldestClosedAt"] = recorded;

        var (_, problems) = CheckpointVerifier.Lanes(manifest, AefRunStore.Open(CorpusRuns("threshold")), At);

        Assert.Equal(problem is null ? [] : [new AefProblem("lanes/pass", problem)], problems);
    }

    [Fact]
    public void ARecordedResult_ThatDiffersInStatusVersionAxesOrPresence_IsReportedAtItsLane()
    {
        var manifest = Corpus("threshold");
        Lane(manifest, "pass")["result"]!["status"] = "failed";
        Lane(manifest, "le")["result"]!["subjectVersion"] = "v6";
        Lane(manifest, "lt")["result"]!["axes"] = new JsonArray("judges");
        Lane(manifest, "no-runs")["result"] = new JsonObject { ["status"] = "passed", ["subjectVersion"] = "v7", ["oldestClosedAt"] = At };
        Lane(manifest, "no-entry")["result"] = null;

        var (_, problems) = CheckpointVerifier.Lanes(manifest, AefRunStore.Open(CorpusRuns("threshold")), At);

        Assert.Equal(
            ["lanes/le lane-version", "lanes/lt lane-result", "lanes/no-entry lane-result", "lanes/no-runs lane-result", "lanes/pass lane-result"],
            problems.Select(p => $"{p.Path} {p.Code}"));
    }

    [Theory]
    [InlineData("le", "op", "=>")]
    [InlineData("lt", "kind", "bayesian")]
    public void ALaneWhoseRuleHoldsAValueThisVersionDoesNotKnow_IsUnverifiable_NotCompared(string lane, string field, string value)
    {
        // R3-2: a later minor recorded a result this version cannot recompute; the lane's result is still read (§7.3).
        var manifest = Corpus("threshold");
        manifest["lanes"]!.AsArray().Single(l => (string?)l!["lane"] == lane)!["rule"]![field] = value;
        Lane(manifest, lane)["result"]!["status"] = "passed";

        var (lanes, problems) = CheckpointVerifier.Lanes(manifest, AefRunStore.Open(CorpusRuns("threshold")), At);

        Assert.Equal([new AefProblem($"lanes/{lane}", "unverifiable")], problems);
        Assert.Equal(LaneEvidenceStatus.NotMeasured, lanes.Single(l => l.Lane == lane).Result!.Status);
    }

    [Fact]
    public void AnUndecidedCheckpoint_IsNotComparedWithARecordedInput_AndTakesTheGivenTimeForItsAge()
    {
        var running = new LaneRunBuilder("R").Score("c1", 0.9);
        running.Run["status"] = "running";
        running.Run.Remove("endedAt");
        var dir = running.Write(_root, seal: false);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0", ["checkpointId"] = "cp_1",
            ["subject"] = new JsonObject { ["ref"] = LaneRunBuilder.Subject, ["version"] = "v7" },
            ["lanes"] = new JsonArray(Lane("quality", Threshold(), ("R", LaneRunBuilder.RunHashOf(dir)))),
            ["state"] = "running", ["outcome"] = null,
        };

        var (lanes, problems) = CheckpointVerifier.Lanes(manifest, AefRunStore.Open(_root), "2026-10-09T08:00:00.25Z");

        Assert.Equal("2026-10-09T08:00:00.25Z", lanes.Single().Result!.OldestClosedAt);
        Assert.Equal([new AefProblem("lanes/quality/runs/R", "run-unverified")], problems);
    }

    [Fact]
    public void AManifestLaneWithRuns_ThatTheInputLeavesOut_HasNoResultThere_AndIsAnEvidenceProblem()
    {
        // [CKP-7] evidence: "a lane has runs but no result in the input" (W4-3).
        var document = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "checkpoints", "valid-decided", "document.json")))!.AsObject();
        var input = document["decisionInput"]!["lanes"]!.AsArray();
        var withRuns = NameOf(document["lanes"]!.AsArray().First(l => l!["runs"]!.AsArray().Count > 0)!);
        input.Remove(input.First(l => (string?)l!["lane"] == withRuns)!);

        Assert.Contains("evidence", CheckpointManifest.Verify(document));
        Assert.Contains("lanes", CheckpointManifest.Verify(document));
    }

    [Fact]
    public void AManifestTheReaderRefuses_IsRefused()
    {
        Assert.Throws<FormatException>(() => CheckpointVerifier.Read("""{"schemaVersion": "1.0"}"""u8));
        Assert.Throws<FormatException>(() => CheckpointVerifier.Read("""{"a": 1, "a": 2}"""u8));
    }

    // A decided checkpoint over two runs written here: a threshold lane and a comparison lane, its input and decision
    // recomputed, as a producer records them. Returns its bytes and the run hashes it names (the baseline included).
    private (byte[] Manifest, List<string> Hashes) Decided()
    {
        var q = new LaneRunBuilder("Q").Score("c1", 0.9).Entry("quality", "m", "p").Write(_root);
        var baseline = new LaneRunBuilder("B", "v6");
        var candidate = new LaneRunBuilder("C");
        for (var i = 0; i < 20; i++)
        {
            baseline.Score($"k{i}", 0.5, path: "mem");
            candidate.Score($"k{i}", 0.6, path: "mem");
        }

        var b = baseline.Entry("quality", "m", "mem").Write(_root);
        var c = candidate.Entry("quality", "m", "mem").Write(_root);
        var (qh, bh, ch) = (LaneRunBuilder.RunHashOf(q), LaneRunBuilder.RunHashOf(b), LaneRunBuilder.RunHashOf(c));

        var comparison = new JsonObject
        {
            ["kind"] = "comparison", ["lane"] = "quality", ["metric"] = "m", ["path"] = "mem",
            ["baseline"] = new JsonObject { ["runId"] = "B", ["runHash"] = bh },
            ["significance"] = 0.05, ["minimumPairs"] = 10, ["axes"] = new JsonArray("subject", "judges"),
        };
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0", ["checkpointId"] = "cp_signed",
            ["subject"] = new JsonObject { ["ref"] = LaneRunBuilder.Subject, ["version"] = "v7" },
            ["lanes"] = new JsonArray(Lane("quality", Threshold(), ("Q", qh)), Lane("regression", comparison, ("C", ch))),
            ["state"] = "evidence_complete", ["outcome"] = null,
        };

        var (lanes, problems) = CheckpointVerifier.Lanes(manifest, AefRunStore.Open(_root), At);
        Assert.Empty(problems);
        var input = new JsonObject
        {
            ["subjectVersion"] = "v7", ["evaluatedAt"] = At,
            ["lanes"] = new JsonArray([.. lanes.Select((l, i) => (JsonNode?)new JsonObject
            {
                ["lane"] = l.Lane, ["blocking"] = true, ["result"] = l.Result!.ToJson(),
                ["evidence"] = new JsonArray(i == 0 ? qh : ch),
            })]),
        };
        var decision = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(input)));
        manifest["state"] = "decided";
        manifest["outcome"] = decision["outcome"]!.DeepClone();
        manifest["decisionInput"] = input;
        manifest["decision"] = decision;
        Assert.Equal("approved", (string?)manifest["outcome"]);
        return (AefJsonWriter.Document(manifest), [qh, ch, bh]);
    }

    private static JsonObject Threshold() => new()
    {
        ["kind"] = "threshold", ["lane"] = "quality", ["metric"] = "m", ["path"] = "p", ["op"] = ">=", ["value"] = 0.5,
    };

    private static JsonObject Lane(string name, JsonObject rule, params (string RunId, string RunHash)[] runs) => new()
    {
        ["lane"] = name,
        ["rule"] = rule,
        ["runs"] = new JsonArray([.. runs.Select(r => (JsonNode?)new JsonObject { ["runId"] = r.RunId, ["runHash"] = r.RunHash, ["origin"] = "launched" })]),
        ["blocking"] = true,
    };

    // A lane of a corpus manifest's recorded input.
    private static JsonObject Lane(JsonObject manifest, string name) =>
        manifest["decisionInput"]!["lanes"]!.AsArray().Single(l => (string?)l!["lane"] == name)!.AsObject();

    private static JsonObject Corpus(string vector) =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "lane-vectors", vector, "checkpoint.json")))!.AsObject();

    private static string CorpusRuns(string vector) => Path.Combine(AefCorpus.Conformance, "lane-vectors", vector, "runs");

    private static string NameOf(JsonNode lane) => (string)lane["lane"]!;
}
