using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;

namespace AgentEval.Results.Tests.Checkpoints;

/// <summary>
/// §5.2–§5.3: lane rules over small sealed runs (<see cref="LaneRunBuilder"/>), eligibility ([LANE-1]) and the lane's
/// version and age ([LANE-9]). Each run is written into a fresh folder of runs and found through an <see cref="AefRunStore"/>.
/// </summary>
public sealed class LaneEvaluatorTests : IDisposable
{
    private const string At = "2026-10-08T00:00:00Z";
    private static readonly CheckpointSubject Subject = new(LaneRunBuilder.Subject, "v7", null);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aef-lanes-{Guid.NewGuid():N}");

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

    // ------------------------------------------------------------------ threshold ([LANE-2])

    [Theory]
    [InlineData(">=", 0.75, "passed")]
    [InlineData(">", 0.75, "failed")]
    [InlineData("<=", 0.75, "passed")]
    [InlineData("<", 0.75, "failed")]
    [InlineData("=>", 0.5, "not_measured")]   // an operator this version does not know (§7.3)
    [InlineData("==", 0.75, "not_measured")]
    public void AThreshold_ComparesTheSummaryValue_ByItsOperator(string op, double value, string status)
    {
        var dir = Run(new LaneRunBuilder("Q").Score("c1", 0.5).Score("c2", 1.0).Entry("quality", "m", "p"));

        Assert.Equal(status, Status(Threshold(op, value), dir));
    }

    [Theory]
    [InlineData(null, "passed")]
    [InlineData(2L, "passed")]
    [InlineData(3L, "not_measured")]   // n below minimumN, counted in the run's entry
    public void AThreshold_NeedsMinimumN_MeasuredLines(long? minimumN, string status)
    {
        var dir = Run(new LaneRunBuilder("Q").Score("c1", 0.5).Score("c2", 1.0).Line("c3", "p", "skipped").Entry("quality", "m", "p"));

        Assert.Equal(status, Status(Threshold(">=", 0.5, minimumN), dir));
    }

    [Theory]
    [InlineData("median", null, "passed")]
    [InlineData("max", null, "passed")]
    [InlineData("pass@k", 0.99, "not_measured")]   // [SUM-8]: a value nobody can check never decides a release
    [InlineData("bootstrap", 0.99, "not_measured")]
    public void AThreshold_ReadsAnAggregate_OnlyWhenAefDefinesItsMethod(string method, double? written, string status)
    {
        var dir = Run(new LaneRunBuilder("Q").Score("c1", 0.2).Score("c2", 0.9).Score("c3", 0.95).Entry("quality", "m", "p", method, written));

        Assert.Equal(status, Status(Threshold(">=", 0.85), dir));
    }

