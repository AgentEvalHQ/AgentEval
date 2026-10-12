// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefEvalWriterTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("aef-eval");

    public void Dispose() => AefTestRuns.Delete(_out);

    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static TestSummary Summary() => new("demo",
    [
        new TestResult { TestName = "greets", Passed = true, Score = 90, Performance = new() { StartTime = Start, EndTime = Start.AddSeconds(2), PromptTokens = 100, CompletionTokens = 20, EstimatedCost = 0.001m, ModelUsed = "gpt-x" } },
        new TestResult { TestName = "refunds", Passed = false, Score = 40, ActualOutput = "a reply that must not be written" },
        new TestResult { TestName = "What is my secret prompt", Passed = false, Error = new InvalidOperationException("the judge said: secret text") },
    ]);

    private static AefEvalRunOptions Options(AefTargetMode mode = AefTargetMode.Live) => new()
    {
        SubjectRef = "model:gpt-x",
        SubjectVersion = "2026-10-01",
        Endpoint = "https://user:pass@example.com/v1?api-key=k",
        JudgeModel = "judge-x",
        TargetMode = mode,
        DatasetName = "datasets/demo.yaml",
        DatasetBytes = Encoding.UTF8.GetBytes("a: 1\r\nb: 2\r\n"),
    };

    [Fact]
    public void Session_DisposedBeforeCompletion_LeavesRunningEvidenceOnDisk()
    {
        string dir;
        using (var session = AefEvalWriter.Start(_out, Options(), Start))
        {
            dir = session.Directory;
            Assert.Empty(File.ReadAllBytes(Path.Combine(dir, "results.ndjson")));
            session.Add(Summary().Results[0], "c1");
            Assert.Single(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
            Assert.Equal("running", (string?)AefTestRuns.Document(dir, "run.json")["status"]);
            Assert.False(File.Exists(Path.Combine(dir, "seal.json")));
        }
        Assert.Single(File.ReadAllLines(Path.Combine(dir, "results.ndjson")));
        Assert.Equal("running", (string?)AefTestRuns.Document(dir, "run.json")["status"]);
        Assert.False(File.Exists(Path.Combine(dir, "summary.json")));
    }

    [Fact]
    public void Session_PartialTrials_AreVisibleBeforeAbortAndRollUpUnderOriginalPolicy()
    {
        using var session = AefEvalWriter.Start(_out, Options(), Start, plannedTrials: 3, requirePasses: 2);
        session.Add(Summary().Results[0], "c1");
        var before = JsonNode.Parse(File.ReadAllLines(Path.Combine(session.Directory, "results.ndjson")).Single())!;
        Assert.Equal(0, (int)before["trial"]!);
        var (dir, _) = session.Complete(Start.AddSeconds(5), abortReason: "cancelled");
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("error", (string?)Assert.Single(lines, l => l["trials"] is not null)["state"]);
        Assert.Equal(3, (int)AefTestRuns.Document(dir, "run.json")["suite"]!["executionPolicy"]!["trialsPerCase"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
        Assert.Throws<InvalidOperationException>(() => session.Add(Summary().Results[0], "c2"));
    }

    [Fact]
    public void Session_LateMetrics_PreserveHarnessVerdictAndTypedAbsencesWithoutContent()
    {
        using var session = AefEvalWriter.Start(_out, Options() with { GateRule = "all-cases-pass" }, Start);
        session.Add(Summary().Results[0], "c1", 70);
        session.AddMetrics("c1",
        [
            AgentEval.Core.MetricResult.Fail("code_quality", "secret explanation", 25),
            AgentEval.Core.MetricResult.NotMeasured("llm_context", "secret missing context"),
        ]);
        var lines = File.ReadAllLines(Path.Combine(session.Directory, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("passed", (string?)lines[0]["state"]);
        Assert.Equal(1, (int)lines[0]["aggregation"]!["measured"]!);
        Assert.Equal(2, (int)lines[0]["aggregation"]!["total"]!);
        Assert.Equal(1, (int)lines[0]["aggregation"]!["unmeasured"]!["not_measured"]!);
        Assert.Equal("case/metrics/code_quality", (string?)lines[1]["path"]);
        Assert.Equal("failed", (string?)lines[1]["state"]);
        Assert.Equal("not_measured", (string?)lines[2]["state"]);
        Assert.Null(lines[2]["scores"]);
        var (dir, _) = session.Complete(Start.AddSeconds(5));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)));
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal("ship", (string?)gate["outcome"]);
    }

    [Fact]
    public void Write_GivesASealedRun_IntactUnderBothVerifiers_WithOneRootPerCase()
    {
        var (dir, runHash) = AefEvalWriter.Write(_out, Summary(), ["c1", "c2", "c3"], Options(), Start, Start.AddSeconds(5));

        var verification = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        Assert.Equal(64, runHash.Length);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
        }

        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(["passed", "failed", "error"], lines.Select(l => (string?)l["state"]));
        Assert.Equal(["c1", "c2", "c3"], lines.Select(l => (string?)l["caseId"]));
        Assert.All(lines, l => Assert.Equal("eval", (string?)l["lane"]));
        Assert.Equal("the evaluation threw InvalidOperationException", (string?)lines[2]["reason"]);
    }

    [Fact]
    public void Write_RecordsTheTargetMode_TheSuiteDigest_AndNoCredentialOrContent()
    {
        var (dir, _) = AefEvalWriter.Write(_out, Summary(), ["c1", "c2", "c3"], Options(AefTargetMode.Mocked), Start, Start.AddSeconds(5));

        var run = AefTestRuns.Document(dir, "run.json");
        Assert.Equal("mocked", (string?)run["execution"]!["targetMode"]);
        Assert.Equal("off", (string?)run["contentCapture"]);
        Assert.Equal(AefEvalWriter.DatasetDigest(Encoding.UTF8.GetBytes("a: 1\nb: 2\n")), (string?)run["suite"]!["digest"]);
        var all = string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("user:pass", all, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key", all, StringComparison.Ordinal);
        Assert.DoesNotContain("must not be written", all, StringComparison.Ordinal);
        Assert.DoesNotContain("secret text", all, StringComparison.Ordinal);
        Assert.DoesNotContain("secret prompt", all, StringComparison.Ordinal);   // a test name can be the input's start
    }

    [Fact]
    public void Write_TotalsTheRunsUsage_PerRoleAndModel_WithTheJudgesAsReported()
    {
        var options = Options() with { JudgeUsage = new AefUsage { Role = AefUsageRole.Judge, Model = "judge-x", InputTokens = 500, OutputTokens = 40 } };

        PerformanceMetrics Perf(int input) => new() { StartTime = Start, EndTime = Start.AddSeconds(1), PromptTokens = input, CompletionTokens = 5, ModelUsed = "gpt-x" };
        var complete = new TestSummary("demo",
        [
            new TestResult { TestName = "a", Passed = true, Score = 90, Performance = Perf(100) },
            new TestResult { TestName = "b", Passed = true, Score = 90, Performance = Perf(30) },
        ]);

        var (dir, _) = AefEvalWriter.Write(_out, complete, ["c1", "c2"], options, Start, Start.AddSeconds(5));
        var (partialDir, _) = AefEvalWriter.Write(Path.Combine(_out, "partial"), Summary(), ["c1", "c2", "c3"], options, Start, Start.AddSeconds(5));

        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        var usage = AefTestRuns.Document(dir, "summary.json")["usage"]!.AsArray();
        var agent = usage.Single(u => (string?)u!["role"] == "agent")!;
        Assert.Equal("gpt-x", (string?)agent["model"]);
        Assert.Equal(130, (long)agent["gen_ai.usage.input_tokens"]!);
        // Two of the three cases reported no performance: the agent's total is unknown, not the one case's figure.
        Assert.DoesNotContain(AefTestRuns.Document(partialDir, "summary.json")["usage"]!.AsArray(), u => (string?)u!["role"] == "agent");
        var judge = usage.Single(u => (string?)u!["role"] == "judge")!;
        Assert.Equal(500, (long)judge["gen_ai.usage.input_tokens"]!);
        Assert.Equal(40, (long)judge["gen_ai.usage.output_tokens"]!);
    }

    [Fact]
    public void Write_RecordsTheGate_NoShip_NamingTheCasesThatDidNotPass()
    {
        var (dir, _) = AefEvalWriter.Write(_out, Summary(), ["c1", "c2", "c3"], Options() with { GateRule = "all-cases-pass" }, Start, Start.AddSeconds(5));

        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
        }

        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal("no_ship", (string?)gate["outcome"]);
        Assert.Equal(1, (int)gate["exitCode"]!);
        Assert.Equal(2, gate["decisive"]!.AsArray().Count);   // c2 failed, c3 errored
        Assert.Equal(3, gate["inputs"]!["results"]!.AsArray().Count);
    }

    [Fact]
    public void Write_RecordsNoGate_WhenTheCommandHasNone() =>
        Assert.False(File.Exists(Path.Combine(AefEvalWriter.Write(_out, Summary(), ["c1", "c2", "c3"], Options(), Start, Start.AddSeconds(5)).Directory, "gates.ndjson")));

    [Fact]
    public void Write_RecordsAJudgeThatProducedNoVerdict_AsAnError_NeverAsAFailure()
    {
        var summary = new TestSummary("demo",
        [
            new TestResult { TestName = "judged", Passed = false, Score = 0, JudgeFailed = true, CriteriaResults = [] },
        ]);

        var (dir, _) = AefEvalWriter.Write(_out, summary, ["c1"], Options(), Start, Start.AddSeconds(1));

        var line = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Single())!;
        Assert.Equal("error", (string?)line["state"]);
        Assert.Null(line["scores"]);
        Assert.StartsWith("the judge produced no verdict", (string?)line["reason"]);
        Assert.Null(AefTestRuns.Document(dir, "run.json")["judges"]);   // it graded nothing ([RUN-9])
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
    }

    [Fact]
    public void Write_NamesTheJudge_OnlyWhenItGradedACase()
    {
        var graded = new TestSummary("demo", [new TestResult { TestName = "t", Passed = true, Score = 80, CriteriaResults = [] }]);
        var ungraded = new TestSummary("demo", [new TestResult { TestName = "t", Passed = true, Score = 100 }]);

        var (withJudge, _) = AefEvalWriter.Write(Path.Combine(_out, "a"), graded, ["c1"], Options(), Start, Start.AddSeconds(1));
        var (withoutJudge, _) = AefEvalWriter.Write(Path.Combine(_out, "b"), ungraded, ["c1"], Options(), Start, Start.AddSeconds(1));

        Assert.Equal("judge-x", (string?)AefTestRuns.Document(withJudge, "run.json")["judges"]![0]!["model"]);
        Assert.Null(AefTestRuns.Document(withoutJudge, "run.json")["judges"]);
    }

    [Fact]
    public void Write_RecordsCriteriaAndAssertionsAsChildren_BesideTheCasesOwnVerdict()
    {
        var summary = new TestSummary("demo",
        [
            new TestResult
            {
                TestName = "judged", Passed = true, Score = 85,
                CriteriaResults =
                [
                    new AgentEval.Core.CriterionResult { Criterion = "mentions the refund policy", Met = true, Explanation = "the reply quotes it" },
                    new AgentEval.Core.CriterionResult { Criterion = "is polite", Met = false },
                ],
                AssertionResults =
                [
                    AgentEval.Output.AssertionResult.Pass("Contain(\"the refund window is 30 days\")"),
                    AgentEval.Output.AssertionResult.Fail("HaveCallCount", "Expected 2 tool call(s), but 3 were made."),
                    AgentEval.Output.AssertionResult.Undecidable("NeverCallTool(PlaceOrder)", "not a declared tool"),
                ],
            },
        ]);

        var (dir, _) = AefEvalWriter.Write(_out, summary, ["c1"], Options(), Start, Start.AddSeconds(1), passingScores: [70]);

        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
        }

        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        var root = lines.Single(l => (string?)l["path"] == "case");
        Assert.Equal("Own", (string?)root["aggregation"]!["strategy"]);
        Assert.Equal("threshold", (string?)root["aggregation"]!["rulePath"]);
        Assert.Equal(70.0, (double)root["aggregation"]!["threshold"]!);
        Assert.Equal(5, (int)root["aggregation"]!["total"]!);
        var children = lines.Where(l => l["parentResultId"] is not null).ToDictionary(l => (string)l["path"]!, l => (string?)l["state"]);
        Assert.Equal("passed", children["case/criteria/1"]);
        Assert.Equal("failed", children["case/criteria/2"]);
        Assert.Equal("passed", children["case/assertions/1"]);
        Assert.Equal("failed", children["case/assertions/2"]);
        Assert.Equal("inconclusive", children["case/assertions/3"]);
        var all = string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("refund policy", all, StringComparison.Ordinal);   // criterion text is the suite's, not written
        Assert.DoesNotContain("quotes it", all, StringComparison.Ordinal);       // the judge's explanation is content
        Assert.DoesNotContain("were made", all, StringComparison.Ordinal);       // an assertion's message may quote the response
        Assert.DoesNotContain("refund window", all, StringComparison.Ordinal);   // nor its name the expected text
    }

    private static TestResult Pass(int score = 90) => new() { TestName = "t", Passed = true, Score = score };
    private static TestResult Fail(int score = 20) => new() { TestName = "t", Passed = false, Score = score };
    private static TestResult Broken() => new() { TestName = "t", Passed = false, Error = new TimeoutException() };

    [Fact]
    public void WriteTrials_WritesALinePerTrial_AndARollupPerCase_IntactUnderBothVerifiers()
    {
        // requirePasses 2 of 3: c1 passes 3/3; c2 passes 1/3 with no unmeasured trial, so it fails and is flaky;
        // c3 passes 1/3 with one error, which could have been the second pass, so it is undecided: an error.
        IReadOnlyList<IReadOnlyList<TestResult>> trials =
        [
            [Pass(), Pass(80), Pass(70)],
            [Pass(), Fail(), Fail()],
            [Pass(), Fail(), Broken()],
        ];

        var (dir, _) = AefEvalWriter.WriteTrials(_out, trials, ["c1", "c2", "c3"], 2, Options(), Start, Start.AddSeconds(9));

        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
        }

        var policy = AefTestRuns.Document(dir, "run.json")["suite"]!["executionPolicy"]!;
        Assert.Equal(3, (int)policy["trialsPerCase"]!);
        Assert.Equal(2, (int)policy["requirePasses"]!);
        Assert.Equal("AtLeast", (string?)policy["aggregation"]);

        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(9, lines.Count(l => l["trial"] is not null));
        var rollups = lines.Where(l => l["trials"] is not null).ToDictionary(l => (string)l["caseId"]!);
        Assert.Equal(["passed", "failed", "error"], new[] { "c1", "c2", "c3" }.Select(c => (string?)rollups[c]["state"]));
        Assert.Equal(80.0, (double)rollups["c1"]["scores"]![0]!["value"]!);   // the mean of 90, 80, 70
        Assert.False((bool)rollups["c2"]["trials"]!["agree"]!);
        Assert.Equal(1, (int)rollups["c3"]["trials"]!["passed"]!);
        Assert.StartsWith("1 of 3 trials were not measured", (string?)rollups["c3"]["reason"]);
        var pass = AefTestRuns.Document(dir, "summary.json")["lanes"]![0]!["metrics"]![0]!;   // rollups only (SUM-3)
        Assert.Equal(0.5, (double)pass["value"]!);   // c1 passed, c2 failed
        Assert.Equal(1, (int)pass["notMeasured"]!);   // c3 is undecided
    }

    [Theory]
    [InlineData(0.8, 5, 4)]
    [InlineData(0.8, 3, 3)]
    [InlineData(0.5, 4, 2)]
    [InlineData(1.0, 10, 10)]
    [InlineData(0.01, 3, 1)]
    public void RequirePasses_IsTheLeastPassCountThatMeetsTheThreshold(double threshold, int trials, int expected) =>
        Assert.Equal(expected, AefEvalWriter.RequirePasses(threshold, trials));

    [Fact]
    public void RequirePasses_RefusesAThresholdThatZeroPassesMeet() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AefEvalWriter.RequirePasses(0, 5));

    [Theory]
    [InlineData("agent:a", AefSubjectKind.Agent)]
    [InlineData("workflow:w", AefSubjectKind.Workflow)]
    [InlineData("model:m", AefSubjectKind.Model)]
    [InlineData("endpoint:e", AefSubjectKind.Endpoint)]
    [InlineData("mcp-server:s", AefSubjectKind.McpServer)]
    [InlineData("tool:t", AefSubjectKind.Other)]
    [InlineData("noprefix", AefSubjectKind.Other)]
    public void KindOf_TakesTheRefsKind_OrOther(string subjectRef, AefSubjectKind kind) =>
        Assert.Equal(kind, AefEvalWriter.KindOf(subjectRef));

    [Fact]
    public void DatasetDigest_DoesNotDependOnLineEndings() =>
        Assert.Equal(AefEvalWriter.DatasetDigest(Encoding.UTF8.GetBytes("x\r\ny\r\n")), AefEvalWriter.DatasetDigest(Encoding.UTF8.GetBytes("x\ny\n")));
}
