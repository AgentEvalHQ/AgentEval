using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Runner;
using AgentEval.Results.Tests.Checkpoints;

namespace AgentEval.Results.Tests.Runner;

/// <summary>
/// [STRM-3] as the stream verifier reads a stream's bytes (D8 / W1-14: <c>event-invalid</c> for a line that is not a
/// valid event, <c>encoding</c> at <c>stream</c> for a framing defect), and [STRM-4] over runs found through an
/// <see cref="AefRunStore"/>.
/// </summary>
public sealed class RunnerEventStreamTests : IDisposable
{
    private const string Accepted = """{"schemaVersion":"1.0","seq":1,"kind":"job.accepted","jobId":"job-7","at":"2026-10-08T12:00:00Z","planId":"plan-1","planDigest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","runnerId":"runner-1"}"""; // DevSkim: ignore DS173237 — a placeholder digest

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aef-stream-{Guid.NewGuid():N}");

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

    // ------------------------------------------------------------------ [STRM-3]

    [Theory]
    [InlineData("""{"schemaVersion":"1.0","seq":2,""")]                                                                                  // not JSON
    [InlineData("""{"schemaVersion":"1.0","seq":2,"seq":2,"kind":"spend.updated","jobId":"job-7","at":"2026-10-08T12:00:01Z","spentUsd":0.1}""")]   // a member twice
    [InlineData("""{"schemaVersion":"1.0","seq":"2","kind":"spend.updated","jobId":"job-7","at":"2026-10-08T12:00:01Z","spentUsd":0.1}""")]   // not the schema's
    [InlineData("""{"schemaVersion":"1.0","seq":2,"kind":"spend.updated","jobId":"job-7","at":"2026-02-31T12:00:01Z","spentUsd":0.1}""")]    // no such time
    [InlineData("[]")]
    public void ALineThatIsNotAValidEvent_IsEventInvalid_AndTakesNoPartInTheOtherChecks(string line)
    {
        var problems = Verify(Accepted, line, Spend(3, 0.2), Cancelled(4));

        Assert.Equal(["event:2 event-invalid"], problems);
    }

    [Fact]
    public void ALineBeyondADepthLimit_IsEventInvalid()
    {
        // ext at depth 2, then 63 arrays: 65 deep ([ENC-17] allows 64).
        var deep = """{"schemaVersion":"1.0","seq":2,"kind":"spend.updated","jobId":"job-7","at":"2026-10-08T12:00:01Z","spentUsd":0.1,"ext":{"x":"""
                   + new string('[', 63) + new string(']', 63) + "}}";

        Assert.Equal(["event:2 event-invalid"], Verify(Accepted, deep, Spend(3, 0.2), Cancelled(4)));
    }

    [Fact]
    public void TheEventAfterAnInvalidLine_IsNotCheckedForSeq_AndTheCountGoesOnFromTheValueItWrites()
    {
        // Line 3 writes seq 7 after an invalid line: not reported; line 4 must then be 8.
        Assert.Equal(["event:2 event-invalid", "event:4 seq"], Verify(Accepted, "[]", Spend(7, 0.1), Spend(9, 0.2), Cancelled(10)));
        Assert.Equal(["event:2 event-invalid"], Verify(Accepted, "[]", Spend(7, 0.1), Spend(8, 0.2), Cancelled(9)));

        // Two invalid lines in a row: the first valid event after them is not checked either.
        Assert.Equal(["event:2 event-invalid", "event:3 event-invalid"], Verify(Accepted, "[]", "{}", Spend(5, 0.1), Cancelled(6)));
    }

