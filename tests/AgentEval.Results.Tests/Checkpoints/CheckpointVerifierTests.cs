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
        Assert.Equal(hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), verification.Anchors);   // each once, in byte order (spec 09 §9.3)
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

    [Theory]
    [InlineData("minimumShare", 0.9)]   // a member a later minor added
    [InlineData("minimumN", 0.5)]       // a value the writer schema refuses (an integer field)
    public void ALaneWhoseRuleIsNotValidAgainstTheWriterSchema_IsUnverifiable(string member, double value)
    {
        // [CKP-8] (round 4): any rule this version's writer schema refuses, not only four fields of it.
        var manifest = Corpus("threshold");
        manifest["lanes"]!.AsArray().Single(l => (string?)l!["lane"] == "pass")!["rule"]![member] = value;

        var (_, problems) = CheckpointVerifier.Lanes(manifest, AefRunStore.Open(CorpusRuns("threshold")), At);

        Assert.Equal([new AefProblem("lanes/pass", "unverifiable")], problems);
    }

    [Theory]
    [InlineData("target-mode")]           // an unknown execution.targetMode in a run of the lane
    [InlineData("severity")]              // an unknown severity on a severity lane's line
    [InlineData("severity-passed")]       // on a line of any state: the text names its lines, not its failures
    [InlineData("severity-trial")]        // trial lines included
    [InlineData("direction")]             // an unknown direction of the compared metric, in the candidate
    [InlineData("baseline-direction")]    // or in the baseline, a found run the lane reads
    [InlineData("baseline-target-mode")]
    public void ALaneThatReadsAValueThisVersionDoesNotKnowInARun_IsUnverifiable_NotCompared(string what)
    {
        // [CKP-8] (round 4): recomputing the lane reads something this version does not know; whatever was recorded,
        // the lane is unverifiable, never lane-result.
        var run = new LaneRunBuilder("R");
        JsonObject rule = Threshold();
        var lane = (Runs: new List<string> { "R" }, Baseline: (LaneRunBuilder?)null);
        switch (what)
        {
            case "target-mode":
                run.Score("c1", 0.9).Entry("quality", "m", "p").Run["execution"]!["targetMode"] = "live-shadow";
                break;
            case "severity" or "severity-passed" or "severity-trial":
                rule = new JsonObject { ["kind"] = "severity", ["max"] = "low", ["path"] = "a" };
                if (what == "severity-trial")
                {
                    run.Line("c1", "a", "failed", severity: "info", trial: 0).Line("c1", "a", "failed", severity: "low", rollup: (1, 0));
                }
                else
                {
                    run.Line("c1", "a", what == "severity" ? "failed" : "passed", severity: "info");
                }

                break;
            default:
                var baseline = new LaneRunBuilder("B", "v6");
                for (var i = 0; i < 20; i++)
                {
                    baseline.Score($"k{i}", 0.5, path: "mem");
                    run.Score($"k{i}", 0.6, path: "mem");
                }

                var unknown = what.StartsWith("baseline", StringComparison.Ordinal) ? baseline : run;
                if (what.EndsWith("direction", StringComparison.Ordinal))
                {
                    unknown.Metrics["metrics"]![0]!["direction"] = "target_band";
                }
                else
                {
                    unknown.Run["execution"]!["targetMode"] = "live-shadow";
                }

                lane.Baseline = baseline;
                break;
        }

        var hash = LaneRunBuilder.RunHashOf(run.Write(_root));
        if (lane.Baseline is { } b)
        {
            rule = new JsonObject
            {
                ["kind"] = "comparison", ["lane"] = "quality", ["metric"] = "m", ["path"] = "mem",
                ["baseline"] = new JsonObject { ["runId"] = "B", ["runHash"] = LaneRunBuilder.RunHashOf(b.Write(_root)) },
                ["significance"] = 0.05, ["minimumPairs"] = 10, ["axes"] = new JsonArray("subject"),
            };
        }

        var manifest = DecidedOneLane(rule, ("R", hash), new JsonObject { ["status"] = "passed", ["subjectVersion"] = "v7", ["oldestClosedAt"] = At });

        var (_, problems) = CheckpointVerifier.Lanes(manifest, AefRunStore.Open(_root), At);

        Assert.Equal([new AefProblem("lanes/l", "unverifiable")], problems);
    }

    [Fact]
    public void AnUnknownSeverityOutsideASeverityLanesScope_OrInAnotherKindOfLane_IsNotRead()
    {
        // [CKP-8]: only the lane's lines (its summary lane and path) are read for their severity, and only by a severity lane.
        var run = new LaneRunBuilder("R").Line("c1", "a", "passed").Line("c2", "b", "failed", severity: "info").Score("c3", 0.9).Entry("quality", "m", "p");
        var hash = LaneRunBuilder.RunHashOf(run.Write(_root));
        var recorded = new JsonObject { ["status"] = "passed", ["subjectVersion"] = "v7", ["oldestClosedAt"] = "2026-10-05T00:00:00Z" };

        var severity = DecidedOneLane(new JsonObject { ["kind"] = "severity", ["max"] = "low", ["path"] = "a" }, ("R", hash), recorded);
        Assert.Empty(CheckpointVerifier.Lanes(severity, AefRunStore.Open(_root), At).Problems);

        var threshold = DecidedOneLane(Threshold(), ("R", hash), recorded.DeepClone().AsObject());
        Assert.Empty(CheckpointVerifier.Lanes(threshold, AefRunStore.Open(_root), At).Problems);
    }

    // A decided checkpoint of one lane "l" over one run, with this recorded result (the decision is not recomputed here).
    private static JsonObject DecidedOneLane(JsonObject rule, (string RunId, string RunHash) run, JsonObject recorded) => new()
    {
        ["schemaVersion"] = "1.0", ["checkpointId"] = "cp_1",
        ["subject"] = new JsonObject { ["ref"] = LaneRunBuilder.Subject, ["version"] = "v7" },
        ["lanes"] = new JsonArray(Lane("l", rule, run)),
        ["state"] = "decided", ["outcome"] = "approved",
        ["decisionInput"] = new JsonObject
        {
            ["subjectVersion"] = "v7", ["evaluatedAt"] = At,
            ["lanes"] = new JsonArray(new JsonObject { ["lane"] = "l", ["blocking"] = true, ["result"] = recorded, ["evidence"] = new JsonArray(run.RunHash) }),
        },
    };

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
