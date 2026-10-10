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