    [Fact]
    public void TheFirstEvent_IsTheFirstValidOne_ForFirstAndForTheJobId()
    {
        Assert.Equal(["event:1 event-invalid"], Verify("[]", Accepted, Cancelled(2)));
        Assert.Equal(["event:1 event-invalid", "event:2 first"], Verify("[]", Spend(1, 0.1), Cancelled(2)));
        Assert.Equal(["event:1 event-invalid", "event:3 job-id"], Verify("[]", Accepted, Cancelled(2).Replace("job-7", "job-8", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("\r\n")]   // a CR
    [InlineData("\n\n")]   // a blank line
    public void FinishedLinesThatBreakTheFraming_AreOneEncodingProblemAtStream(string separator)
    {
        var bytes = Encoding.UTF8.GetBytes(Accepted + separator + Cancelled(2) + "\n");

        Assert.Equal([("stream", "encoding")], RunnerEventStream.Verify(RunnerEventStream.Read(bytes)));
    }

    [Fact]
    public void AByteOrderMark_IsAnEncodingProblem_AndACrInTheLineStillBeingWritten_IsNotRead()
    {
        var bom = Encoding.UTF8.GetBytes("﻿" + Accepted + "\n" + Cancelled(2) + "\n");
        Assert.Equal([("stream", "encoding")], RunnerEventStream.Verify(RunnerEventStream.Read(bom)));

        var open = Encoding.UTF8.GetBytes(Accepted + "\n" + Cancelled(2) + "\r");   // the last line has no LF yet ([STRM-2])
        Assert.Equal([("stream", "no-terminal")], RunnerEventStream.Verify(RunnerEventStream.Read(open)));
    }

    [Fact]
    public void AStreamOfMoreLinesThanTheLimit_IsOneLimitProblemAtStream()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{}\n", 1_000_001)));

        Assert.Equal([("stream", "limit")], RunnerEventStream.Verify(RunnerEventStream.Read(bytes)));
    }

    // ------------------------------------------------------------------ [STRM-4]

    [Theory]
    [InlineData("R-1", "R-2", "R-3")]
    [InlineData("R-3", "R-2", "R-1")]
    public void TheJobsCost_IsSummedExactly_WhateverTheOrderTheRunsAreNamedIn(string first, string second, string third)
    {
        // $0.10 + $0.20 + $0.30 on a plan of $0.60: summed in order as binary64 it is 0.6000000000000001, exactly and
        // rounded once it is 0.6, within the budget (W4-2).
        JobRun("R-1", 0.1);
        JobRun("R-2", 0.2);
        JobRun("R-3", 0.3);

        Assert.Empty(Conform(Plan(maxUsd: 0.6), Announce("R-1", "R-2", "R-3"), Sealed(9, first, second, third)));
        Assert.Equal(["job over-budget"], Conform(Plan(maxUsd: 0.59), Announce("R-1", "R-2", "R-3"), Sealed(9, first, second, third)));
    }

    [Fact]
    public void AnEvidenceProducedLineThatIsInvalid_AnnouncesNothing()
    {
        JobRun("R-1", 1.0);
        var invalid = Announce("R-1")[0].Replace("\"seq\":2", "\"seq\":\"2\"", StringComparison.Ordinal);

        Assert.Equal(["run:R-1 run-hash"], Conform(Plan(), [invalid], Sealed(3, "R-1")));
    }

    [Fact]
    public void AStreamWithAFramingDefect_GivesNoEvents_SoNoRunIsChecked()
    {
        JobRun("R-1", 9.0);
        var lines = new[] { Accepted }.Concat(Announce("R-1")).Append(Sealed(3, "R-1"));
        var bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");

        Assert.Empty(RunnerEventStream.Conform(RunnerEventStream.Read(bytes), Plan(), AefRunStore.Open(_root)));
    }

    [Fact]
    public void TheJobsCases_AreTheDistinctCaseIdsOfLinesWithoutAParent_AcrossItsRuns()
    {
        // k2 has a child line, of its own case ([RES-5], round 6: a tree belongs to one case, so a child never adds one).
        var a = JobRun("R-1", 0.1, b =>
        {
            b.Line("k1", "p", "passed", trial: 0).Line("k1", "p", "passed", trial: 1).Line("k1", "p", "passed", rollup: (2, 2))
                .Line("k2", "p", "failed", severity: "low").Line("k2", "p/child", "failed", severity: "low", parentCaseId: "k2", parentPath: "p");
            b.Results[^2]["aggregation"] = JsonNode.Parse($$"""
                {"strategy": "Min", "rulePath": "severity", "measured": 1, "total": 1, "unmeasured": {"not_measured": 0, "not_applicable": 0, "skipped": 0, "error": 0},
                 "decisive": ["{{b.Results[^1]["resultId"]}}"]}
                """);
            b.Results[^1]["component"] = new JsonObject { ["weight"] = 1, ["required"] = true };
            return b;
        });
        var b = JobRun("R-2", 0.1, b => b.Line("k2", "p", "passed").Line("k3", "p", "passed"));
        Assert.Empty(AgentEval.Results.Integrity.AefRunVerifier.Verify(a).Problems);
        Assert.Empty(AgentEval.Results.Integrity.AefRunVerifier.Verify(b).Problems);

        Assert.Empty(Conform(Plan(cases: 3), Announce("R-1", "R-2"), Sealed(9, "R-1", "R-2")));
        Assert.Equal(["job over-cases"], Conform(Plan(cases: 2), Announce("R-1", "R-2"), Sealed(9, "R-1", "R-2")));
    }

    [Fact]
    public void ACase_IsItsRunsSuiteWithItsCaseId_SoTwoSuitesThatShareCaseIds_CountTwice()
    {
        // [STRM-4] over-cases (round 7, [PLAN-8]): k1 and k2 in two suites are four cases, not two.
        JobRun("R-1", 0.1, b => b.Line("k1", "p", "passed").Line("k2", "p", "passed"));
        JobRun("R-2", 0.1, b =>
        {
            b.Run["suite"] = new JsonObject { ["ref"] = "suite:shop/regression", ["version"] = "1" };
            return b.Line("k1", "p", "passed").Line("k2", "p", "failed", severity: "low");
        });
        var plan = Plan(cases: 4);
        plan["suites"]!.AsArray().Add(new JsonObject { ["ref"] = "suite:shop/regression", ["version"] = "1", ["lane"] = "regression" });

        Assert.Empty(Conform(plan, Announce("R-1", "R-2"), Sealed(9, "R-1", "R-2")));
        plan["limits"]!["cases"] = 3;
        Assert.Equal(["job over-cases"], Conform(plan, Announce("R-1", "R-2"), Sealed(9, "R-1", "R-2")));
    }

    [Theory]
    [InlineData(null, null, true)]                                  // a plan without a mode asks for live; a manifest without modes gives it
    [InlineData("scripted", null, false)]                           // a manifest without targetModes gives live only
    [InlineData(null, """["scripted"]""", false)]
    [InlineData("live", """["live", "scripted"]""", true)]
    [InlineData("scripted", """["live", "scripted"]""", true)]
    [InlineData("mocked", """["live", "scripted"]""", false)]
    public void ARunnerTakesAPlan_OnlyWhenItGivesThePlansTargetMode_Plan7(string? planMode, string? runnerModes, bool takes)
    {
        var plan = Plan();
        if (planMode is not null) plan["targetMode"] = planMode;
        var runner = new JsonObject
        {
            ["schemaVersion"] = "1.0", ["runnerId"] = "runner-1", ["kind"] = "local", ["os"] = "linux",
            ["runtime"] = new JsonObject { ["name"] = "any", ["version"] = "1" }, ["providers"] = new JsonArray("local"),
            ["tags"] = new JsonArray(), ["version"] = "1.0.0",
        };
        if (runnerModes is not null) runner["targetModes"] = JsonNode.Parse(runnerModes);

        Assert.Equal(takes, RunnerEventStream.Matches(plan, runner));
        Assert.Equal(takes, RunnerEventStream.WhyNot(plan, runner) is null);
    }

    [Fact]
    public void ARunnersReasonForNotTakingAPlan_NamesACredentialByItsNameNeverItsPath()
    {
        var plan = Plan();
        plan["credentialRefs"] = new JsonArray(new JsonObject { ["name"] = "KEY", ["scheme"] = "hsm", ["path"] = "secret/where-it-lives", ["purpose"] = "subject" });
        var runner = new JsonObject { ["providers"] = new JsonArray("local"), ["tags"] = new JsonArray() };

        var why = RunnerEventStream.WhyNot(plan, runner);

        Assert.Contains("KEY", why, StringComparison.Ordinal);
        Assert.DoesNotContain("secret/where-it-lives", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A B", "", true)]              // no judge: within
    [InlineData("A B", null, true)]            // no judges member: within
    [InlineData("A B", "B", true)]             // some of the plan's
    [InlineData("A B", "A B", true)]           // all, in the plan's order
    [InlineData("A B", "B A", false)]          // out of the plan's order
    [InlineData("A B", "A A", false)]          // more often than the plan names it
    [InlineData("A A B", "A A", true)]         // a judge the plan names twice may be named twice
    [InlineData("A A B", "A A A", false)]      // never more
    [InlineData("A B", "A C", false)]          // one the plan does not name
    [InlineData("A B", "A-other-rubric", false)]    // the plan's model with another rubric digest
    [InlineData("A B", "A-other-provider", false)]  // the plan's model served by another provider
    [InlineData("P", "A", true)]               // a plan judge that names no provider leaves it to the runner
    [InlineData("P", "A-other-provider", true)]
    [InlineData("A", "P", false)]              // a plan judge that names one: the run's names it too
    [InlineData("M", "A", true)]               // a plan judge that names only its model leaves provider and rubric to the runner
    [InlineData("M", "A-other-rubric", true)]
    [InlineData("M", "C", false)]              // but not the model
    [InlineData(null, "A", false)]             // a plan that names no judges allows none
    [InlineData("", "A", false)]
    [InlineData(null, null, true)]
    [InlineData("", "", true)]
    public void ARunsJudges_AreThePlansWithSomeLeftOut_InOrder(string? planJudges, string? runJudges, bool within)
    {
        // [STRM-4] judges: a run names the models that graded it ([RUN-9]); the run's, in order, are the plan's with some
        // left out (none, or all, among them); a run's judge is a plan's with its model and rubric digest and, when the
        // plan judge names a provider, that provider.
        static JsonObject Judge(string name) => name switch
        {
            "A" => new() { ["model"] = "gpt-5.1", ["provider"] = "azure.ai.openai", ["rubricDigest"] = "sha256:" + new string('a', 64) },
            "B" => new() { ["model"] = "llama-3.3-70b", ["provider"] = "local", ["rubricDigest"] = "sha256:" + new string('b', 64) },
            "C" => new() { ["model"] = "gpt-4o-mini", ["provider"] = "openai", ["rubricDigest"] = "sha256:" + new string('a', 64) },
            "A-other-provider" => new() { ["model"] = "gpt-5.1", ["provider"] = "openai", ["rubricDigest"] = "sha256:" + new string('a', 64) },
            "P" => new() { ["model"] = "gpt-5.1", ["rubricDigest"] = "sha256:" + new string('a', 64) },
            "M" => new() { ["model"] = "gpt-5.1" },
            _ => new() { ["model"] = "gpt-5.1", ["provider"] = "azure.ai.openai", ["rubricDigest"] = "sha256:" + new string('c', 64) },
        };
        static JsonArray Judges(string names) => new([.. names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => (JsonNode?)Judge(n))]);

        JobRun("R-1", 0.1, b =>
        {
            if (runJudges is not null)
            {
                b.Run["judges"] = Judges(runJudges);
            }

            return b.Line("c1", "p", "passed");
        });
        var plan = Plan();
        if (planJudges is not null)
        {
            plan["judges"] = Judges(planJudges);
        }

        Assert.Equal(within ? Array.Empty<string>() : ["run:R-1 judges"], Conform(plan, Announce("R-1"), Sealed(3, "R-1")));
    }

    [Fact]
    public void APlanJudgeThatNamesOnlyItsModel_LeavesProviderAndRubricToTheRunner_ButNotTheModel()
    {
        JobRun("R-1", 0.1, b =>
        {
            b.Run["judges"] = new JsonArray(new JsonObject { ["model"] = "gpt-5.1", ["provider"] = "openai", ["rubricDigest"] = "sha256:" + new string('a', 64) });
            return b.Line("c1", "p", "passed");
        });
        var plan = Plan();
        plan["judges"] = new JsonArray(new JsonObject { ["model"] = "gpt-5.1" });

        Assert.Empty(Conform(plan, Announce("R-1"), Sealed(3, "R-1")));

        plan["judges"] = new JsonArray(new JsonObject { ["model"] = "gpt-5" });   // only the model is always compared
        Assert.Equal(["run:R-1 judges"], Conform(plan, Announce("R-1"), Sealed(3, "R-1")));
    }

    [Fact]
    public void ARunWithoutACost_IsANoCostProblem_AndARunNoFolderHolds_IsMissing()
    {
        JobRun("R-1", null);

        Assert.Equal(["run:R-1 no-cost", "run:R-2 run-missing"], Conform(Plan(), Announce("R-1", "R-2"), Sealed(9, "R-1", "R-2")));
    }

    [Fact]
    public void ARunNamedButNeverAnnounced_IsARunHashProblem()
    {
        JobRun("R-1", 1.0);

        Assert.Equal(["run:R-1 run-hash"], Conform(Plan(), [], Sealed(2, "R-1")));
    }

    // ------------------------------------------------------------------ helpers

    private static string Spend(int seq, double usd) =>
        $$"""{"schemaVersion":"1.0","seq":{{seq}},"kind":"spend.updated","jobId":"job-7","at":"2026-10-08T12:00:0{{seq % 10}}Z","spentUsd":{{usd.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""";

    private static string Cancelled(int seq) =>
        $$"""{"schemaVersion":"1.0","seq":{{seq}},"kind":"job.cancelled","jobId":"job-7","at":"2026-10-08T12:00:30Z","reason":"stopped"}""";

    private static List<string> Verify(params string[] lines)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        return [.. RunnerEventStream.Verify(RunnerEventStream.Read(bytes)).Select(p => $"{p.Where} {p.Problem}")];
    }

    private string JobRun(string runId, double? cost, Func<LaneRunBuilder, LaneRunBuilder>? lines = null)
    {
        var builder = new LaneRunBuilder(runId);
        builder.Run["startedAt"] = "2026-10-08T12:00:10Z";
        builder.Run["endedAt"] = "2026-10-08T12:00:20Z";
        builder.Run["contentCapture"] = "off";
        builder.Run["suite"] = new JsonObject { ["ref"] = "suite:shop/memory", ["version"] = "3" };
        builder.Run["provenance"] = new JsonObject
        {
            ["planId"] = "plan-1", ["planDigest"] = new string('a', 64), ["jobId"] = "job-7", ["runnerId"] = "runner-1",
        };
        builder.CostUsd = cost;
        (lines ?? (b => b.Line("c1", "p", "passed")))(builder);
        return builder.Write(_root);
    }

    private List<string> Announce(params string[] runIds) =>
        [.. runIds.Select((id, i) => $$"""{"schemaVersion":"1.0","seq":{{i + 2}},"kind":"evidence.produced","jobId":"job-7","at":"2026-10-08T12:00:2{{i}}Z","runId":"{{id}}","runHash":"{{(Directory.Exists(Path.Combine(_root, id)) ? LaneRunBuilder.RunHashOf(Path.Combine(_root, id)) : new string('0', 64))}}"}""")];

    private static string Sealed(int seq, params string[] runIds) =>
        $$"""{"schemaVersion":"1.0","seq":{{seq}},"kind":"job.sealed","jobId":"job-7","at":"2026-10-08T12:00:40Z","runs":[{{string.Join(",", runIds.Select(r => $"\"{r}\""))}}]}""";

    private static JsonObject Plan(double maxUsd = 10, int? cases = null)
    {
        var plan = new JsonObject
        {
            ["schemaVersion"] = "1.0", ["planId"] = "plan-1",
            ["subject"] = new JsonObject { ["ref"] = LaneRunBuilder.Subject, ["version"] = "v7" },
            ["suites"] = new JsonArray(new JsonObject { ["ref"] = "suite:shop/memory", ["version"] = "3", ["lane"] = "quality" }),
            ["limits"] = new JsonObject { ["maxUsd"] = maxUsd },
            ["contentCapture"] = "off", ["isolation"] = "process", ["provider"] = "local",
        };
        if (cases is { } n) plan["limits"]!["cases"] = n;
        return plan;
    }

    private List<string> Conform(JsonObject plan, IEnumerable<string> announcements, string sealedEvent)
    {
        var lines = new[] { Accepted }.Concat(announcements).Append(sealedEvent);
        var stream = RunnerEventStream.Read(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        return [.. RunnerEventStream.Conform(stream, plan, AefRunStore.Open(_root)).Select(p => $"{p.Where} {p.Problem}")];
    }
}
