using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runner;
using AgentEval.Results.Tests.Corpus;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Runner;

/// <summary>
/// The scripted runner (spec 06 §6, spec 09 §9.2.1 "A scripted target" and "The job's clock"): whether it takes a plan
/// ([PLAN-7]), its suites and case ids ([PLAN-8]), its limits against the cases' bounds ([PLAN-9]), its credentials
/// ([PLAN-3]: resolved, never written), what it derives ([PLAN-10]), and that one job run twice writes the same bytes.
/// Every job here is also checked as a Stream verifier checks it ([STRM-3], [STRM-4]) and each run as a Run verifier reads
/// it.
/// </summary>
public sealed class AefScriptedRunnerTests : IDisposable
{
    private static readonly AefTime Start = AefTime.Parse("2026-10-09T09:00:00Z");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aef-runner-{Guid.NewGuid():N}");

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

    // ------------------------------------------------------------------ [PLAN-9] limits against bounds

    [Theory]
    [InlineData(1.0, 0.5, "job.sealed", null, 3)]       // 0.50 + 0.50 is exactly maxUsd: within it, the third case runs
    [InlineData(0.99, 0.5, "job.failed", "maxUsd", 2)]  // the third case's bound would pass it, though its cost would not
    [InlineData(1.0, 0.25, "job.sealed", null, 3)]
    public void TheBudget_IsCheckedAgainstTheSpendPlusTheCasesCostBound_EqualIsWithin(double maxUsd, double bound, string terminal, string? limit, int run)
    {
        var target = Target(1, Suite("suite:s/a", "1", Case("a-1", usd: 0.25, usdBound: 0.25), Case("a-2", usd: 0.25, usdBound: 0.25), Case("a-3", usd: 0.25, usdBound: bound)));
        var job = Run(Plan(maxUsd: maxUsd, suites: [("suite:s/a", "1")]), target);

        Assert.Equal((terminal, limit), (job.Result.Terminal, job.Result.Limit));
        var only = Assert.Single(job.Runs);
        Assert.Equal(run == 3 ? "completed" : "aborted", (string?)only.Run["status"]);
        Assert.Equal(run, only.Lines.Count);
        Assert.Equal(0.25 * run, job.SpentUsd);
        AssertKeepsTheStreamRules(job);
    }

    [Fact]
    public void TheBudget_IsCheckedAsTheStreamAndTheRunsWillBeSummed_SoNeitherSumPassesIt()
    {
        // $1 spent, and a case whose bound is 1e-17: exactly the sum is above maxUsd $1, rounded once it is $1, which is
        // what [STRM-3]'s spentUsd and [STRM-4]'s run total will be: within the budget, so the case runs (R7R-3).
        var target = Target(1, Suite("suite:s/r", "1", Case("r-1", usd: 1.0, usdBound: 1.0), Case("r-2", usd: 1e-17, usdBound: 1e-17)));
        var job = Run(Plan(maxUsd: 1.0, suites: [("suite:s/r", "1")]), target);

        Assert.Equal("job.sealed", job.Result.Terminal);
        AssertKeepsTheStreamRules(job);

        // Runs of $0.10 + $0.10 and $0.10 + $0.30: the cases sum to 0.6 rounded once, within $0.60, but the runs' totals,
        // 0.2 and 0.4, sum to 0.6000000000000001: the runner stops before the last case.
        target = Target(1, Suite("suite:s/x", "1", Case("x-1", usd: 0.1, usdBound: 0.1), Case("x-2", usd: 0.1, usdBound: 0.1)),
                           Suite("suite:s/y", "1", Case("y-1", usd: 0.1, usdBound: 0.1), Case("y-2", usd: 0.3, usdBound: 0.3)));
        job = Run(Plan(maxUsd: 0.6, suites: [("suite:s/x", "1"), ("suite:s/y", "1")]), target);

        Assert.Equal(("job.failed", "maxUsd"), (job.Result.Terminal, job.Result.Limit));
        Assert.Equal(["completed", "aborted"], job.Runs.Select(r => (string)r.Run["status"]!));
        AssertKeepsTheStreamRules(job);

        // Runs of $0.10 + $0.20 and $0.13 + $0.03: exactly the cases are within $0.46, and so is their sum rounded once;
        // the runs' totals, 0.30000000000000004 and 0.16, sum to 0.4600000000000001: the runner stops before the last case.
        target = Target(1, Suite("suite:s/x", "1", Case("x-1", usd: 0.1, usdBound: 0.1), Case("x-2", usd: 0.2, usdBound: 0.2)),
                           Suite("suite:s/y", "1", Case("y-1", usd: 0.13, usdBound: 0.13), Case("y-2", usd: 0.03, usdBound: 0.03)));
        job = Run(Plan(maxUsd: 0.46, suites: [("suite:s/x", "1"), ("suite:s/y", "1")]), target);

        Assert.Equal(("job.failed", "maxUsd"), (job.Result.Terminal, job.Result.Limit));
        Assert.Equal(["x-1", "x-2", "y-1"], job.Runs.SelectMany(r => r.Lines).Select(l => (string)l["caseId"]!));
        AssertKeepsTheStreamRules(job);
    }

