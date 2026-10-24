// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefBenchmarkComparisonTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("aef-benchmark-comparison");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_out);

    [Theory]
    [InlineData(MeasurementState.Measured, "pass", true, 0.75)]
    [InlineData(MeasurementState.Measured, "fail", false, 0.75)]
    [InlineData(MeasurementState.Measured, "warn", false, 0.75)]
    [InlineData(MeasurementState.Measured, "scored", false, 0.75)]
    [InlineData(MeasurementState.Measured, "inconclusive", false, 0.75)]
    [InlineData(MeasurementState.NotMeasured, "error", false, null)]
    [InlineData(MeasurementState.NotMeasured, "skipped", false, null)]
    [InlineData(MeasurementState.NotMeasured, "warn", false, null)]
    [InlineData(MeasurementState.NotApplicable, "inapplicable", false, null)]
    public void Read_OriginalBenchmark_PreservesMeasurementWithoutChangingProbes(
        MeasurementState measurement, string label, bool passed, double? expectedScore)
    {
        var result = Result(measurement, label, passed);
        var directory = Write(result);
        var scenario = Assert.Single(new AefComparisonReader().Read(directory).Scenarios);
        Assert.Equal("owasp-smoke", scenario.Name);
        var legacy = EvalResultPersistence.ToScenarioResult(result, "owasp-smoke", "benchmark", subjectModel: "subject-x");
        Assert.Equal((legacy.Id, expectedScore, legacy.Passed, legacy.Measurement, legacy.Comparability),
            (scenario.Id, scenario.Score, scenario.Passed, scenario.Measurement, scenario.Comparability));
        var probe = Assert.Single(AefTestRuns.Results(directory));
        Assert.Equal("attack/probe", (string?)probe["caseId"]);
        Assert.Equal("failed", (string?)probe["state"]);
        Assert.Null(probe["ext"]?["agenteval.comparison"]);
        Assert.Equal(expectedScore, (double?)AefTestRuns.Document(directory, "summary.json")["ext"]!["agenteval.bench"]!["score"]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        if (AefTestRuns.ReferenceVerify(directory) is { } reference)
            Assert.Equal("intact", (string?)reference["outcome"]);
    }

    [Theory]
    [InlineData(AefContentCapture.On, false)]
    [InlineData(AefContentCapture.Off, false)]
    [InlineData(AefContentCapture.On, true)]
    public void Read_ObservedInput_UsesOwnSubjectAndHonorsCapture(AefContentCapture capture, bool unknown)
    {
        var result = Result() with { Observation = new(unknown ? null : "private query", unknown ? null : "judge-x") };
        var directory = Write(result, capture);
        var actual = Assert.Single(new AefComparisonReader().Read(directory).Scenarios);
        var expected = EvalResultPersistence.ToScenarioResult(result, "owasp-smoke", "benchmark", subjectModel: "outer-model");
        Assert.Equal(expected.Comparability, actual.Comparability);
        Assert.Equal(capture == AefContentCapture.On ? expected.StimulusHash : null, actual.StimulusHash);
        Assert.DoesNotContain("private query", string.Concat(Directory.GetFiles(directory).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_NoAggregate_DoesNotInventBenchmarkComparison(bool aborted)
    {
        var (directory, _) = AefProbeRunWriter.Write(_out, aborted ? [] : Probes(),
            Options() with { AbortReason = aborted ? "cancelled" : null }, Start, Start.AddSeconds(1));
        Assert.Null(AefTestRuns.Document(directory, "summary.json")["ext"]?["agenteval.bench"]);
        Assert.Throws<InvalidDataException>(() => new AefComparisonReader().Read(directory));
    }

    [Fact]
    public void Write_ConflictingVerdict_RefusesBeforeCreatingRun()
    {
        Assert.Throws<ArgumentException>(() => AefProbeRunWriter.Write(_out, Probes(), Options() with
        {
            BenchmarkComparison = new("owasp-smoke", Result()), BenchVerdict = ("fail", 0),
        }, Start, Start.AddSeconds(1)));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("null-identity")]
    [InlineData("metric")]
    [InlineData("version")]
    [InlineData("label")]
    [InlineData("score")]
    [InlineData("measurement")]
    [InlineData("provenance")]
    public void Write_InvalidOriginalFacts_RefusesBeforeCreatingRun(string fault)
    {
        var result = Result();
        result = fault switch
        {
            "metric" => result with { Metric = result.Metric with { Key = "" } },
            "version" => result with { Metric = result.Metric with { Version = "" } },
            "label" => result with { Score = result.Score with { Label = "" } },
            "score" => result with { Score = result.Score with { Value = 1.5 } },
            "measurement" => result with { Score = result.Score with { Passed = false, Measurement = (MeasurementState)999 } },
            "provenance" => result with { Provenance = null! },
            _ => result,
        };
        Assert.Throws<ArgumentException>(() => AefProbeRunWriter.Write(_out, Probes(), Options() with
        {
            BenchmarkComparison = new(fault == "identity" ? "" : fault == "null-identity" ? null! : "owasp-smoke", result),
        }, Start, Start.AddSeconds(1)));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData(AefContentCapture.On)]
    [InlineData(AefContentCapture.Off)]
    public void Read_ChanceFloor_KeepsNumericFactsAndAppliesContentPolicy(AefContentCapture capture)
    {
        var result = Result() with { Details = new(new Dictionary<string, double> { ["chance_floor"] = 0.25 },
            [new("chance-floor", "uniform", "private derivation")], null, null, null) };
        var directory = Write(result, capture);
        var actual = Assert.Single(new AefComparisonReader().Read(directory).Scenarios).Comparability!.ChanceFloor!;
        var original = EvalResultPersistence.ToScenarioResult(result, "benchmark", "benchmark").Comparability!.ChanceFloor!;
        Assert.Equal((original.Kind, original.State, original.Bar), (actual.Kind, actual.State, actual.Bar));
        Assert.Equal(capture == AefContentCapture.On ? original.Derivation : "Derivation text omitted by content-capture policy.", actual.Derivation);
        if (capture == AefContentCapture.Off)
            Assert.DoesNotContain("private derivation", string.Concat(Directory.GetFiles(directory).Select(File.ReadAllText)));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("null-profile")]
    [InlineData("measurement")]
    [InlineData("passed-type")]
    [InlineData("absent-passed")]
    [InlineData("score-missing")]
    [InlineData("score-range")]
    [InlineData("absent-score")]
    [InlineData("shadow-state")]
    [InlineData("identity")]
    [InlineData("selection")]
    [InlineData("metric")]
    [InlineData("facts")]
    [InlineData("comparison-name")]
    [InlineData("evaluator")]
    [InlineData("instrument")]
    [InlineData("hash")]
    [InlineData("capture")]
    [InlineData("missing-identity")]
    [InlineData("missing-evaluator")]
    [InlineData("missing-evaluator-version")]
    [InlineData("evaluator-type")]
    public void Read_ResealedContradictorySummary_Refuses(string fault)
    {
        var directory = Write(Result());
        var summary = AefTestRuns.Document(directory, "summary.json");
        var bench = summary["ext"]!["agenteval.bench"]!.AsObject();
        var profile = bench["comparison"]!.AsObject();
        var observation = profile["observation"]!.AsObject();
        var metadata = observation["ext"]!["agenteval.comparison"]!.AsObject();
        switch (fault)
        {
            case "version": profile["version"] = 2; break;
            case "null-profile": bench["comparison"] = null; break;
            case "measurement": profile["measurement"] = "0"; break;
            case "passed-type": profile["passed"] = "true"; break;
            case "absent-passed": profile["measurement"] = "NotMeasured"; bench["score"] = null; break;
            case "score-missing": bench["score"] = null; break;
            case "score-range": bench["score"] = 1.5; break;
            case "absent-score": profile["measurement"] = "NotMeasured"; profile["passed"] = false; break;
            case "shadow-state": observation["state"] = "passed"; break;
            case "identity": metadata["id"] = "another-case"; break;
            case "selection": metadata["selected"] = false; break;
            case "metric": metadata["metric"] = "resisted"; break;
            case "facts": metadata["facts"] = null; break;
            case "comparison-name": metadata["name"] = false; break;
            case "evaluator": observation["evaluator"]!["id"] = "another-evaluator"; break;
            case "instrument": metadata["facts"]!["instrumentKind"] = "deterministic"; break;
            case "hash": metadata["stimulusHash"] = "not-a-digest"; break;
            case "capture": metadata["stimulusHash"] = "sha256:" + new string('a', 64); break;
            case "missing-identity": metadata["id"] = null; observation["caseId"] = null; break;
            case "missing-evaluator": observation["evaluator"] = null; break;
            case "missing-evaluator-version": observation["evaluator"]!["version"] = null; break;
            case "evaluator-type": observation["evaluator"] = "not-an-object"; break;
        }
        File.WriteAllText(Path.Combine(directory, "summary.json"), summary.ToJsonString());
        Reseal(directory);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        Assert.Throws<InvalidDataException>(() => new AefComparisonReader().Read(directory));
    }

    [Theory]
    [InlineData("non-probe-evaluator")]
    [InlineData("probe-comparison")]
    [InlineData("direction")]
    [InlineData("scale")]
    public void Read_ResealedAlternateEvidenceMeaning_Refuses(string fault)
    {
        var directory = Write(Result());
        if (fault is "non-probe-evaluator" or "probe-comparison")
        {
            var probe = Assert.Single(AefTestRuns.Results(directory));
            if (fault == "non-probe-evaluator") probe["evaluator"]!["id"] = "ordinary-evaluation";
            else probe["ext"] = new JsonObject { ["agenteval.comparison"] = "malformed selection" };
            File.WriteAllText(Path.Combine(directory, "results.ndjson"), probe.ToJsonString(new() { WriteIndented = false }) + "\n");
        }
        else
        {
            var metrics = AefTestRuns.Document(directory, "metrics.json");
            var score = metrics["metrics"]!.AsArray().Single(m => (string?)m!["id"] == "score")!;
            if (fault == "direction") score["direction"] = "lower_better";
            else score["scale"]!["max"] = 100;
            File.WriteAllText(Path.Combine(directory, "metrics.json"), metrics.ToJsonString());
        }
        Reseal(directory);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        Assert.Throws<InvalidDataException>(() => new AefComparisonReader().Read(directory));
    }

    [Theory]
    [InlineData(AefContentCapture.On)]
    [InlineData(AefContentCapture.Off)]
    public void Read_OriginalScenarioName_PreservesCallerRecordedLabel(AefContentCapture capture)
    {
        var directory = Write(Result(), capture, "Original benchmark — title");
        var scenario = Assert.Single(new AefComparisonReader().Read(directory).Scenarios);
        Assert.Equal("owasp-smoke", scenario.Id);
        Assert.Equal("Original benchmark — title", scenario.Name);
    }

    private string Write(EvalResult result, AefContentCapture capture = AefContentCapture.Off, string? name = null) =>
        AefProbeRunWriter.Write(_out, Probes(), Options() with
        {
            ContentCapture = capture, BenchmarkComparison = new("owasp-smoke", result, "subject-x") { Name = name },
        }, Start, Start.AddSeconds(1)).Directory;

    private static AefProbeRunOptions Options() => new()
    {
        SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, SuiteRef = "bench:owasp/smoke", Lane = "owasp",
    };
    private static AefProbeLine[] Probes() => [new() { CaseId = "attack/probe", State = AefState.Failed, Annotator = AefAnnotatorKind.Code }];
    private static EvalResult Result(MeasurementState measurement = MeasurementState.Measured, string label = "pass", bool passed = true) => new(
        new("owasp", "OWASP", "security", "1.0.0"),
        new EvalScore(0.75, null, label, passed, 0.7, "none", null) { Measurement = measurement },
        new(null, null, null, null, null), new("atomic-llm", "judge-x", null, null, null, 0, false), Start);

    private static void Reseal(string directory)
    {
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
    }
}