    [Fact]
    public void AThreshold_FailedBeatsNotMeasured_WhichBeatsPassed()
    {
        var pass = Run(new LaneRunBuilder("P").Score("c1", 0.9).Entry("quality", "m", "p"));
        var fail = Run(new LaneRunBuilder("F").Score("c1", 0.1, state: "failed").Entry("quality", "m", "p"));
        var none = Run(new LaneRunBuilder("N").Line("c1", "p", "error").Entry("quality", "m", "p"));

        Assert.Equal("failed", Status(Threshold(">=", 0.5), pass, fail, none));
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5), pass, none));
        Assert.Equal("passed", Status(Threshold(">=", 0.5), pass));
    }

    [Fact]
    public void AThreshold_WithNoEntryForItsLaneMetricAndPath_IsNotMeasured()
    {
        var dir = Run(new LaneRunBuilder("Q").Score("c1", 0.9).Entry("quality", "m", "p"));

        Assert.Equal("not_measured", Status(Threshold(">=", 0.5, path: "elsewhere"), dir));
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5, lane: "security"), dir));
    }

    // ------------------------------------------------------------------ severity ([LANE-3])

    [Fact]
    public void ASeverityPath_HoldsItselfAndWhatIsBelowIt_NotASiblingThatSharesItsPrefix()
    {
        // "toolsets" starts with "tools" but is not below it.
        var dir = Run(new LaneRunBuilder("S")
            .Line("c1", "tools", "passed").Line("c2", "tools/fetch", "failed", "low").Line("c3", "toolsets", "failed", "critical"));

        Assert.Equal("passed", Status(Severity("low", path: "tools"), dir));
        Assert.Equal("failed", Status(Severity("low"), dir));
        Assert.Equal("failed", Status(Severity("high", path: "toolsets"), dir));
    }

    [Fact]
    public void ASeverityLane_IsTheSummaryLane_AndALineWithoutOneBelongsToTheOnlyLane()
    {
        // [SUM-3]: with a single summary lane, a line without lane belongs to it.
        var single = Run(new LaneRunBuilder("S1").Line("c1", "p", "failed", "high", lane: null).Entry("quality", "ok", "p"));
        var two = Run(new LaneRunBuilder("S2").Line("c1", "p", "failed", "high", lane: null).Line("c2", "p", "passed", lane: "security")
            .Entry("quality", "ok", "p").Entry("security", "ok", "p"));

        Assert.Equal("failed", Status(Severity("low", lane: "quality"), single));
        Assert.Equal("not_measured", Status(Severity("low", lane: "quality"), two));   // the line belongs to no lane: nothing decided
        Assert.Equal("passed", Status(Severity("low", lane: "security"), two));
    }

    [Theory]
    [InlineData("high", null, "failed")]       // a failure without severity counts as critical
    [InlineData("high", "critical", "failed")]
    [InlineData("high", "unheard-of", "failed")]   // an unknown severity reads as critical (§7.3)
    [InlineData("high", "high", "passed")]
    [InlineData("medium", "high", "failed")]
    public void ASeverity_ComparesTheWorstFailure_WithMax(string max, string? severity, string status)
    {
        var dir = Run(new LaneRunBuilder("S").Line("c1", "p", "warn", severity).Line("c2", "p", "passed"));

        Assert.Equal(status, Status(Severity(max), dir));
    }

    [Theory]
    [InlineData("critical")]       // a severity, but not a max this version defines
    [InlineData("catastrophic")]
    public void ASeverityMax_ThisVersionDoesNotKnow_IsNotMeasured(string max)
    {
        var dir = Run(new LaneRunBuilder("S").Line("c1", "p", "passed"));

        Assert.Equal("not_measured", Status(Severity(max), dir));
    }

    [Fact]
    public void ASeverityMinimumN_CountsDecidedLinesOverAllTheLanesRuns_TrialsLeftOut()
    {
        var a = Run(new LaneRunBuilder("A").Line("c1", "p", "passed").Line("c2", "p", "passed", trial: 0).Line("c2", "p", "not_applicable", rollup: (1, 1))
            .Line("c3", "p", "not_applicable").Line("c4", "p", "scored"));
        var b = Run(new LaneRunBuilder("B").Line("c1", "p", "warn", "low"));

        Assert.Equal("passed", Status(Severity("low", minimumN: 2), a, b));
        Assert.Equal("not_measured", Status(Severity("low", minimumN: 3), a, b));
        Assert.Equal("passed", Status(Severity("low"), a));
        Assert.Equal("not_measured", Status(Severity("low", minimumN: 2), a));   // the trial, not_applicable and scored lines take no part
    }

    [Fact]
    public void AFailingTrialBeyondMax_FailsTheLane_EvenWhenItsRollupPasses_ButTrialsAreNeverCounted()
    {
        // R3-3: a red team's single successful attempt fails the lane; in the counts a case is its rollup.
        var dir = Run(new LaneRunBuilder("T")
            .Line("c1", "p", "failed", "high", trial: 0).Line("c1", "p", "passed", trial: 1).Line("c1", "p", "passed", rollup: (2, 1))
            .Line("c2", "q", "failed", "high", trial: 0).Line("c2", "q", "passed", rollup: (1, 0))
            .Line("c3", "p", "error", trial: 0).Line("c3", "p", "passed", rollup: (1, 0)));

        Assert.Empty(AgentEval.Results.Integrity.AefRunVerifier.Verify(dir).Problems);
        Assert.Equal("failed", Status(Severity("medium"), dir));
        Assert.Equal("passed", Status(Severity("high"), dir));                       // the error trial is not a step 2 line
        Assert.Equal("failed", Status(Severity("low", path: "q"), dir));            // scoped by path, as other lines
        Assert.Equal("passed", Status(Severity("low", lane: "security"), Run(new LaneRunBuilder("U")
            .Line("c1", "p", "failed", "high", lane: "quality", trial: 0).Line("c1", "p", "passed", lane: "quality", rollup: (1, 0))
            .Line("c2", "p", "passed", lane: "security")
            .Entry("quality", "ok", "p").Entry("security", "ok", "p"))));          // and by lane
        Assert.Equal("not_measured", Status(Severity("high", minimumN: 4), dir));    // three rollups, the trials not counted
    }

    [Fact]
    public void ASeverity_WithAnyUndecidedLine_IsNotMeasured_UnlessAFailureIsWorseThanMax()
    {
        var dir = Run(new LaneRunBuilder("S").Line("c1", "p", "passed").Line("c2", "p", "inconclusive"));
        var worse = Run(new LaneRunBuilder("W").Line("c1", "p", "failed", "high").Line("c2", "p", "error"));

        Assert.Equal("not_measured", Status(Severity("high"), dir));
        Assert.Equal("failed", Status(Severity("low"), worse));
    }

    // ------------------------------------------------------------------ evidence-present ([LANE-4])

    [Fact]
    public void EvidencePresent_CountsEachRunOnce()
    {
        var dir = Run(new LaneRunBuilder("E1").Line("c1", "p", "passed"));
        var hash = LaneRunBuilder.RunHashOf(dir);

        // A lane naming one run twice relies on one run (W4-5).
        var twice = Lane(new JsonObject { ["kind"] = "evidence-present", ["runs"] = 2 }, ("E1", hash), ("E1", hash));
        Assert.Equal(LaneEvidenceStatus.NotMeasured, LaneEvaluator.Evaluate(twice, Subject, AefRunStore.Open(_root), At).Result!.Status);
        Assert.Equal("passed", Status(new EvidencePresentRule(1, null), dir));
    }

    // ------------------------------------------------------------------ eligibility ([LANE-1]) and the lane's version and age ([LANE-9])

    [Theory]
    [InlineData("status", "aborted")]
    [InlineData("targetMode", "replayed")]
    [InlineData("targetMode", "mocked")]
    [InlineData("targetMode", "teleported")]   // unknown: reads as mocked (§7.3)
    [InlineData("subject", "agent:someone/else")]
    [InlineData("version", null)]
    public void ARunThatIsNotEligible_MakesTheLaneNotMeasured(string field, string? value)
    {
        var run = new LaneRunBuilder("G", field == "version" ? null : "v7").Score("c1", 0.9).Entry("quality", "m", "p");
        switch (field)
        {
            case "status":
                run.Run["status"] = value;
                run.Run["abortReason"] = "stopped";
                break;
            case "targetMode":
                run.Run["execution"]!["targetMode"] = value;
                break;
            case "subject":
                run.Run["subject"]!["ref"] = value;
                break;
        }

        Assert.Equal("not_measured", Status(Threshold(">=", 0.5), Run(run)));
    }

    [Fact]
    public void ARunNotIntact_IsFound_AndMakesTheLaneNotMeasured()
    {
        var unsealed = new LaneRunBuilder("U").Score("c1", 0.9).Entry("quality", "m", "p").Write(_root, seal: false);
        var tampered = Run(new LaneRunBuilder("T").Score("c1", 0.9).Entry("quality", "m", "p"));
        File.AppendAllText(Path.Combine(tampered, "metrics.json"), " ");

        Assert.Equal("not_measured", Status(Threshold(">=", 0.5), unsealed));
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5), tampered));
    }

    [Fact]
    public void ACheckpointsDeploymentAndARulesSuite_BindTheRuns()
    {
        var run = new LaneRunBuilder("D").Score("c1", 0.9).Entry("quality", "m", "p");
        run.Run["deployment"] = new JsonObject { ["ref"] = "deployment:shop/assistant@prod" };
        run.Run["suite"] = new JsonObject { ["ref"] = "suite:shop/memory", ["version"] = "3", ["digest"] = "sha256:" + new string('a', 64) };
        var dir = Run(run);
        var prod = Subject with { Deployment = "deployment:shop/assistant@prod" };

        Assert.Equal("passed", Status(Threshold(">=", 0.5), dir, subject: prod));
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5), dir, subject: Subject with { Deployment = "deployment:shop/assistant@staging" }));
        Assert.Equal("passed", Status(Threshold(">=", 0.5) with { Suite = new SuiteBinding("suite:shop/memory", null, null) }, dir, subject: prod));
        Assert.Equal("passed", Status(Threshold(">=", 0.5) with { Suite = new SuiteBinding("suite:shop/memory", "3", "sha256:" + new string('a', 64)) }, dir));
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5) with { Suite = new SuiteBinding("suite:shop/memory", "4", null) }, dir));
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5) with { Suite = new SuiteBinding("suite:shop/memory", null, "sha256:" + new string('b', 64)) }, dir));

        var bare = Run(new LaneRunBuilder("N").Score("c1", 0.9).Entry("quality", "m", "p"));
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5), bare, subject: prod));   // a run that names no deployment is in none
        Assert.Equal("not_measured", Status(Threshold(">=", 0.5) with { Suite = new SuiteBinding("suite:shop/memory", null, null) }, bare));
    }

    [Fact]
    public void ARunOfAnotherVersion_Counts_AndTheResultCarriesTheFirstOtherVersion()
    {
        var v7 = Run(new LaneRunBuilder("A").Score("c1", 0.9).Entry("quality", "m", "p"));
        var v6 = Run(new LaneRunBuilder("B", "v6").Score("c1", 0.9).Entry("quality", "m", "p"));
        var v5 = Run(new LaneRunBuilder("C", "v5").Score("c1", 0.9).Entry("quality", "m", "p"));

        var result = Result(Threshold(">=", 0.5), v7, v6, v5)!;
        Assert.Equal("passed", LaneResult.StatusName(result.Status));
        Assert.Equal("v6", result.SubjectVersion);
        Assert.Equal("v7", Result(Threshold(">=", 0.5), v7)!.SubjectVersion);
    }

    [Fact]
    public void TheLanesAge_IsTheOldestClosingTime_AsWritten_TheSealsForAnIntactRun()
    {
        var newer = new LaneRunBuilder("N").Score("c1", 0.9).Entry("quality", "m", "p");
        newer.Run["endedAt"] = "2026-10-06T00:00:00.000Z";
        var older = new LaneRunBuilder("O").Score("c1", 0.9).Entry("quality", "m", "p");
        older.Run["endedAt"] = "2026-10-04T12:00:00.5Z";

        Assert.Equal("2026-10-04T12:00:00.5Z", Result(Threshold(">=", 0.5), Run(newer), Run(older))!.OldestClosedAt);
    }

    [Fact]
    public void OnlyIntactRunsOfTheCheckpointsSubjectDeploymentAndSuite_GiveTheLaneItsVersionAndAge()
    {
        // LANE-9 (W4-4, W4-9): a run of something else, or one not intact, never gives the lane its version or its age.
        var other = new LaneRunBuilder("O", "v6").Score("c1", 0.9).Entry("quality", "m", "p");
        other.Run["subject"]!["ref"] = "agent:someone/else";
        other.Run["endedAt"] = "2026-10-02T00:00:00Z";
        var tampered = new LaneRunBuilder("T", "v5").Score("c1", 0.9).Entry("quality", "m", "p");
        tampered.Run["endedAt"] = "2026-10-03T00:00:00Z";
        var tamperedDir = Run(tampered);
        File.AppendAllText(Path.Combine(tamperedDir, "metrics.json"), " ");
        var aborted = new LaneRunBuilder("A", "v4").Score("c1", 0.9).Entry("quality", "m", "p");
        aborted.Run["status"] = "aborted";
        aborted.Run["abortReason"] = "stopped";
        aborted.Run["endedAt"] = "2026-10-04T00:00:00Z";
        var right = Run(new LaneRunBuilder("R").Score("c1", 0.9).Entry("quality", "m", "p"));

        var alone = Result(Threshold(">=", 0.5), Run(other), tamperedDir)!;
        Assert.Equal(("not_measured", "v7", At), (LaneResult.StatusName(alone.Status), alone.SubjectVersion, alone.OldestClosedAt));

        // An intact run of the subject counts though not eligible (aborted), beside one that is.
        var mixed = Result(Threshold(">=", 0.5), Path.Combine(_root, "O"), tamperedDir, Run(aborted), right)!;
        Assert.Equal(("not_measured", "v4", "2026-10-04T00:00:00Z"), (LaneResult.StatusName(mixed.Status), mixed.SubjectVersion, mixed.OldestClosedAt));

        // The rule's suite binds too.
        var suited = Result(Threshold(">=", 0.5) with { Suite = new SuiteBinding("suite:shop/memory", null, null) }, right)!;
        Assert.Equal(At, suited.OldestClosedAt);
    }

    [Fact]
    public void ALaneWithNoRunFound_HasNoResult_AndARunStillRunning_TakesTheEvaluationTime()
    {
        Directory.CreateDirectory(_root);
        Assert.Null(LaneEvaluator.Result(Threshold(">=", 0.5), [null, null], null, Subject, At));

        var running = new LaneRunBuilder("R").Score("c1", 0.9);
        running.Run["status"] = "running";
        running.Run.Remove("endedAt");
        var dir = running.Write(_root, seal: false);

        var result = Result(Threshold(">=", 0.5), dir)!;
        Assert.Equal("not_measured", LaneResult.StatusName(result.Status));
        Assert.Equal(At, result.OldestClosedAt);
    }

    [Fact]
    public void AKindThisVersionDoesNotKnow_IsNotMeasured()
    {
        var dir = Run(new LaneRunBuilder("K").Score("c1", 0.9).Entry("quality", "m", "p"));

        Assert.Equal("not_measured", Status(LaneRule.Read(new JsonObject { ["kind"] = "bayesian", ["prior"] = 0.5 }), dir));
    }

    // ------------------------------------------------------------------ comparison ([LANE-5]–[LANE-8])

    [Fact]
    public void AComparison_FailsOnASignificantRegression_AndPassesOtherwise()
    {
        var baseline = Pairs("B", "v6", Enumerable.Repeat(0.8, 20));
        var regressed = Pairs("C", "v7", Enumerable.Repeat(0.5, 18).Concat([0.9, 0.9]));     // 18 regressions, 2 improvements
        var mixed = Pairs("D", "v7", Enumerable.Repeat(0.5, 12).Concat(Enumerable.Repeat(0.9, 8)));

        Assert.Equal("failed", Compare(regressed, baseline));
        Assert.Equal("passed", Compare(mixed, baseline));
    }

    [Fact]
    public void AComparison_DropsTies_AndNeedsMinimumPairs()
    {
        var baseline = Pairs("B", "v6", Enumerable.Repeat(0.8, 20));
        var candidate = Pairs("C", "v7", Enumerable.Repeat(0.5, 10).Concat(Enumerable.Repeat(0.8, 10)));   // 10 regressions, 10 ties

        Assert.Equal("failed", Compare(candidate, baseline, minimumPairs: 10));
        Assert.Equal("not_measured", Compare(candidate, baseline, minimumPairs: 11));
    }

    [Theory]
    [InlineData("m", 0.5, "failed")]       // higher is better: lower values regressed
    [InlineData("lat", 0.5, "passed")]     // lower is better: lower values improved
    [InlineData("lat", 0.9, "failed")]
    [InlineData("flat", 0.5, "not_measured")]   // direction none cannot regress
    public void AComparison_OrientsPairsByTheCandidatesDirection(string metric, double candidateValue, string status)
    {
        var baseline = Pairs("B", "v6", Enumerable.Repeat(0.8, 20), metric);
        var candidate = Pairs("C", "v7", Enumerable.Repeat(candidateValue, 20), metric);

        Assert.Equal(status, Compare(candidate, baseline, metric: metric));
    }

    [Fact]
    public void AComparison_IsIncomparable_OnTheAxesThatDiffer_InTheRulesOrder_AnUnknownAxisDiffering()
    {
        var baseline = Pairs("B", "v6", Enumerable.Repeat(0.8, 20), judge: ("gpt-5.1", "sha256:" + new string('1', 64)));
        var candidate = Pairs("C", "v7", Enumerable.Repeat(0.5, 20), judge: ("gpt-6", "sha256:" + new string('2', 64)));

        var result = CompareResult(candidate, baseline, axes: ["rubrics", "subject", "target-mode", "judges", "weather"]);
        Assert.Equal("incomparable", LaneResult.StatusName(result.Status));
        Assert.Equal(["rubrics", "judges", "weather"], result.Axes);
        Assert.Equal("failed", Compare(candidate, baseline, axes: ["subject", "suite", "suite-content", "target-mode", "deployment", "producer"]));
    }

    [Fact]
    public void AComparison_NeedsOneEligibleCandidate_AndABaselineCheckedAsItIs_ExceptForItsVersion()
    {
        var baseline = Pairs("B", null, Enumerable.Repeat(0.8, 20));   // no version: a baseline may be of any (W4-7)
        var candidate = Pairs("C", "v7", Enumerable.Repeat(0.5, 20));
        var second = Pairs("D", "v7", Enumerable.Repeat(0.5, 20));
        var scripted = Pairs("S", "v6", Enumerable.Repeat(0.8, 20));
        scripted.Builder.Run["execution"]!["targetMode"] = "scripted";

        Assert.Equal("failed", Compare(candidate, baseline));
        Assert.Equal("not_measured", Compare(candidate, Write(scripted)));
        var store = AefRunStore.Open(_root);
        var rule = ComparisonRule(baseline, 0.05, 20, ["subject"]);
        Assert.Equal(LaneEvidenceStatus.NotMeasured,
            LaneEvaluator.Result(rule, [Find(store, candidate), Find(store, Write(second))], Find(store, baseline), Subject, At)!.Status);
        Assert.Equal(LaneEvidenceStatus.NotMeasured, LaneEvaluator.Result(rule, [Find(store, candidate)], null, Subject, At)!.Status);
    }

    [Fact]
    public void AComparisonsBaseline_IsAProblemWhenNotFound_AndTakesNoPartInTheLanesAge()
    {
        var baseline = Pairs("B", "v6", Enumerable.Repeat(0.8, 20));
        baseline.Builder.Run["startedAt"] = "2026-08-01T00:00:00Z";
        baseline.Builder.Run["endedAt"] = "2026-09-01T00:00:00Z";
        var candidate = Pairs("C", "v7", Enumerable.Repeat(0.5, 20));
        _ = Write(baseline);
        var hash = LaneRunBuilder.RunHashOf(Write(candidate).Dir);

        var lane = Lane(ComparisonJson(baseline.RunId, LaneRunBuilder.RunHashOf(baseline.Dir), ["subject"]), ("C", hash));
        var found = LaneEvaluator.Evaluate(lane, Subject, AefRunStore.Open(_root), At);
        Assert.Equal("2026-10-05T00:00:00Z", found.Result!.OldestClosedAt);
        Assert.Empty(found.Problems);

        var missing = LaneEvaluator.Evaluate(Lane(ComparisonJson("B", new string('0', 64), ["subject"]), ("C", hash)), Subject, AefRunStore.Open(_root), At);
        Assert.Equal("not_measured", LaneResult.StatusName(missing.Result!.Status));
        Assert.Equal([new AefProblem("lanes/lane/runs/B", "run-missing")], missing.Problems);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Paired(string RunId, LaneRunBuilder Builder)
    {
        public string Dir { get; set; } = "";
    }

    private Paired Pairs(string runId, string? version, IEnumerable<double> values, string metric = "m", (string Model, string Rubric)? judge = null)
    {
        var builder = new LaneRunBuilder(runId, version);
        var i = 0;
        foreach (var value in values)
        {
            builder.Score($"case-{i++}", value, path: "mem", metric: metric);
        }

        builder.Entry("quality", metric, "mem");
        if (judge is { } j)
        {
            builder.Run["judges"] = new JsonArray(new JsonObject { ["model"] = j.Model, ["provider"] = "azure.ai.openai", ["mode"] = "single", ["rubricDigest"] = j.Rubric });
        }

        return new Paired(runId, builder);
    }

    private Paired Write(Paired paired)
    {
        if (paired.Dir.Length == 0)
        {
            paired.Dir = Run(paired.Builder);
        }

        return paired;
    }

    private static AefStoredRun Find(AefRunStore store, Paired paired) => store.Find(paired.RunId, LaneRunBuilder.RunHashOf(paired.Dir))!;

    private ComparisonRule ComparisonRule(Paired baseline, double significance, long minimumPairs, IReadOnlyList<string> axes, string metric = "m") =>
        new("quality", metric, "mem", new AefRunPointer(baseline.RunId, LaneRunBuilder.RunHashOf(Write(baseline).Dir)), significance, minimumPairs, axes, null);

    private LaneResult CompareResult(Paired candidate, Paired baseline, long minimumPairs = 20, string metric = "m", IReadOnlyList<string>? axes = null)
    {
        Write(candidate);
        Write(baseline);
        var store = AefRunStore.Open(_root);
        var rule = ComparisonRule(baseline, 0.05, minimumPairs, axes ?? ["subject", "suite", "judges", "rubrics", "target-mode"], metric);
        return LaneEvaluator.Result(rule, [Find(store, candidate)], Find(store, baseline), Subject, At)!;
    }

    private string Compare(Paired candidate, Paired baseline, long minimumPairs = 20, string metric = "m", IReadOnlyList<string>? axes = null) =>
        LaneResult.StatusName(CompareResult(candidate, baseline, minimumPairs, metric, axes).Status);

    private static JsonObject ComparisonJson(string baselineId, string baselineHash, string[] axes) => new()
    {
        ["kind"] = "comparison", ["lane"] = "quality", ["metric"] = "m", ["path"] = "mem",
        ["baseline"] = new JsonObject { ["runId"] = baselineId, ["runHash"] = baselineHash },
        ["significance"] = 0.05, ["minimumPairs"] = 20, ["axes"] = new JsonArray([.. axes.Select(a => (JsonNode?)a)]),
    };

    private static JsonObject Lane(JsonObject rule, params (string RunId, string RunHash)[] runs) => new()
    {
        ["lane"] = "lane",
        ["rule"] = rule,
        ["runs"] = new JsonArray([.. runs.Select(r => (JsonNode?)new JsonObject { ["runId"] = r.RunId, ["runHash"] = r.RunHash, ["origin"] = "launched" })]),
        ["blocking"] = true,
    };

    private string Run(LaneRunBuilder builder) => builder.Write(_root);

    private static ThresholdRule Threshold(string op, double value, long? minimumN = null, string lane = "quality", string path = "p") =>
        new(lane, "m", path, op, value, minimumN, null);

    private static SeverityRule Severity(string max, string? lane = null, string? path = null, long? minimumN = null) =>
        new(max, lane, path, minimumN, null);

    private LaneResult? Result(LaneRule rule, params string[] dirs) => Result(rule, Subject, dirs);

    private LaneResult? Result(LaneRule rule, CheckpointSubject subject, params string[] dirs)
    {
        var store = AefRunStore.Open(_root);
        var runs = dirs.Select(d => store.Runs.Single(r => r.Directory == d)).Select(r => (AefStoredRun?)store.Find(r.RunId, r.RunHash.Value)).ToList();
        return LaneEvaluator.Result(rule, runs, null, subject, At);
    }

    private string Status(LaneRule rule, params string[] dirs) => LaneResult.StatusName(Result(rule, Subject, dirs)!.Status);

    private string Status(LaneRule rule, string dir, CheckpointSubject subject) => LaneResult.StatusName(Result(rule, subject, dir)!.Status);
}