    [Theory]
    [InlineData(20L, "job.sealed", null)]        // 40 s so far + 20 + 0: exactly the timeout, within it
    [InlineData(21L, "job.failed", "timeout")]   // 40 + 21 + 0: above it, though the case would take only 20
    public void TheTimeout_IsCheckedAgainstTheTimeSoFarPlusTheCasesTimeBoundPlusCloseSeconds(long lastBound, string terminal, string? limit)
    {
        // A timeout of one minute and closeSeconds 0: two cases of 20 s, then a third of 20 s whose time bound is lastBound.
        var target = Target(0, Suite("suite:s/t", "1", Case("t-1"), Case("t-2"), Case("t-3", secondsBound: lastBound)));
        var job = Run(Plan(timeout: "PT1M", suites: [("suite:s/t", "1")]), target);

        Assert.Equal((terminal, limit), (job.Result.Terminal, job.Result.Limit));
        Assert.Equal(limit is null ? Start.AddSeconds(60) : Start.AddSeconds(40), job.Result.EndsAt);
        AssertKeepsTheStreamRules(job);
    }

    [Fact]
    public void TheTimeout_KeepsCloseSecondsForClosingAndSealingTheRun()
    {
        // closeSeconds 1: before the third case, 40 + 20 + 1 > 60, so the run is closed aborted after two cases and sealed
        // at 41 s; without that second the third case would fit.
        var target = Target(1, Suite("suite:s/t", "1", Case("t-1"), Case("t-2"), Case("t-3")));
        var job = Run(Plan(timeout: "PT1M", suites: [("suite:s/t", "1")]), target);

        Assert.Equal(("job.failed", "timeout"), (job.Result.Terminal, job.Result.Limit));
        Assert.Equal(Start.AddSeconds(41), job.Result.EndsAt);
        Assert.Equal("aborted", (string?)Assert.Single(job.Runs).Run["status"]);
        Assert.Equal("2026-10-09T09:00:40Z", (string?)job.Runs[0].Run["endedAt"]);
        AssertKeepsTheStreamRules(job);
    }

    [Fact]
    public void TheCasesLimit_MetAsASuiteEnds_ClosesItsRunCompleted_AndOpensNoRunForTheNext()
    {
        var target = Target(1, Suite("suite:s/a", "1", Case("a-1"), Case("a-2")), Suite("suite:s/b", "1", Case("b-1"), Case("b-2")));
        var job = Run(Plan(cases: 2, suites: [("suite:s/a", "1"), ("suite:s/b", "1")]), target);

        Assert.Equal(("job.failed", "cases"), (job.Result.Terminal, job.Result.Limit));
        Assert.Equal("completed", (string?)Assert.Single(job.Runs).Run["status"]);
        Assert.Single(Directory.GetDirectories(Path.Combine(job.Out, "runs")));
        Assert.Equal(new JsonObject { ["cases"] = 2, ["usdLow"] = 0.5, ["usdHigh"] = 0.5 }.ToJsonString(), Estimate(job).ToJsonString());
        AssertKeepsTheStreamRules(job);
    }

    [Fact]
    public void ALimitStoppingMidSuite_ClosesTheRunAborted_WithNoLineForTheCasesNotRun()
    {
        var target = Target(1, Suite("suite:s/a", "1", Case("a-1"), Case("a-2"), Case("a-3")));
        var job = Run(Plan(cases: 2, suites: [("suite:s/a", "1")]), target);

        var run = Assert.Single(job.Runs);
        Assert.Equal("aborted", (string?)run.Run["status"]);
        Assert.NotNull((string?)run.Run["abortReason"]);
        Assert.Equal(["a-1", "a-2"], run.Lines.Select(l => (string)l["caseId"]!));
        AssertKeepsTheStreamRules(job);
    }

    // ------------------------------------------------------------------ [PLAN-3] credentials

