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
    public void WriteAbortedEmpty_NoCompletedEvaluation_WritesNoPlannedResultsOrJudges()
    {
        var preset = Composite("preset", "error", 0, null, new EvalLineage("required-error", []),
            Leaf("planned", "pass", 1, "atomic-llm", "not-called"));
        var (dir, _) = AefCompositeRunWriter.WriteAbortedEmpty(_out, preset, new AefCompositeRunOptions
        {
            SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr/test",
            AbortReason = "cancelled",
            ExecutionPolicy = new AefExecutionPolicy { TrialsPerCase = 3, Aggregation = AefTrialAggregation.MajorityVote },
        }, Start, Start.AddSeconds(1));

        Assert.Empty(File.ReadAllBytes(Path.Combine(dir, "results.ndjson")));
        var header = AefTestRuns.Document(dir, "run.json");
        Assert.Equal("aborted", (string?)header["status"]);
        Assert.Null(header["judges"]);
        Assert.Equal(3, (int)header["suite"]!["executionPolicy"]!["trialsPerCase"]!);
        var verified = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verified.Outcome);
        Assert.Empty(verified.Problems);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteAbortedTrials_DifferentLeafBreakdowns_PreservesOnlyObservedPathsAndExactRollups(bool captureContent)
    {
        var first = Composite("preset", "pass", 1, null, new EvalLineage("threshold", []),
            Composite("leaf", "pass", 1, null, new EvalLineage("threshold", []), Leaf("claim", "pass", 1)));
        var second = Composite("preset", "error", 0, null, new EvalLineage("required-error", []), Leaf("leaf", "error", 0));
        var (dir, _) = AefCompositeRunWriter.WriteAbortedTrials(_out, [first, second],
            new AefComponentShape(1, true, "Averaged", [Leaf()]), new AefCompositeRunOptions
            {
                SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr/test",
                AbortReason = "cancelled",
                ContentCapture = captureContent ? AefContentCapture.On : AefContentCapture.Off,
                ExecutionPolicy = new AefExecutionPolicy { TrialsPerCase = 3, Aggregation = AefTrialAggregation.MajorityVote },
            }, Start, Start.AddSeconds(1));

        var verified = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verified.Outcome);
        Assert.Empty(verified.Problems);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(5, lines.Count);
        Assert.Equal(captureContent, lines.Any(l => l["evidence"] is not null));
        Assert.All(lines.Where(l => l["trial"] is null), l => Assert.Null(l["evidence"]));
        var roots = lines.Where(l => (string?)l["path"] == "case").ToList();
        Assert.Equal(2, (int)Assert.Single(roots, l => l["trials"] is not null)["trials"]!["n"]!);
        var claim = Assert.Single(lines, l => (string?)l["path"] == "case/claim" && l["trials"] is not null);
        Assert.Equal(1, (int)claim["trials"]!["n"]!);
        Assert.Equal("error", (string?)claim["state"]);
        Assert.DoesNotContain(lines, l => (string?)l["path"] == "case/claim" && (int?)l["trial"] == 1);
        Assert.Equal(3, (int)AefTestRuns.Document(dir, "run.json")["suite"]!["executionPolicy"]!["trialsPerCase"]!);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
    }

    [Fact]
    public void Write_AbortedTree_RetainsMeasuredChildrenWithoutGateOrInventedParentScore()
    {
        var interrupted = Leaf("interrupted", "error", 0);
        interrupted = interrupted with { Score = interrupted.Score with { Measurement = AgentEval.Evals.Meta.MeasurementState.NotMeasured } };
        var article = Composite("article", "error", 0, null, new EvalLineage("required-error", []),
            Leaf("finished", "pass", 1), interrupted);
        var preset = Composite("preset", "error", 0, null, new EvalLineage("required-error", []), article);
        var shape = new AefComponentShape(1, true, "Averaged", [new(1, true, "Averaged", [Leaf(), Leaf()])]);
        var (dir, _) = AefCompositeRunWriter.Write(_out, preset, shape, new AefCompositeRunOptions
        {
            SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr/test",
            AbortReason = "cancelled",
            Gate = new AefCommandGate { GateId = "gate:bench", Rule = "label:pass", ExitCode = 0, Outcome = AefGateOutcome.Ship },
        }, Start, Start.AddSeconds(1));

        var verified = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verified.Outcome);
        Assert.Empty(verified.Problems);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference) Assert.Equal("intact", (string?)reference["outcome"]);
        Assert.Equal("aborted", (string?)AefTestRuns.Document(dir, "run.json")["status"]);
        Assert.Null(AefTestRuns.Document(dir, "summary.json")["ext"]!["agenteval.bench"]!["score"]);
        Assert.False(File.Exists(Path.Combine(dir, "gates.ndjson")));
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("passed", (string?)Assert.Single(lines, l => (string?)l["path"] == "case/finished")["state"]);
        foreach (var line in lines.Where(l => (string?)l["state"] == "error")) Assert.Null(line["scores"]);
    }

    [Fact]
    public void Write_RecordsTheTree_ItsRulesAndComponents_IntactUnderBothVerifiers()
    {
        // A preset of two articles: art5 failed on its required judge part (failure-effect), art6 errored (required-error).
        var art5 = Composite("art5", "fail", 0.55, 0.7, new EvalLineage("failure-effect", ["art5.judge"]),
            Leaf("art5.judge", "fail", 0.2, "atomic-llm", "judge-x", "high"),
            Leaf("art5.check", "pass", 0.9));
        var art6 = Composite("art6", "error", 0, 0.7, new EvalLineage("required-error", ["art6.trace"]),
            Leaf("art6.trace", "error", 0, "atomic-llm", "judge-y"),
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
            Gate = new AefCommandGate { GateId = "gate:bench", Rule = "label:fail", ExitCode = 9, Outcome = AefGateOutcome.NoShip },
        }, Start, Start.AddMinutes(2));

        var verification = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        var gate = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "gates.ndjson")).Single())!;
        Assert.Equal(9, (int)gate["exitCode"]!);
        Assert.Equal("no_ship", (string?)gate["outcome"]);
        Assert.Equal(2, gate["inputs"]!["results"]!.AsArray().Count);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
        }

        Assert.Equal("judge-x", (string?)AefTestRuns.Document(dir, "run.json")["judges"]![0]!["model"]);
        Assert.Single(AefTestRuns.Document(dir, "run.json")["judges"]!.AsArray());   // judge-y's leaf errored: it graded nothing
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
        Assert.Null(lines["art6@case/art6.trace"]["annotator"]);   // an errored judge leaf names no grader
        Assert.Contains("measurement state NotMeasured", (string?)lines["art6@case/art6.check"]["reason"]);   // skipped: not "Measured"

        var summary = AefTestRuns.Document(dir, "summary.json");
        Assert.Equal("fail", (string?)summary["ext"]!["agenteval.bench"]!["label"]);
        var entries = summary["lanes"]![0]!["metrics"]!.AsArray();
        var mean = Assert.Single(entries, e => (string?)e!["metric"] == "score")!;
        var aggregate = Assert.Single(entries, e => (string?)e!["metric"] == "preset-score")!;
        Assert.Equal(0.55, (double)mean["value"]!);
        Assert.Equal(0.4, (double)aggregate["value"]!);
        Assert.Equal("failed", (string?)aggregate["verdict"]);
        Assert.Equal("agenteval.weightedsum", (string?)aggregate["aggregate"]!["method"]);
        var all = string.Concat(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("secret", all, StringComparison.Ordinal);   // summaries and recommendations are content
    }

    [Fact]
    public void Write_RecordsALeafsOwnBreakdown_BesideItsVerdict_AsOwn()
    {
        // A leaf eval (no components) that keeps sub-results, two of them with the same key: Own, weight 0, index paths.
        var leaf = new EvalResult(new("groundedness", "g", "test", "1.0.0"),
            new EvalScore(0.5, null, "warn", false, 0.7, "medium", null),
            new(null, null, null, [Leaf("claim", "pass", 1), Leaf("claim", "fail", 0)], null),
            new("atomic-code", null, null, null, null, 0, false), Start);
        var preset = Composite("p", "warn", 0.5, 0.7, new EvalLineage("threshold", []), leaf);

        var (dir, _) = AefCompositeRunWriter.Write(_out, preset, new AefComponentShape(1, true, "Averaged", [Leaf()]),
            new AefCompositeRunOptions { SubjectRef = "agent:x", TargetMode = AefTargetMode.Replayed, Lane = "agentic", SuiteRef = "bench:agentic/x" },
            Start, Start.AddMinutes(1));

        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject()).ToDictionary(l => (string)l["path"]!);
        Assert.Equal("Own", (string?)lines["case"]["aggregation"]!["strategy"]);
        Assert.Equal(0.0, (double)lines["case/2"]["component"]!["weight"]!);
        Assert.Equal("failed", (string?)lines["case/2"]["state"]);
    }

    [Fact]
    public void WriteTrials_PreservesTreesDisagreementAndAbsences_IntactUnderBothVerifiers()
    {
        EvalResult Preset(string label, string childLabel, double value) =>
            Composite("p", label, value, 0.7, new EvalLineage("threshold", []),
                Composite("article", label, value, 0.7, new EvalLineage("threshold", []), Leaf("judge", childLabel, value, "atomic-llm", "judge-x")));
        var trials = new[] { Preset("pass", "pass", 1), Preset("fail", "fail", 0), Preset("error", "error", 0) };
        var rollup = Preset("fail", "fail", 0.5);
        var shape = new AefComponentShape(1, true, "Averaged", [new(1, true, "Averaged", [Leaf()])]);
        var (dir, _) = AefCompositeRunWriter.WriteTrials(_out, rollup, trials, shape,
            new AefCompositeRunOptions { SubjectRef = "agent:x", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr/x" },
            Start, Start.AddMinutes(1));

        var verification = AefRunVerifier.Verify(dir);
        Assert.Equal(AefOutcome.Intact, verification.Outcome);
        Assert.Empty(verification.Problems);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
            Assert.Equal("intact", (string?)reference["outcome"]);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(8, lines.Count);
        foreach (var root in lines.Where(l => (string?)l["path"] == "case"))
        {
            var child = Assert.Single(lines, l => (string?)l["parentResultId"] == (string?)root["resultId"]);
            Assert.Equal((int?)root["trial"], (int?)child["trial"]);
        }
        foreach (var aggregate in lines.Where(l => l["trial"] is null))
        {
            Assert.Equal(3, (int)aggregate["trials"]!["n"]!);
            Assert.Equal(1, (int)aggregate["trials"]!["passed"]!);
            Assert.False((bool)aggregate["trials"]!["agree"]!);
            Assert.Null(aggregate["annotator"]);
        }
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_WithheldPresetScore_DoesNotPublishItsPlaceholderAsAnAggregate()
    {
        var preset = Composite("p", "warn", 0, 0.7, new EvalLineage("required-error", []), Leaf("a", "pass", 0.8));
        preset = preset with { Score = preset.Score with { Measurement = AgentEval.Evals.Meta.MeasurementState.NotMeasured } };
        var (dir, _) = AefCompositeRunWriter.Write(_out, preset, AefCompositeRunWriter.Undescribed,
            new AefCompositeRunOptions { SubjectRef = "agent:x", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:gdpr/x" },
            Start, Start.AddMinutes(1));

        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        var metrics = AefTestRuns.Document(dir, "summary.json")["lanes"]![0]!["metrics"]!.AsArray();
        Assert.Equal(0.8, (double)Assert.Single(metrics, m => (string?)m!["metric"] == "score")!["value"]!);
        var aggregate = Assert.Single(metrics, m => (string?)m!["metric"] == "preset-score")!;
        Assert.Equal("not_measured", (string?)aggregate["verdict"]);
        Assert.Null(aggregate["value"]);
        if (AefTestRuns.ReferenceVerify(dir) is { } reference)
            Assert.Equal("intact", (string?)reference["outcome"]);
    }

    [Fact]
    public void Write_RefusesAShapeThatDoesNotMatchTheResult()
    {
        var preset = Composite("p", "pass", 1, 0.5, new EvalLineage("threshold", []), Leaf("a", "pass", 1));

        Assert.Throws<ArgumentException>(() => AefCompositeRunWriter.Write(_out, preset, new AefComponentShape(1, true, "Averaged", [Leaf(), Leaf()]),
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
