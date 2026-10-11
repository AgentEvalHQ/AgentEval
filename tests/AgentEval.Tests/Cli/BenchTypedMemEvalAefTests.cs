// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Memory.External.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// TypedMemEval in AEF (owner's decision, 2026-10-11): a line per question, scored with no verdict (the benchmark
/// publishes no pass mark), score 1 for correct, the typed outcome beside it; no question or answer text.
/// </summary>
public sealed class BenchTypedMemEvalAefTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "aef-tme-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    private static QuestionResult Question(string id, TypedMemEvalOutcome? outcome, QuestionExecutionStatus status = QuestionExecutionStatus.Completed) => new()
    {
        QuestionId = id,
        QuestionType = "recency",
        Question = "secret question text",
        GoldAnswer = "secret gold answer",
        AgentResponse = "secret agent answer",
        Correct = outcome == TypedMemEvalOutcome.Correct,
        RawScore = null,
        ExecutionStatus = status,
        TypedOutcome = outcome is { } o ? new TypedMemEvalQuestionDetail { Shape = "recency", Outcome = o, Attribution = TypedMemEvalEvidenceAttribution.Unobserved } : null,
    };

    private static ExternalBenchmarkResult Result() => new()
    {
        BenchmarkId = "typedmemeval",
        BenchmarkName = "TypedMemEval",
        OverallAccuracy = 25,   // a percentage, as the runner reports it
        TaskAveragedAccuracy = null,
        PerTypeResults = [],
        Duration = TimeSpan.FromSeconds(3),
        Options = new ExternalBenchmarkOptions(),
        QuestionResults =
        [
            Question("q1", TypedMemEvalOutcome.Correct),
            Question("q2", TypedMemEvalOutcome.Wrong),
            Question("q3", TypedMemEvalOutcome.Abstained),
            Question("q4", TypedMemEvalOutcome.Premature),
            Question("q5", TypedMemEvalOutcome.Inconclusive),
            Question("q6", TypedMemEvalOutcome.Unrun, QuestionExecutionStatus.AgentError),   // as the runner marks a skip
            Question("q7", null, QuestionExecutionStatus.AgentError),
        ],
    };

    [Fact]
    public void TypedTree_WithEveryOutcome_WritesScoredLinesWithoutVerdictOrText()
    {
        var (tree, caseExt) = BenchAef.TypedTree(Result(), "episodic", "judge-x");
        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        var (dir, _) = AefCompositeRunWriter.Write(_out, tree, AefCompositeRunWriter.Undescribed, new AefCompositeRunOptions
        {
            SubjectRef = "agent:memory-bot", TargetMode = AefTargetMode.Mocked, Lane = "typedmemeval",
            SuiteRef = "bench:typedmemeval/episodic", CaseExt = caseExt,
        }, start, start.AddMinutes(1));

        Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Intact, AgentEval.Results.Integrity.AefRunVerifier.Verify(dir).Outcome);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject())
            .ToDictionary(l => (string)l["caseId"]!);
        Assert.Equal("scored", (string?)lines["q1"]["state"]);
        Assert.Equal(1.0, (double)lines["q1"]["scores"]![0]!["value"]!);
        Assert.Equal("scored", (string?)lines["q3"]["state"]);   // abstained: measured, not a failure
        Assert.Equal("abstained", (string?)lines["q3"]["ext"]!["agenteval.typedmemeval"]!["outcome"]);
        Assert.Equal("premature", (string?)lines["q4"]["ext"]!["agenteval.typedmemeval"]!["outcome"]);
        Assert.Equal("not_measured", (string?)lines["q5"]["state"]);
        Assert.Equal("skipped", (string?)lines["q6"]["state"]);
        Assert.Equal("error", (string?)lines["q7"]["state"]);
        Assert.DoesNotContain(lines.Values, l => (string?)l["state"] is "passed" or "failed");   // no verdict is claimed

        var score = AefTestRunsSummary(dir, "score");
        Assert.Equal(0.25, (double)score["value"]!);   // q1 of q1-q4: the accuracy over the measured questions
        Assert.Equal(0.25, (double)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "summary.json")))!["ext"]!["agenteval.bench"]!["score"]!);   // 0..1, not 25
        var all = string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("secret", all, StringComparison.Ordinal);
    }

    private static JsonNode AefTestRunsSummary(string dir, string metric) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "summary.json")))!["lanes"]![0]!["metrics"]!.AsArray()
            .Single(m => (string?)m!["metric"] == metric)!;
}