    [Fact]
    public void EveryCredential_IsResolved_ThoughTheTargetNeedsNone_AndNoValueOrPathIsWritten()
    {
        var asked = new List<string>();
        var values = new Dictionary<string, string> { ["AEF_UNIT_SUBJECT_KEY"] = "value-" + Guid.NewGuid().ToString("N"), ["AEF_UNIT_JUDGE_KEY"] = "value-" + Guid.NewGuid().ToString("N") };
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan["credentialRefs"] = new JsonArray(Credential("SUBJECT_API_KEY", "env", "AEF_UNIT_SUBJECT_KEY", "subject"), Credential("JUDGE_API_KEY", "env", "AEF_UNIT_JUDGE_KEY", "judge"));

        var job = Run(plan, Target(1, Suite("suite:s/a", "1", Case("a-1"))), name =>
        {
            asked.Add(name);
            return values.GetValueOrDefault(name);
        });

        Assert.Equal("job.sealed", job.Result.Terminal);
        Assert.Equal(["AEF_UNIT_SUBJECT_KEY", "AEF_UNIT_JUDGE_KEY"], asked);
        AssertHoldsNone(job.Out, [.. values.Values, .. values.Keys]);
        AssertKeepsTheStreamRules(job);
    }

    [Theory]
    [InlineData("env", "AEF_UNIT_UNSET_KEY")]
    [InlineData("keychain", "agenteval/unit-keychain-entry")]
    [InlineData("vault", "kv/agenteval/unit-vault-path")]
    public void ACredentialItCannotResolve_IsARefusalBeforeJobAccepted_ThatNamesNoPath(string scheme, string path)
    {
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan["credentialRefs"] = new JsonArray(Credential("SUBJECT_API_KEY", scheme, path, "subject"));

        var job = Run(plan, Target(1, Suite("suite:s/a", "1", Case("a-1"))), _ => null);

        Assert.Equal("job.refused", Assert.Single(job.Events)["kind"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(job.Out, "runs")));
        AssertHoldsNone(job.Out, [path]);
        Assert.DoesNotContain(path, job.Result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvCredentialWhoseVariableIsEmpty_DoesNotResolve()
    {
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan["credentialRefs"] = new JsonArray(Credential("SUBJECT_API_KEY", "env", "AEF_UNIT_EMPTY_KEY", "subject"));

        var job = Run(plan, Target(1, Suite("suite:s/a", "1", Case("a-1"))), name => name == "AEF_UNIT_EMPTY_KEY" ? "" : null);

        Assert.Equal("job.refused", (string?)Assert.Single(job.Events)["kind"]);
    }

    [Theory]
    [InlineData("container")]
    [InlineData("remote-zone")]
    public void AScriptedTargetRunsInTheRunnersProcess_SoItGivesTheIsolationProcessOnly(string isolation)
    {
        // The manifest takes the plan (it supports docker and is in the plan's zone); the runner does not (§9.2.1, R7R-8).
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan["isolation"] = isolation;
        plan["provider"] = "docker";
        if (isolation == "container") plan["subject"]!["image"] = "sha256:" + string.Concat(Enumerable.Repeat("9d", 32));
        if (isolation == "remote-zone") plan["zone"] = "eu-1";
        var runner = Manifest();
        runner["providers"] = new JsonArray("local", "docker");
        runner["networkZone"] = "eu-1";
        Assert.True(RunnerEventStream.Matches(plan, runner));

        var job = Run(plan, Target(1, Suite("suite:s/a", "1", Case("a-1"))), runner: runner);

        var refused = Assert.Single(job.Events);
        Assert.Equal("job.refused", (string?)refused["kind"]);
        Assert.Contains(isolation, (string?)refused["reason"], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ [PLAN-8] suites and case ids

    [Fact]
    public void ARunDeclaresPassRate_AndASuiteWithALane_SummarisesItThereAtCheck()
    {
        // quality: passed, failed, not_measured and error (the last two not measured): N 4, n 2, value 0.5. A suite the
        // plan gives no lane: no lane on its lines, none in its summary.
        var plan = Plan(suites: [("suite:s/a", "1"), ("suite:s/b", "1")]);
        plan["suites"]![1]!.AsObject().Remove("lane");
        var target = Target(1,
            Suite("suite:s/a", "1", Case("a-1"), Case("a-2", "failed"), Case("a-3", "not_measured"), Case("a-4", "error")),
            Suite("suite:s/b", "1", Case("b-1")));

        var job = Run(plan, target);

        foreach (var run in job.Runs)
        {
            var metrics = JsonNode.Parse(File.ReadAllBytes(Path.Combine(run.Folder, "metrics.json")))!;
            Assert.Equal("[{\"id\":\"pass-rate\",\"kind\":\"rate\",\"direction\":\"higher_better\",\"scale\":{\"min\":0,\"max\":1}}]", metrics["metrics"]!.ToJsonString());
        }

        var quality = JsonNode.Parse(File.ReadAllBytes(Path.Combine(job.Runs[0].Folder, "summary.json")))!["lanes"]!.AsArray();
        var entry = Assert.Single(Assert.Single(quality)!["metrics"]!.AsArray())!;
        Assert.Equal(("quality", "pass-rate", "check"), ((string?)quality[0]!["lane"], (string?)entry["metric"], (string?)entry["path"]));
        Assert.Equal((4, 2, 0.5), ((int)entry["N"]!, (int)entry["n"]!, (double)entry["value"]!));
        Assert.All(job.Runs[0].Lines, l => Assert.Equal("quality", (string?)l["lane"]));

        Assert.Empty(JsonNode.Parse(File.ReadAllBytes(Path.Combine(job.Runs[1].Folder, "summary.json")))!["lanes"]!.AsArray());
        Assert.Null(job.Runs[1].Lines[0]["lane"]);
        AssertKeepsTheStreamRules(job);
    }

    [Fact]
    public void TwoSuitesThatShareCaseIds_KeepTheirIds_InOneRunEach_AndCountAsTheirSuitesCases()
    {
        var target = Target(1,
            Suite("suite:s/a", "1", Case("case-1"), Case("case-2", "failed", severity: "low")),
            Suite("suite:s/b", "2", Case("case-1", "warn", severity: "medium"), Case("case-2", "not_measured")));
        var job = Run(Plan(cases: 4, suites: [("suite:s/a", "1"), ("suite:s/b", "2")]), target);

        Assert.Equal("job.sealed", job.Result.Terminal);
        Assert.Equal(2, job.Runs.Count);
        Assert.Equal([("case-1", "passed"), ("case-2", "failed")], job.Runs[0].Lines.Select(l => ((string)l["caseId"]!, (string)l["state"]!)));
        Assert.Equal([("case-1", "warn"), ("case-2", "not_measured")], job.Runs[1].Lines.Select(l => ((string)l["caseId"]!, (string)l["state"]!)));
        Assert.All(job.Runs.SelectMany(r => r.Lines), l => Assert.Equal("check", (string?)l["path"]));
        Assert.Equal("low", (string?)job.Runs[0].Lines[1]["severity"]);       // a failed line carries the case's severity ([RES-9])
        Assert.Equal("medium", (string?)job.Runs[1].Lines[0]["severity"]);
        Assert.Null(job.Runs[0].Lines[0]["severity"]);
        Assert.NotNull((string?)job.Runs[1].Lines[1]["reason"]);               // a typed absence a reason ([RES-2])

        // Each case.completed names its run: two suites share the ids.
        var completed = job.Events.Where(e => (string?)e["kind"] == "case.completed").ToList();
        Assert.Equal([job.Runs[0].Id, job.Runs[0].Id, job.Runs[1].Id, job.Runs[1].Id], completed.Select(e => (string)e["runId"]!));
        AssertKeepsTheStreamRules(job);   // four cases: exactly the plan's limit ([STRM-4] over-cases)
    }

    [Fact]
    public void APlanThatNamesASuiteTwice_IsRefused()
    {
        var target = Target(1, Suite("suite:s/a", "1", Case("a-1")));
        var job = Run(Plan(suites: [("suite:s/a", "1"), ("suite:s/a", "1")]), target);

        var refused = Assert.Single(job.Events);
        Assert.Equal("job.refused", (string?)refused["kind"]);
        Assert.Contains("twice", (string?)refused["reason"], StringComparison.Ordinal);
        Assert.Equal(["events.ndjson"], Directory.GetFileSystemEntries(job.Out).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("suite:s/missing", "1", null)]                                                                         // not resolved
    [InlineData("suite:s/a", "2", null)]                                                                               // another version
    [InlineData("suite:s/a", "1", "sha256:0000000000000000000000000000000000000000000000000000000000000000")]          // another digest
    public void ASuiteItCannotResolve_OrWhoseContentHasAnotherDigest_IsRefusedBeforeJobAccepted(string suiteRef, string version, string? digest)
    {
        var plan = Plan(suites: [(suiteRef, version)]);
        if (digest is not null) plan["suites"]![0]!["digest"] = digest;

        var job = Run(plan, Target(1, Suite("suite:s/a", "1", Case("a-1"))));

        Assert.Equal("job.refused", (string?)Assert.Single(job.Events)["kind"]);
    }

    [Fact]
    public void ASuiteWithItsContentsDigest_IsRunAndNamesThatDigest()
    {
        var target = Target(1, Suite("suite:s/a", "1", Case("a-1")));
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan["suites"]![0]!["digest"] = AefScriptedTarget.Read(target).Suites[0].Digest;

        var job = Run(plan, target);

        Assert.Equal("job.sealed", job.Result.Terminal);
        Assert.Equal((string?)plan["suites"]![0]!["digest"], (string?)job.Runs[0].Run["suite"]!["digest"]);
    }

    // ------------------------------------------------------------------ [PLAN-7], [PLAN-6] target modes

    [Theory]
    [InlineData(null, """["scripted"]""", "job.refused")]              // a plan without a mode asks for live
    [InlineData("scripted", null, "job.refused")]                      // a manifest without modes gives live only
    [InlineData("live", """["live", "scripted"]""", "job.refused")]    // the manifest says live; this runner cannot drive a live target
    [InlineData("scripted", """["live", "scripted"]""", "job.sealed")]
    public void ItGivesTheScriptedTargetModeOnly_AndOnlyWhenItsManifestDoes(string? planMode, string? manifestModes, string terminal)
    {
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan.Remove("targetMode");
        if (planMode is not null) plan["targetMode"] = planMode;
        var runner = Manifest();
        runner.Remove("targetModes");
        if (manifestModes is not null) runner["targetModes"] = JsonNode.Parse(manifestModes);

        var job = Run(plan, Target(1, Suite("suite:s/a", "1", Case("a-1"))), runner: runner);

        Assert.Equal(terminal, job.Result.Terminal);
        if (terminal == "job.sealed")
        {
            Assert.Equal("scripted", (string?)job.Runs[0].Run["execution"]!["targetMode"]);
        }
    }

    [Fact]
    public void APlanTheReaderRefuses_IsRefused_NamingItsPlanIdAndTheDigestOfItsBytes()
    {
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan["limits"]!["timeout"] = "PT30S";   // no seconds in an AEF duration ([ENC-9])
        var bytes = Encoding.UTF8.GetBytes(plan.ToJsonString());

        var job = Run(bytes, Target(1, Suite("suite:s/a", "1", Case("a-1"))), _ => null, Manifest());

        var refused = Assert.Single(job.Events);
        Assert.Equal(("job.refused", "plan-unit"), ((string?)refused["kind"], (string?)refused["planId"]));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), (string?)refused["planDigest"]);
        Assert.Equal(Start.ToString(), (string?)refused["at"]);
    }

    [Theory]
    [InlineData("{")]                                                    // does not read
    [InlineData("""{"schemaVersion": "1.0"}""")]                         // no planId
    [InlineData("""{"schemaVersion": "1.0", "planId": "a plan"}""")]     // a planId that is no id
    public void APlanThatDoesNotRead_OrNamesNoPlanId_IsAnInputError_AndNothingIsWritten(string plan)
    {
        var output = Path.Combine(_root, "out");
        var target = AefScriptedTarget.Read(Target(1, Suite("suite:s/a", "1", Case("a-1"))));

        Assert.Throws<FormatException>(() => AefScriptedRunner.Run(Encoding.UTF8.GetBytes(plan), Manifest(), target, output, new AefJobOptions { At = Start }));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void AManifestTheReaderRefuses_OrAnOutputThatIsNotEmpty_IsAnInputError()
    {
        var target = AefScriptedTarget.Read(Target(1, Suite("suite:s/a", "1", Case("a-1"))));
        var plan = Encoding.UTF8.GetBytes(Plan(suites: [("suite:s/a", "1")]).ToJsonString());
        var manifest = Manifest();
        manifest.Remove("runnerId");
        var output = Path.Combine(_root, "out");

        Assert.Throws<FormatException>(() => AefScriptedRunner.Run(plan, manifest, target, output, new AefJobOptions { At = Start }));
        Assert.False(Directory.Exists(output));

        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "x"), "x");
        Assert.Throws<ArgumentException>(() => AefScriptedRunner.Run(plan, Manifest(), target, output, new AefJobOptions { At = Start }));
    }

    // ------------------------------------------------------------------ [PLAN-10], determinism

    [Theory]
    [InlineData("agent:support/triage", "deployment:support/triage@dev", null, "agent", "deployment:support/triage@dev")]
    [InlineData("pipeline:support/triage", "deployment:support/triage@dev", null, "other", "deployment:support/triage@dev")]
    [InlineData("mcp-server:tools/files", null, "https://api.example.com/v1", "mcp-server", "endpoint:https://api.example.com/v1")]
    [InlineData("model:openai/gpt-5.1", null, null, "model", null)]
    public void WhatThePlanDoesNotSay_IsDerivedAsPlan10Says(string subjectRef, string? deployment, string? endpoint, string kind, string? deploymentRef)
    {
        var plan = Plan(suites: [("suite:s/a", "1")]);
        plan["subject"] = new JsonObject { ["ref"] = subjectRef, ["version"] = "7" };
        if (deployment is not null) plan["subject"]!["deployment"] = deployment;
        if (endpoint is not null) plan["subject"]!["endpoint"] = endpoint;

        var job = Run(plan, Target(1, Suite("suite:s/a", "1", Case("a-1"))));

        var run = Assert.Single(job.Runs).Run;
        Assert.Equal(kind, (string?)run["subject"]!["kind"]);
        Assert.Equal(deploymentRef, (string?)run["deployment"]?["ref"]);
        Assert.Equal(endpoint, (string?)run["deployment"]?["endpoint"]);
    }

    [Theory]
    [InlineData("Support Agent", "Support%20Agent")]
    [InlineData("50%", "50%25")]
    [InlineData("é", "%C3%A9")]
    [InlineData("", "-")]
    [InlineData("-", "%2D")]
    public void ANameFromFreeText_IsEncodedAsEnc13Says(string text, string name) => Assert.Equal(name, AefPlanDefaults.EncodeName(text));

    [Fact]
    public void ANameLongerThan256_KeepsItsFirst239_AndAHashOfTheText()
    {
        var encoded = AefPlanDefaults.EncodeName("https://example.com/" + new string('a', 300));

        Assert.Equal(256, encoded.Length);
        Assert.Equal('~', encoded[239]);
        Assert.Matches("^[0-9a-f]{16}$", encoded[240..]);
    }

    [Fact]
    public void OneJobRunTwiceUnderAt_WritesTheSameBytes_WhateverTheCredentialsValues()
    {
        var plan = Plan(cases: 3, suites: [("suite:s/a", "1"), ("suite:s/b", "1")]);
        plan["credentialRefs"] = new JsonArray(Credential("SUBJECT_API_KEY", "env", "AEF_UNIT_KEY", "subject"));
        var target = Target(2, Suite("suite:s/a", "1", Case("a-1"), Case("a-2", "error")), Suite("suite:s/b", "1", Case("b-1"), Case("b-2")));

        var first = Run(plan, target, _ => "value-" + Guid.NewGuid().ToString("N"));
        var second = Run(plan, target, _ => "value-" + Guid.NewGuid().ToString("N"));

        var files = Files(first.Out);
        Assert.Equal(files.Keys, Files(second.Out).Keys);
        Assert.All(files, f => Assert.Equal(f.Value, Files(second.Out)[f.Key]));
        // accepted, estimated; a-1 and a-2 (completed and spend each), a's run announced; b-1, b's run announced (aborted
        // at the cases limit); failed.
        Assert.Equal(1 + 1 + 2 + 2 + 1 + 2 + 1 + 1, first.Result.Events);
        Assert.Equal(("job.failed", "cases"), (first.Result.Terminal, first.Result.Limit));
        Assert.Equal(["completed", "aborted"], first.Runs.Select(r => (string)r.Run["status"]!));

        // Another start: another job, and other run ids.
        var later = Run(plan, target, _ => "x", at: Start.AddSeconds(1));
        Assert.DoesNotContain(later.Runs[0].Id, first.Runs.Select(r => r.Id));
    }

    // ------------------------------------------------------------------ the target's shape (§9.2.1)

    [Theory]
    [InlineData("""{"suites": [], "closeSeconds": 1}""")]                                                                 // no priceTable
    [InlineData("""{"suites": [], "closeSeconds": 1, "priceTable": "p", "extra": 1}""")]                                  // a member it does not have
    [InlineData("""{"suites": [], "closeSeconds": 1.5, "priceTable": "p"}""")]                                            // not whole seconds
    [InlineData("""{"suites": [], "closeSeconds": -1, "priceTable": "p"}""")]
    [InlineData("""{"suites": [], "closeSeconds": 1, "priceTable": ""}""")]
    [InlineData("""{"suites": [{"ref": "suite:a", "version": "1", "content": "", "cases": []}], "closeSeconds": 1, "priceTable": "p"}""")]
    [InlineData("""{"suites": [{"ref": "suite:a", "version": "1", "content": "", "cases": [CASE]}], "closeSeconds": 1, "priceTable": "p"}""", "pending")]
    [InlineData("""{"suites": [{"ref": "suite:a", "version": "1", "content": "", "cases": [CASE]}], "closeSeconds": 1, "priceTable": "p"}""", "passed", 0.5, 0.25)]
    [InlineData("""{"suites": [{"ref": "suite:a", "version": "1", "content": "", "cases": [CASE]}], "closeSeconds": 1, "priceTable": "p"}""", "passed", 0.25, 0.25, 20L, 10L)]
    [InlineData("""{"suites": [{"ref": "suite:a", "version": "1", "content": "", "cases": [CASE]}], "closeSeconds": 1, "priceTable": "p"}""", "passed", -0.25, 0.25)]
    [InlineData("""{"suites": [{"ref": "suite:a", "version": "1", "content": "", "cases": [CASE, CASE]}], "closeSeconds": 1, "priceTable": "p"}""")]
    [InlineData("""{"suites": [{"ref": "suite:a", "version": "1", "content": "", "cases": [CASE]}, {"ref": "suite:a", "version": "1", "content": "", "cases": [CASE]}], "closeSeconds": 1, "priceTable": "p"}""")]
    public void ATargetNotOfItsShape_IsRefused(string json, string state = "passed", double usd = 0.25, double usdBound = 0.25, long seconds = 20, long secondsBound = 20)
    {
        var text = json.Replace("CASE", Case("c-1", state, usd, usdBound, seconds, secondsBound).ToJsonString(), StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => AefScriptedTarget.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("passed", "high")]       // a severity on a case that did not fail
    [InlineData("failed", null)]         // a failed case without one
    [InlineData("warn", null)]
    [InlineData("failed", "severe")]     // not a severity of RES-9
    [InlineData("not_measured", "low")]
    public void ACaseSeverity_IsOnAFailedOrWarnCaseOnly_AndASeverityOfRes9(string state, string? severity)
    {
        var json = Case("c-1", state);
        json.Remove("severity");
        if (severity is not null) json["severity"] = severity;

        Assert.Throws<FormatException>(() => AefScriptedTarget.Read(Target(1, Suite("suite:a", "1", json))));
    }

    [Fact]
    public void ATargetsWholeSeconds_MayBeWrittenWithAFraction_OfZero()
    {
        var target = AefScriptedTarget.Read(Encoding.UTF8.GetBytes(
            """{"suites": [{"ref": "suite:a", "version": "1", "content": "x", "cases": [{"caseId": "c", "state": "passed", "usd": 0, "usdBound": 0, "seconds": 20.0, "secondsBound": 20}]}], "closeSeconds": 1.0, "priceTable": "p"}"""));

        Assert.Equal((20L, 1L), (target.Suites[0].Cases[0].Seconds, target.CloseSeconds));
        Assert.Equal("sha256:2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881", target.Suites[0].Digest);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record WrittenRun(string Id, JsonObject Run, List<JsonObject> Lines, string Folder);

    private sealed record Job(string Out, AefJobResult Result, List<JsonObject> Events, List<WrittenRun> Runs, byte[] Plan)
    {
        public double SpentUsd => Events.LastOrDefault(e => (string?)e["kind"] == "spend.updated")?["spentUsd"]?.GetValue<double>() ?? 0;
    }

    private Job Run(JsonObject plan, JsonObject target, Func<string, string?>? environment = null, JsonObject? runner = null, AefTime? at = null) =>
        Run(Encoding.UTF8.GetBytes(plan.ToJsonString()), target, environment ?? (_ => null), runner ?? Manifest(), at);

    private Job Run(byte[] plan, JsonObject target, Func<string, string?> environment, JsonObject runner, AefTime? at = null)
    {
        var output = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var result = AefScriptedRunner.Run(plan, runner, AefScriptedTarget.Read(target), output, new AefJobOptions { At = at ?? Start, Environment = environment });
        var events = File.ReadAllLines(Path.Combine(output, "events.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(result.Events, events.Count);
        Assert.Equal((at ?? Start).ToString(), (string?)events[0]["at"]);   // the clock's start
        Assert.Equal(result.EndsAt.ToString(), (string?)events[^1]["at"]);
        var runs = result.Runs.Select(id =>
        {
            var folder = Path.Combine(output, "runs", id);
            return new WrittenRun(id, JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "run.json")))!.AsObject(),
                [.. File.ReadAllLines(Path.Combine(folder, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject())], folder);
        }).ToList();
        return new Job(output, result, events, runs, plan);
    }

    // [STRM-3] and [STRM-4] find nothing; each run is intact, scripted, of the job, and costs the sum of its cases.
    private static void AssertKeepsTheStreamRules(Job job)
    {
        var plan = JsonNode.Parse(job.Plan)!;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(job.Plan)).ToLowerInvariant();
        var stream = RunnerEventStream.Read(File.ReadAllBytes(Path.Combine(job.Out, "events.ndjson")));

        Assert.Empty(RunnerEventStream.Verify(stream, plan, digest));
        Assert.Empty(RunnerEventStream.Conform(stream, plan, AefRunStore.Open(Path.Combine(job.Out, "runs"))));
        foreach (var run in job.Runs)
        {
            var verification = AefRunVerifier.Verify(run.Folder);
            Assert.Equal((AefOutcome.Intact, 0), (verification.Outcome, verification.Problems.Count));
            Assert.Equal("scripted", (string?)run.Run["execution"]!["targetMode"]);
            Assert.Equal(job.Result.JobId, (string?)run.Run["provenance"]!["jobId"]);
        }
    }

    private static JsonNode Estimate(Job job)
    {
        var estimate = job.Events.Single(e => (string?)e["kind"] == "plan.estimated");
        return new JsonObject { ["cases"] = estimate["cases"]!.GetValue<long>(), ["usdLow"] = estimate["usdLow"]!.GetValue<double>(), ["usdHigh"] = estimate["usdHigh"]!.GetValue<double>() };
    }

    private static void AssertHoldsNone(string folder, IEnumerable<string> texts)
    {
        foreach (var (name, bytes) in Files(folder))
        {
            foreach (var text in texts)
            {
                Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) < 0, $"{name} holds {text}");
            }
        }
    }

    private static SortedDictionary<string, byte[]> Files(string folder) =>
        new(Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes), StringComparer.Ordinal);

