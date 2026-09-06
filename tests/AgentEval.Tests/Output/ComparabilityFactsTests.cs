// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// ADR-031 finding <b>V1</b> — the five comparability facts a run must carry besides the stimulus:
/// the eval's key, its version, the effective bar, the chance floor and the judge fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>prerequisite</b> for S5 (<c>agenteval compare</c>), not S5. S5's acceptance was
/// refuted by execution: a real run directory carried five of the six facts nowhere, so a
/// <c>compare</c> written to it would have exited 13 on every pair of runs in this repository. These
/// tests are about the facts being <i>recorded</i>, and every one of them is wired to a rule that
/// fails in the flattering direction if it is got wrong:
/// </para>
/// <list type="bullet">
/// <item>a null bar read as 0.0 — "everything passes";</item>
/// <item>an absent floor read as a zero floor — how a metric gets condemned at p = 0.70;</item>
/// <item>"nobody said which model judged" read as "a different model judged";</item>
/// <item>a new member silently rewriting every stored scenario file;</item>
/// <item>and a judge fingerprint that quietly carries the endpoint it was resolved from.</item>
/// </list>
/// </remarks>
public class ComparabilityFactsTests
{
    // The store's own serializer settings. The byte-identity claim is only true under these, so it
    // is asserted under these — AND against a file the real store wrote, because a copy of the
    // settings is an input the artifact under test supplies to its own pass/fail.
    // ⚠ The JsonStringEnumConverter is not decoration. It was MISSING from the first draft of this
    //   copy, and the round-trip test below caught it by failing to read a file the real store had
    //   just written: the store writes FloorState as "notDerivable", a hand-rolled copy without the
    //   converter reads it as a number and throws. That is the copy-of-the-settings hazard firing
    //   for real — which is why the byte claims below are ALSO made against a file the store wrote.
    private static readonly JsonSerializerOptions s_storeLike = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // ── the record itself: an absence is never a number ─────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Facts_RefuseABlankEvalKey(string key)
    {
        // A run that cannot say which eval produced it cannot be compared with anything, and a
        // blank key would compare EQUAL to another blank key — the flattering direction.
        Assert.Throws<ArgumentException>(() => new ComparabilityFacts(key, "1.0.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Facts_RefuseABlankEvalVersion(string version)
    {
        Assert.Throws<ArgumentException>(() => new ComparabilityFacts("some.eval", version));
    }

    [Fact]
    public void Facts_RefuseABlankKeyOnACopyToo()
    {
        var facts = new ComparabilityFacts("some.eval", "1.0.0");

        // The AE-01 / AE-08 pattern: a guard that only runs in the constructor is bypassed by `with`.
        Assert.Throws<ArgumentException>(() => facts with { EvalKey = " " });
        Assert.Throws<ArgumentException>(() => facts with { EvalVersion = "" });
    }

    [Fact]
    public void Facts_EffectiveBarIsNullWhenNoneWasDeclared_NotZero()
    {
        var facts = new ComparabilityFacts("some.eval", "1.0.0");

        Assert.Null(facts.EffectiveBar);

        // 0.0 is a REAL bar meaning "everything clears". Reading "no bar" as 0.0 would make two
        // runs comparable that were held to different standards.
        Assert.NotEqual(0.0, facts.EffectiveBar ?? -1.0);
    }

    // ── the floor projection: the meta type cannot be serialised ────────────────────────────

    [Fact]
    public void TheMetaFloorTypeReallyDoesThrow_WhichIsWhyTheProjectionExists()
    {
        // The hazard this projection exists for is asserted, not asserted-about. Without this, the
        // next reader has no way to tell whether RecordedChanceFloor is solving a real problem.
        var undefined = ChanceFloor.NotDerivable("no pool to draw from");

        Assert.Throws<InvalidOperationException>(() => undefined.Value);
        Assert.Throws<InvalidOperationException>(() => undefined.ComparisonBar);
    }

    [Fact]
    public void RecordedFloor_ProjectsAnUndefinedFloorWithoutThrowing_AndWithoutAZero()
    {
        var undefined = ChanceFloor.NotDerivable("no pool to draw from");

        var recorded = RecordedChanceFloor.From(undefined);

        Assert.Equal(FloorState.NotDerivable, recorded.State);
        Assert.Null(recorded.Bar);              // ⚠ null, NOT 0.0
        Assert.False(recorded.IsUsableAsABar);
        Assert.Contains("no pool to draw from", recorded.Derivation, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordedFloor_CarriesTheComparisonBar_NotTheRawValue()
    {
        // An EMPIRICAL floor is an estimate and carries a Clopper-Pearson upper bound. A comparison
        // must clear THAT, not the point estimate computed from the same corpus — the co-moving
        // operands failure. Recording RawValue instead would be flattering by exactly the interval.
        var empirical = ChanceFloor.Empirical(10, 14, policiesConsidered: 4, heldOutFrom: "fold-B");

        var recorded = RecordedChanceFloor.From(empirical);

        Assert.Equal(FloorState.Derived, recorded.State);
        Assert.True(empirical.WasEstimated, "the fixture is not exercising the interval branch");
        Assert.Equal(empirical.ComparisonBar, recorded.Bar!.Value, 12);
        Assert.NotEqual(empirical.Value, recorded.Bar!.Value, 12);
    }

    [Fact]
    public void RecordedFloor_AnExactFloorRecordsItsValue()
    {
        var exact = ChanceFloor.UniformChoice(alternatives: 4);

        var recorded = RecordedChanceFloor.From(exact);

        Assert.Equal(FloorState.Derived, recorded.State);
        Assert.Equal(0.25, recorded.Bar!.Value, 12);
        Assert.True(recorded.IsUsableAsABar);
    }

    // ── the judge fingerprint: it must never carry an endpoint or a credential ──────────────
    //
    // ⚠ EVERY value below is SYNTHETIC. Nothing here is a real host, a real deployment or a real
    //   key, and no test in this file reads an environment variable.

    [Theory]
    [InlineData("https://synthetic-resource.openai.azure.com/", "a URL")]
    [InlineData("synthetic-resource.openai.azure.com", "an endpoint host")]
    [InlineData("sk-SYNTHETICSYNTHETICSYNTHETICSYNTHETICSYNTHETIC", "an API key prefix")]
    [InlineData("0123456789abcdef0123456789abcdef", "a 32-char hex key")]
    [InlineData("SYNTHETICsyntheticSYNTHETICsyntheticSYNTHETICsynth", "a 49-char opaque token")]
    public void JudgeFingerprint_RefusesAnythingShapedLikeAnEndpointOrASecret(string value, string why)
    {
        var ex = Assert.Throws<ArgumentException>(() => new JudgeFingerprint(value));

        // …and the refusal does NOT echo the offending value, because the whole reason for refusing
        // is that it may be the thing that must never be written down.
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-4o")]
    [InlineData("text-embedding-3-small")]
    [InlineData("gpt-4o-mini-realtime-preview-2024-12-17")]   // 39 chars — the longest real shape found
    [InlineData("my-judge-deployment")]
    [InlineData("claude-opus-4-1")]
    public void JudgeFingerprint_AcceptsARealModelName(string modelId)
    {
        // THE POSITIVE CONTROL. A guard that refuses everything is not a guard, it is an outage —
        // and it would fail in the direction that looks like diligence.
        var fingerprint = new JudgeFingerprint(modelId);

        Assert.Equal(modelId, fingerprint.ModelId);
        Assert.Null(JudgeFingerprint.ShapeOfASecret(modelId));
    }

    [Fact]
    public void JudgeFingerprint_RefusesOnACopyToo()
    {
        var fingerprint = new JudgeFingerprint("gpt-5.5");

        Assert.Throws<ArgumentException>(
            () => fingerprint with { ModelId = "https://synthetic-resource.openai.azure.com/" });
    }

    [Fact]
    public void JudgeFingerprint_HasNoFieldAnEndpointCouldLiveIn()
    {
        // The structural half of the claim: the guard catches a value in the WRONG field, but the
        // stronger statement is that there is no RIGHT field for one. Asserted by reflection so it
        // fails the day somebody adds `Endpoint` beside `ModelId`.
        string[] members = typeof(JudgeFingerprint)
            .GetProperties()
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ModelId", "RubricDigest", "SubjectRelation"], members);
    }

    [Fact]
    public void JudgeFingerprint_TheRubricDigestIsNotPutThroughTheModelNameGuard()
    {
        // A digest IS 64 hex characters. Running the model-name guard over it would refuse every
        // real rubric digest — the failure that would have made the guard look strict and be useless.
        var fingerprint = new JudgeFingerprint(
            "gpt-5.5",
            RubricDigest: "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        Assert.StartsWith("sha256:", fingerprint.RubricDigest, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://synthetic-resource.openai.azure.com/", "a URL")]
    [InlineData("synthetic-resource.openai.azure.com", "an endpoint host")]
    [InlineData("sha256:https://synthetic-resource.openai.azure.com/rubric", "a URL smuggled behind a digest prefix")]
    public void JudgeFingerprint_RefusesAnEndpointInTheRUBRICDIGESTToo(string value, string why)
    {
        // 🔴 THE GAP THE WAVE-8 REVIEW FOUND (`MEASUREMENT_STATUS` §68.2). The type's own headline
        // said it "MUST NEVER CARRY A CREDENTIAL OR AN ENDPOINT, AND IT REFUSES ONE AT
        // CONSTRUCTION", and exactly one of its two strings was guarded. `RubricDigest` took
        // anything — including the endpoint — and it is written into run files that get committed
        // and pasted into chat, which is the whole reason the guard exists on the other field.
        var ex = Assert.Throws<ArgumentException>(() => new JudgeFingerprint("gpt-5.5", RubricDigest: value));

        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]  // bare sha256
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("rubric-v3")]
    [InlineData(null)]
    public void JudgeFingerprint_AcceptsARealRubricDigest(string? digest)
    {
        // THE POSITIVE CONTROL, and it is the one that matters here: a digest IS 64 hex characters,
        // so applying the MODEL-NAME rules to this field would refuse every real one and the guard
        // would fail in the direction that looks like diligence. Only the endpoint half applies.
        var fingerprint = new JudgeFingerprint("gpt-5.5", RubricDigest: digest);

        Assert.Equal(digest, fingerprint.RubricDigest);
    }

    [Fact]
    public void JudgeFingerprint_RefusesAnEndpointOnACopyOfTheDigestToo()
    {
        var fingerprint = new JudgeFingerprint("gpt-5.5", RubricDigest: "sha256:abc");

        Assert.Throws<ArgumentException>(
            () => fingerprint with { RubricDigest = "https://synthetic-resource.openai.azure.com/" });
    }

    [Fact]
    public void EveryStringOnTheFingerprintRefusesAnEndpoint_NotJustTheModelName()
    {
        // The structural form of the same claim. `JudgeFingerprint_HasNoFieldAnEndpointCouldLiveIn`
        // pins WHICH members exist; this pins that each string one REFUSES an endpoint — so a third
        // string member added later fails here until somebody decides what guards it.
        const string Url = "https://synthetic-resource.openai.azure.com/";

        var guarded = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["ModelId"] = () => _ = new JudgeFingerprint(Url),
            ["RubricDigest"] = () => _ = new JudgeFingerprint("gpt-5.5", RubricDigest: Url),
        };

        string[] strings = typeof(JudgeFingerprint)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(guarded.Keys.Order(StringComparer.Ordinal).ToArray(), strings);

        foreach (var (name, construct) in guarded)
        {
            var ex = Assert.Throws<ArgumentException>(construct);
            Assert.DoesNotContain(Url, ex.Message, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }

    // ── judgeIsSubjectModel: three states, because a bool answers "nobody said" with "no" ────

    [Fact]
    public void SubjectRelation_IsUnknownWhenNobodySuppliedTheSubjectModel()
    {
        Assert.Equal(JudgeSubjectRelation.Unknown, JudgeFingerprint.RelationTo("gpt-5.5", null));
        Assert.Equal(JudgeSubjectRelation.Unknown, JudgeFingerprint.RelationTo("gpt-5.5", "  "));

        // …and Unknown is the DEFAULT, so a producer that says nothing says nothing.
        Assert.Equal(JudgeSubjectRelation.Unknown, default(JudgeSubjectRelation));
        Assert.Equal(JudgeSubjectRelation.Unknown, new JudgeFingerprint("gpt-5.5").SubjectRelation);
    }

    [Fact]
    public void SubjectRelation_SaysSameModelWhenTheJudgeIsTheSubject()
    {
        // The gate-self-examination failure at its purest: the artifact under test grades itself.
        Assert.Equal(JudgeSubjectRelation.SameModel, JudgeFingerprint.RelationTo("gpt-5.5", "gpt-5.5"));
        Assert.Equal(JudgeSubjectRelation.SameModel, JudgeFingerprint.RelationTo("GPT-5.5", " gpt-5.5 "));
    }

    [Fact]
    public void SubjectRelation_SaysDifferentModelOnlyWhenBothNamesAreKnownAndDisagree()
    {
        Assert.Equal(JudgeSubjectRelation.DifferentModel, JudgeFingerprint.RelationTo("gpt-5.5", "gpt-4o"));
    }

    [Fact]
    public void SubjectRelation_TheAblationBothWays()
    {
        // BOTH DIRECTIONS, on one fixture, because a detector that only ever fires and a detector
        // that never fires are indistinguishable from a single green test.
        var same = JudgeFingerprint.For("gpt-5.5", rubricDigest: null, subjectModel: "gpt-5.5");
        var different = JudgeFingerprint.For("gpt-5.5", rubricDigest: null, subjectModel: "gpt-4o");
        var unknown = JudgeFingerprint.For("gpt-5.5", rubricDigest: null, subjectModel: null);

        Assert.Equal(JudgeSubjectRelation.SameModel, same.SubjectRelation);
        Assert.Equal(JudgeSubjectRelation.DifferentModel, different.SubjectRelation);
        Assert.Equal(JudgeSubjectRelation.Unknown, unknown.SubjectRelation);

        // …and the three are genuinely three, not two with a label.
        Assert.Equal(3, new[] { same.SubjectRelation, different.SubjectRelation, unknown.SubjectRelation }.Distinct().Count());
    }

    // ── the persistence boundary: nothing moves for a producer that records nothing ─────────

    [Fact]
    public void ScenarioResult_WithNoComparability_SerialisesExactlyAsBefore()
    {
        var before = Scenario(comparability: null);

        string json = JsonSerializer.Serialize(before, s_storeLike);

        Assert.DoesNotContain("comparability", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, JsonDocument.Parse(json).RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task ScenarioFileOnDisk_HasNoComparabilityKey_WhenNoProducerSetOne()
    {
        // The same claim, against the bytes the SHIPPED store writes. The unit test above serialises
        // under a hand-built COPY of the store's options, which is the artifact supplying its own
        // bar: it would stay green if the real store's DefaultIgnoreCondition ever changed and every
        // scenario file on disk silently grew a "comparability": null.
        using var temp = TempWorkspace.Create("ComparabilityNone");
        string file = await WriteOneScenarioAsync(temp, Scenario(comparability: null));

        using var doc = JsonDocument.Parse(File.ReadAllText(file));

        Assert.False(doc.RootElement.TryGetProperty("comparability", out _));
        Assert.Equal(10, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task ScenarioFileOnDisk_CarriesTheFacts_WhenAProducerSetsThem()
    {
        // …and the negative direction is not vacuous: the store DOES write the block when it is set.
        // Without this, a store that dropped the member entirely would pass the test above.
        using var temp = TempWorkspace.Create("ComparabilitySet");
        var facts = new ComparabilityFacts("galaxus.eval02b", "3.1.0")
        {
            EffectiveBar = 0.8,
            ChanceFloor = RecordedChanceFloor.From(ChanceFloor.UniformChoice(4)),
            Judge = JudgeFingerprint.For("gpt-5.5", "sha256:abc", subjectModel: "gpt-5.5"),
        };

        string file = await WriteOneScenarioAsync(temp, Scenario(comparability: facts));
        using var doc = JsonDocument.Parse(File.ReadAllText(file));

        Assert.True(doc.RootElement.TryGetProperty("comparability", out var block));
        Assert.Equal(11, doc.RootElement.EnumerateObject().Count());
        Assert.Equal("galaxus.eval02b", block.GetProperty("evalKey").GetString());
        Assert.Equal("3.1.0", block.GetProperty("evalVersion").GetString());
        Assert.Equal(0.8, block.GetProperty("effectiveBar").GetDouble(), 12);
        Assert.Equal(0.25, block.GetProperty("chanceFloor").GetProperty("bar").GetDouble(), 12);
        Assert.Equal("gpt-5.5", block.GetProperty("judge").GetProperty("modelId").GetString());

        // 🔴 The whole file, not just the fingerprint: no endpoint shape anywhere in the bytes.
        string bytes = File.ReadAllText(file);
        Assert.DoesNotContain("://", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain(".azure.com", bytes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScenarioFileOnDisk_RoundTripsTheFacts()
    {
        using var temp = TempWorkspace.Create("ComparabilityRoundTrip");
        var facts = new ComparabilityFacts("k", "1.0.0")
        {
            ChanceFloor = RecordedChanceFloor.From(ChanceFloor.NotDerivable("nothing to draw from")),
        };

        string file = await WriteOneScenarioAsync(temp, Scenario(comparability: facts));
        var round = JsonSerializer.Deserialize<ScenarioResult>(File.ReadAllText(file), s_storeLike)!;

        Assert.Equal("k", round.Comparability!.EvalKey);
        Assert.Equal(FloorState.NotDerivable, round.Comparability.ChanceFloor!.State);
        Assert.Null(round.Comparability.ChanceFloor.Bar);   // ⚠ still null after a round trip
    }

    private static async Task<string> WriteOneScenarioAsync(TempWorkspace temp, ScenarioResult scenario)
    {
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "ComparabilitySubject");
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(
            subject, new RunContext("Evals", ".", "TestHarness", null, null, "eval"));

        await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);

        string[] files = Directory
            .GetFiles(temp.Path, "*.json", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)!).Equals("scenarios", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Asserts its own input: "no file carried the key" and "no file was written" are otherwise
        // indistinguishable.
        return Assert.Single(files);
    }

    // ── the producer: the runner knows all five at execution time ───────────────────────────

    [Fact]
    public void ToScenarioResult_RecordsTheKeyTheVersionAndTheBar()
    {
        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(threshold: 0.7), "scen-1", "Scenario 1");

        var facts = scenario.Comparability!;
        Assert.Equal("stub.eval", facts.EvalKey);
        Assert.Equal("2.1.0", facts.EvalVersion);
        Assert.Equal(0.7, facts.EffectiveBar!.Value, 12);
    }

    [Fact]
    public void ToScenarioResult_RecordsNoBarWhenTheEvalDeclaredNone()
    {
        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(threshold: null), "scen-1", "Scenario 1");

        Assert.Null(scenario.Comparability!.EffectiveBar);
    }

    [Fact]
    public void ToScenarioResult_RecordsNoFloorWhenNobodyDerivedOne()
    {
        var scenario = EvalResultPersistence.ToScenarioResult(Result(), "scen-1", "Scenario 1");

        // ⚠ null — "nobody derived one" — never a floor of 0.0.
        Assert.Null(scenario.Comparability!.ChanceFloor);
    }

    [Fact]
    public void ToScenarioResult_ReadsTheFloorOffTheConventionADR030Ruled()
    {
        // Floors live in Details.Dimensions["chance_floor"] plus one EvalEvidence("chance-floor",
        // kind, derivation). EvalScore.ChanceFloor was CUT, so there is nowhere else to look.
        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(
                dimensions: new Dictionary<string, double> { ["chance_floor"] = 0.44 },
                evidence: [new EvalEvidence("chance-floor", "uniform-choice", "1 of 4 alternatives, k = 1")]),
            "scen-1", "Scenario 1");

        var floor = scenario.Comparability!.ChanceFloor!;
        Assert.Equal(FloorState.Derived, floor.State);
        Assert.Equal(0.44, floor.Bar!.Value, 12);
        Assert.Equal("uniform-choice", floor.Kind);
        Assert.Contains("k = 1", floor.Derivation, StringComparison.Ordinal);
    }

    [Fact]
    public void ToScenarioResult_ANumberWithNoDerivationIsNotABar()
    {
        // ADR-030 §3.2: "the number without its derivation is unusable". Promoting a bare dimension
        // to a bar would let a comparison be held to a floor nobody can check — flattering to
        // whichever arm the number happens to favour.
        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(dimensions: new Dictionary<string, double> { ["chance_floor"] = 0.44 }),
            "scen-1", "Scenario 1");

        var floor = scenario.Comparability!.ChanceFloor!;
        Assert.Equal(FloorState.NotDerivable, floor.State);
        Assert.Null(floor.Bar);
        Assert.False(floor.IsUsableAsABar);
        Assert.Contains("no derivation", floor.Derivation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToScenarioResult_ADerivationSayingWhyNotIsRecordedAsSuch()
    {
        // "Somebody asked and could not answer" is a different fact from "nobody asked", and it is
        // the more useful of the two.
        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(evidence: [new EvalEvidence("chance-floor", "not-derivable", "the candidate pool was empty")]),
            "scen-1", "Scenario 1");

        var floor = scenario.Comparability!.ChanceFloor!;
        Assert.Equal(FloorState.NotDerivable, floor.State);
        Assert.Null(floor.Bar);
        Assert.Contains("pool was empty", floor.Derivation, StringComparison.Ordinal);
    }

    [Fact]
    public void ToScenarioResult_RecordsNoJudgeForADeterministicEval()
    {
        var scenario = EvalResultPersistence.ToScenarioResult(Result(), "scen-1", "Scenario 1");

        // null = "there was no judge", which is what a code eval means. It is not "unknown judge".
        Assert.Null(scenario.Comparability!.Judge);
    }

    [Fact]
    public void ToScenarioResult_FingerprintsTheJudgeAndSaysWhetherItIsTheSubject()
    {
        var judged = Result(judgeModel: "gpt-5.5", promptHash: "sha256:rubric");

        var graded = EvalResultPersistence.ToScenarioResult(
            judged, "scen-1", "Scenario 1", assertions: null, input: null, subjectModel: "gpt-5.5");
        var independent = EvalResultPersistence.ToScenarioResult(
            judged, "scen-1", "Scenario 1", assertions: null, input: null, subjectModel: "gpt-4o");
        var silent = EvalResultPersistence.ToScenarioResult(judged, "scen-1", "Scenario 1");

        Assert.Equal(JudgeSubjectRelation.SameModel, graded.Comparability!.Judge!.SubjectRelation);
        Assert.Equal(JudgeSubjectRelation.DifferentModel, independent.Comparability!.Judge!.SubjectRelation);
        Assert.Equal(JudgeSubjectRelation.Unknown, silent.Comparability!.Judge!.SubjectRelation);

        Assert.Equal("gpt-5.5", graded.Comparability.Judge.ModelId);
        Assert.Equal("sha256:rubric", graded.Comparability.Judge.RubricDigest);
    }

    [Fact]
    public void ToScenarioResult_RefusesToWriteAnEndpointAsAJudgeName()
    {
        // ⚠ A DECLARED BEHAVIOUR CHANGE. This path was total before; it now throws rather than
        // persist a value shaped like an endpoint. Refusing loudly beats redacting quietly, which
        // would leave the producer believing it had recorded a fingerprint.
        var leaky = Result(judgeModel: "https://synthetic-resource.openai.azure.com/");

        Assert.Throws<ArgumentException>(
            () => EvalResultPersistence.ToScenarioResult(leaky, "scen-1", "Scenario 1"));
    }

    [Fact]
    public void ToScenarioResult_StillPassesTheStimulusThrough()
    {
        // S2 and V1's other five are recorded by the same call; neither displaces the other.
        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(), "scen-1", "Scenario 1", assertions: null, input: "the question");

        Assert.Equal(StimulusHash.Of("the question"), scenario.StimulusHash);
        Assert.NotNull(scenario.Comparability);
    }

    // ── the FIRST CONSUMER, because a field with no producer is dead data ───────────────────

    [Fact]
    public void TheCompositeRunners_DeclareTheSubjectModel()
    {
        // ADR-031 finding V7 cuts a field that "gates nothing and cannot go stale detectably".
        // SubjectRelation would be exactly that if no runner ever supplied a subject model, so the
        // three composite runners were wired to thread `EvalInput.SubjectModel` through. This
        // asserts the wiring by source and — the lesson of 8f3e11c7 — asserts its own INPUT too: a
        // scan that found no files and a scan that found no offenders are indistinguishable.
        string root = RepositoryRoot();
        string[] runners =
        [
            Path.Combine(root, "src", "AgentEval.Compliance.Gdpr", "Articles", "GdprBenchmarkRunner.cs"),
            Path.Combine(root, "src", "AgentEval.Compliance.EuAiAct", "Articles", "EuAiActBenchmarkRunner.cs"),
            Path.Combine(root, "src", "AgentEval.Evals.Agentic", "Composition", "AgenticBenchmarkRunner.cs"),
        ];

        foreach (string runner in runners)
        {
            Assert.True(File.Exists(runner), $"{runner} is not where this test expects it — the scan is asserting nothing.");

            string body = File.ReadAllText(runner);
            Assert.Contains("ToScenarioResult(", body, StringComparison.Ordinal);
            Assert.Contains("subjectModel: input.SubjectModel", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SubjectModel_IsNullByDefaultOnEveryExistingInput()
    {
        // The additive claim: nothing that already builds an EvalInput starts declaring anything.
        Assert.Null(new EvalInput("q").SubjectModel);
        Assert.Equal("gpt-5.5", new EvalInput("q") { SubjectModel = "gpt-5.5" }.SubjectModel);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"AgentEval.sln was not found above {AppContext.BaseDirectory}.");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static ScenarioResult Scenario(ComparabilityFacts? comparability) => new(
        Id: "scen-1",
        Name: "Scenario 1",
        Input: "",
        Output: "{}",
        Passed: true,
        Score: 1.0,
        Metrics: new Dictionary<string, double>(),
        Assertions: [],
        Duration: TimeSpan.Zero,
        EstimatedCost: 0)
    {
        Comparability = comparability,
    };

    private static EvalResult Result(
        double? threshold = null,
        IReadOnlyDictionary<string, double>? dimensions = null,
        IReadOnlyList<EvalEvidence>? evidence = null,
        string? judgeModel = null,
        string? promptHash = null) =>
        new(
            Metric: new EvalMetadata("stub.eval", "Stub", "test", "2.1.0"),
            Score: new EvalScore(1.0, null, "pass", true, threshold, "none", null),
            Details: new EvalDetails(dimensions, evidence, null, null, null),
            Provenance: new EvalProvenance(
                judgeModel is null ? "code" : "atomic-llm", judgeModel, null, promptHash, null, 0, false),
            EvaluatedAt: DateTimeOffset.UnixEpoch);
}
