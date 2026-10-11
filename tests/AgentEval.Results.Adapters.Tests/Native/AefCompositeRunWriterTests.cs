// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefCompositeRunWriterTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("aef-composite");

    public void Dispose() => AefTestRuns.Delete(_out);

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static EvalResult Leaf(string key, string label, double value, string type = "atomic-code", string? judge = null, string severity = "none") => new(
        new(key, key, "test", "1.0.0"),
        new EvalScore(value, null, label, label == "pass", null, label == "pass" ? "none" : severity, null),
        new(null, null, ["the judge said: secret reasoning"], null, null) { Summary = "secret summary" },
        new(type, judge, null, null, null, 0, false),
        Start);

    private static EvalResult Composite(string key, string label, double value, double? threshold, EvalLineage lineage, params EvalResult[] subs) => new(
        new(key, key, "test", "1.0.0"),
        new EvalScore(value, null, label, label == "pass", threshold, label == "pass" ? "none" : "high", null),
        new(null, null, null, subs, "WeightedSum") { Lineage = lineage },
        new("composite", null, null, null, null, 0, false),
        Start);

    private static AefComponentShape Leaf(double weight = 1, bool required = true, string onFailure = "Averaged") => new(weight, required, onFailure, []);

    [Fact]
    public void Write_RecordsTheTree_ItsRulesAndComponents_IntactUnderBothVerifiers()
    {
        // A preset of two articles: art5 failed on its required judge part (failure-effect), art6 errored (required-error).
        var art5 = Composite("art5", "fail", 0.55, 0.7, new EvalLineage("failure-effect", ["art5.judge"]),
            Leaf("art5.judge", "fail", 0.2, "atomic-llm", "judge-x", "high"),
            Leaf("art5.check", "pass", 0.9));
        var art6 = Composite("art6", "error", 0, 0.7, new EvalLineage("required-error", ["art6.trace"]),
            Leaf("art6.trace", "error", 0),
            Leaf("art6.check", "skipped", 0));
        var preset = Composite("gdpr.standard", "fail", 0.4, null, new EvalLineage("severity", []), art5, art6);
        var shape = new AefComponentShape(1, true, "Averaged",
        [
            new(0.5, true, "Averaged", [Leaf(0.3, true, "Fail"), Leaf(0.7, false)]),
            new(0.5, true, "Averaged", [Leaf(0.5), Leaf(0.5, false)]),
        ]);

        var (dir, _) = AefCompositeRunWriter.Write(_out, preset, shape, new AefCompositeRunOptions
        {
            SubjectRef = "agent:support", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr/standard",
        }, Start, Start.AddMinutes(2));

        var verification = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
        }

        Assert.Equal("judge-x", (string?)AefTestRuns.Document(dir, "run.json")["judges"]![0]!["model"]);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject())
            .ToDictionary(l => $"{l["caseId"]}@{l["path"]}");

        var art5Root = lines["art5@case"];
        Assert.Equal("failed", (string?)art5Root["state"]);
        Assert.Equal("failure-effect", (string?)art5Root["aggregation"]!["rulePath"]);
        Assert.Equal("WeightedSum", (string?)art5Root["aggregation"]!["strategy"]);
        Assert.Equal((string?)lines["art5@case/art5.judge"]["resultId"], (string?)art5Root["aggregation"]!["decisive"]![0]);
        Assert.Equal(0.3, (double)lines["art5@case/art5.judge"]["component"]!["weight"]!);
        Assert.Equal("Fail", (string?)lines["art5@case/art5.judge"]["ext"]!["agenteval.component"]!["onFailure"]);
        Assert.False((bool)lines["art5@case/art5.check"]["component"]!["required"]!);
        Assert.Equal("LLM", (string?)lines["art5@case/art5.judge"]["annotator"]!["kind"]);

        var art6Root = lines["art6@case"];
        Assert.Equal("error", (string?)art6Root["state"]);
        Assert.Equal("required-error", (string?)art6Root["aggregation"]!["rulePath"]);
        Assert.Equal(0, (int)art6Root["aggregation"]!["measured"]!);
        Assert.Equal(1, (int)art6Root["aggregation"]!["unmeasured"]!["error"]!);
        Assert.Equal(1, (int)art6Root["aggregation"]!["unmeasured"]!["skipped"]!);

        var summary = AefTestRuns.Document(dir, "summary.json");
        Assert.Equal("fail", (string?)summary["ext"]!["agenteval.bench"]!["label"]);
        var all = string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("secret", all, StringComparison.Ordinal);   // summaries and recommendations are content
    }

    [Fact]
    public void Write_RefusesAShapeThatDoesNotMatchTheResult()
    {
        var preset = Composite("p", "pass", 1, 0.5, new EvalLineage("threshold", []), Leaf("a", "pass", 1));

        Assert.Throws<ArgumentException>(() => AefCompositeRunWriter.Write(_out, preset, new AefComponentShape(1, true, "Averaged", []),
            new AefCompositeRunOptions { SubjectRef = "agent:x", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr/x" },
            Start, Start.AddMinutes(1)));
    }

    [Theory]
    [InlineData("pass", AefState.Passed)]
    [InlineData("warn", AefState.Warn)]
    [InlineData("inapplicable", AefState.NotApplicable)]
    [InlineData("needs-review", AefState.Failed)]
    public void StateOf_ReadsTheLabel_AnUnknownOneByThePassFlag(string label, AefState state) =>
        Assert.Equal(state, AefCompositeRunWriter.StateOf(new EvalScore(0.5, null, label, label == "pass", null, "none", null)));
}