    private static JsonObject Plan(double maxUsd = 5, long? cases = null, string? timeout = null, (string Ref, string Version)[]? suites = null)
    {
        var plan = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["planId"] = "plan-unit",
            ["subject"] = new JsonObject { ["ref"] = "agent:support/support-triage", ["version"] = "git:3f2a1c", ["deployment"] = "deployment:support/support-triage@dev" },
            ["suites"] = new JsonArray([.. (suites ?? []).Select(s => (JsonNode?)new JsonObject { ["ref"] = s.Ref, ["version"] = s.Version, ["lane"] = "quality" })]),
            ["limits"] = new JsonObject { ["maxUsd"] = maxUsd },
            ["contentCapture"] = "off",
            ["targetMode"] = "scripted",
            ["isolation"] = "process",
            ["provider"] = "local",
        };
        if (cases is { } n) plan["limits"]!["cases"] = n;
        if (timeout is not null) plan["limits"]!["timeout"] = timeout;
        return plan;
    }

    private static JsonObject Manifest() =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "jobs", "runner.json")))!.AsObject();

    private static JsonObject Credential(string name, string scheme, string path, string purpose) =>
        new() { ["name"] = name, ["scheme"] = scheme, ["path"] = path, ["purpose"] = purpose };

    private static JsonObject Target(long closeSeconds, params JsonObject[] suites) =>
        new() { ["suites"] = new JsonArray([.. suites]), ["closeSeconds"] = closeSeconds, ["priceTable"] = "unit-prices" };

    private static JsonObject Suite(string suiteRef, string version, params JsonObject[] cases) =>
        new()
        {
            ["ref"] = suiteRef, ["version"] = version,
            ["content"] = $"{suiteRef}@{version}\n" + string.Concat(cases.Select(c => (string)c["caseId"]! + "\n")),
            ["cases"] = new JsonArray([.. cases]),
        };

    // A failed or warn case has a severity (§9.2.1): severity when given, high otherwise.
    private static JsonObject Case(string caseId, string state = "passed", double usd = 0.25, double usdBound = 0.25, long seconds = 20, long secondsBound = 20, string? severity = null)
    {
        var json = new JsonObject { ["caseId"] = caseId, ["state"] = state };
        if (state is "failed" or "warn")
        {
            json["severity"] = severity ?? "high";
        }

        json["usd"] = usd;
        json["usdBound"] = usdBound;
        json["seconds"] = seconds;
        json["secondsBound"] = secondsBound;
        return json;
    }
}
