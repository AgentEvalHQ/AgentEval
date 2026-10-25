// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Evals;
using AgentEval.Results;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;
using AgentEval.Tests.Output;
using static AgentEval.Tests.Cli.CompareCommandTests;

namespace AgentEval.Tests.Cli;

[Collection("ConsoleTests")]
public sealed class CompareAefTests
{
    [Theory]
    [InlineData("scale")]
    [InlineData("direction")]
    [InlineData("kind")]
    [InlineData("unit")]
    public void Compare_DifferentMetricMeaning_RefusesWithoutDeltas(string field)
    {
        using var temp = TempWorkspace.Create("CompareMetric");
        var baseline = Native(temp.Path, .9, false);
        var candidate = Native(temp.Path, .2, false);
        var path = Path.Combine(candidate, "metrics.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        var metric = document["metrics"]!.AsArray().Single(x => (string?)x!["id"] == "score")!;
        metric[field] = field switch
        {
            "scale" => new JsonObject { ["min"] = 0, ["max"] = 100 },
            "direction" => JsonValue.Create("lower_better"),
            "kind" => JsonValue.Create("duration"),
            _ => JsonValue.Create("ms"),
        };
        File.WriteAllText(path, document.ToJsonString());
        File.Delete(Path.Combine(candidate, "seal.json"));
        AefSealer.Seal(candidate);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(candidate).Outcome);

        var (exit, output, _) = Capture(() => CompareCommand.Run(baseline, candidate, asJson: true));
        Assert.Equal(ExitCodes.Incomparable, exit);
        var report = JsonNode.Parse(output)!;
        Assert.Null(report["deltas"]);
        Assert.Contains("metric", report["refusalReasons"]!.ToJsonString());
    }

    [Theory]
    [InlineData("scored")]
    [InlineData("inconclusive")]
    public void Compare_MeasuredWithoutVerdict_PreservesRecordedScore(string state)
    {
        using var temp = TempWorkspace.Create("CompareScored");
        var baseline = Native(temp.Path, .9, false, "scored");
        var candidate = Native(temp.Path, .2, false, "scored");
        foreach (var directory in new[] { baseline, candidate })
        {
            var path = Path.Combine(directory, "results.ndjson");
            var line = JsonNode.Parse(File.ReadAllText(path))!;
            line["state"] = state;
            File.WriteAllText(path, line.ToJsonString() + "\n");
            var run = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!;
            var summary = AefSummaryWriter.Build((string)run["runId"]!, [line.AsObject()],
                new Dictionary<string, AefMetricKind> { ["score"] = AefMetricKind.Score },
                new AefSummary { Lanes = [new("test", [new() { Metric = "score", Path = (string)line["path"]! }])] });
            File.WriteAllText(Path.Combine(directory, "summary.json"), summary.ToJsonString());
            File.Delete(Path.Combine(directory, "seal.json"));
            AefSealer.Seal(directory);
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        }

        var (exit, output, error) = Capture(() => CompareCommand.Run(baseline, candidate, asJson: true));
        Assert.True(exit == ExitCodes.Success, error);
        var report = JsonNode.Parse(output)!;
        Assert.Equal(.9, (double?)report["deltas"]![0]!["baseline"]);
        Assert.Equal(.2, (double?)report["deltas"]![0]!["candidate"]);
        Assert.Equal(-.7, (double)report["deltas"]![0]!["delta"]!, 10);
        Assert.Equal(0, (int)report["regressed"]!);
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("null-root")]
    [InlineData("scalar-root")]
    [InlineData("future-root")]
    [InlineData("missing-selection")]
    [InlineData("wrong-evaluator")]
    [InlineData("wrong-version")]
    [InlineData("wrong-judge")]
    [InlineData("unknown-metric")]
    [InlineData("overlapping-selection")]
    [InlineData("invalid-instrument")]
    [InlineData("conflicting-instrument")]
    [InlineData("conflicting-rubric")]
    public void Compare_SealedMalformedComparisonMetadata_RefusesWithoutDeltas(string defect)
    {
        using var temp = TempWorkspace.Create("CompareMetadata");
        var directory = Native(temp.Path, .9, nested: true);
        var path = Path.Combine(directory, "results.ndjson");
        var lines = File.ReadAllLines(path).Select(x => JsonNode.Parse(x)!.AsObject()).ToList();
        var root = lines.Single(x => x["parentResultId"] is null);
        var leaf = lines.Single(x => x["parentResultId"] is not null);
        const string key = "agenteval.comparison";
        switch (defect)
        {
            case "missing-root": root["ext"]!.AsObject().Remove(key); break;
            case "null-root": root["ext"]![key] = null; break;
            case "scalar-root": root["ext"]![key] = "invalid"; break;
            case "future-root": root["ext"]![key]!["version"] = 999; break;
            case "missing-selection": root["ext"]![key]!.AsObject().Remove("selected"); break;
            case "wrong-evaluator": leaf["ext"]![key]!["facts"]!["evalKey"] = "another.evaluator"; break;
            case "wrong-version": leaf["ext"]![key]!["facts"]!["evalVersion"] = "99"; break;
            case "wrong-judge": leaf["ext"]![key]!["facts"]!["judge"] = new JsonObject { ["modelId"] = "invented-judge", ["subjectRelation"] = "unknown" }; break;
            case "unknown-metric": leaf["ext"]![key]!["metric"] = "absent"; break;
            case "overlapping-selection": root["ext"]![key]!["selected"] = true; break;
            case "invalid-instrument": root["ext"]![key]!["facts"]!["instrumentKind"] = 999; break;
            case "conflicting-instrument": root["ext"]![key]!["facts"]!["instrumentKind"] = "deterministic";
                root["ext"]![key]!["facts"]!["judge"] = new JsonObject { ["modelId"] = "judge" }; break;
            case "conflicting-rubric": root["ext"]![key]!["facts"]!["instrumentKind"] = "model";
                root["ext"]![key]!["facts"]!["judge"] = new JsonObject { ["modelId"] = "judge", ["rubricDigest"] = "rubric-a" };
                root["ext"]![key]!["facts"]!["rubricDigest"] = "rubric-b"; break;
        }
        File.WriteAllText(path, string.Join("\n", lines.Select(x => x.ToJsonString())) + "\n");
        // The protocol permits namespaced extensions; an intact seal does not validate their comparison meaning.
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);

        var (exit, output, error) = Capture(() => CompareCommand.Run(directory, directory, asJson: true));
        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.DoesNotContain("\"deltas\"", output);
        Assert.Contains("comparison", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compare_NativeEvalTrees_CompareObservedLeafUnits(bool nested)
    {
        using var temp = TempWorkspace.Create("CompareNative");
        var baseline = Native(temp.Path, .9, nested);
        var candidate = Native(temp.Path, .2, nested);
        var (exit, output, _) = Capture(() => CompareCommand.Run(baseline, candidate, asJson: true, failOnRegression: true));
        Assert.Equal(ExitCodes.TestFailure, exit);
        var report = JsonNode.Parse(output)!;
        Assert.Equal(1, (int)report["matched"]!);
        Assert.Equal("observed.leaf", (string?)report["deltas"]![0]!["scenario"]);
        Assert.Equal(.9, (double)report["deltas"]![0]!["baseline"]!);
        Assert.Equal(.2, (double)report["deltas"]![0]!["candidate"]!);
    }

    private static string Native(string root, double value, bool nested, string? label = null)
    {
        var leaf = new EvalResult(new EvalMetadata("observed.leaf", "Observed leaf", "test", "1.0"),
            new EvalScore(value, null, label ?? (value >= .7 ? "pass" : "fail"), value >= .7, .7, "none", null),
            new EvalDetails(null, null, null, null, null),
            new EvalProvenance("atomic-code", null, null, null, null, 0, false), DateTimeOffset.UtcNow);
        EvalResult Wrap(EvalResult child, string key) => new(new EvalMetadata(key, key, "test", "1.0"), child.Score,
            new EvalDetails(null, null, null, [child], "AllMustPass"),
            new EvalProvenance("composite", null, null, null, null, 0, false), DateTimeOffset.UtcNow);
        var result = nested ? Wrap(Wrap(leaf, "group"), "preset") : leaf;
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (directory, _) = AefCompositeRunWriter.Write(root, result, AefCompositeRunWriter.Undescribed,
            new AefCompositeRunOptions { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "suite:test" },
            start, start.AddSeconds(1));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        return directory;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Compare_LegacyAndImportedInputs_PreserveScoresAndRegressions(bool importBaseline, bool importCandidate)
    {
        using var temp = TempWorkspace.Create("CompareAef");
        var baseline = await WriteRunAsync(temp, "baseline", [Scenario("one", .9, true, "sha256:aaa", Facts())]);
        var candidate = await WriteRunAsync(temp, "candidate", [Scenario("one", .2, false, "sha256:aaa", Facts())]);
        if (importBaseline) baseline = await Import(baseline, Path.Combine(temp.Path, "baseline-aef"));
        if (importCandidate) candidate = await Import(candidate, Path.Combine(temp.Path, "candidate-aef"));

        var (exit, output, _) = Capture(() => CompareCommand.Run(baseline, candidate, asJson: true, failOnRegression: true));

        Assert.Equal(ExitCodes.TestFailure, exit);
        var report = JsonNode.Parse(output)!;
        Assert.Equal(1, (int)report["regressed"]!);
        Assert.Equal(.9, (double)report["deltas"]![0]!["baseline"]!);
        Assert.Equal(.2, (double)report["deltas"]![0]!["candidate"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compare_TamperedAefWithLegacyScenarios_RefusesBeforeAnyDelta(bool tamperBaseline)
    {
        using var temp = TempWorkspace.Create("CompareAefTamper");
        var legacy = await WriteRunAsync(temp, "source", [Scenario("one", .9, true, "sha256:aaa", Facts())]);
        var valid = await Import(legacy, Path.Combine(temp.Path, "valid"));
        var tampered = await Import(legacy, Path.Combine(temp.Path, "tampered"));
        // Tempt the old reader to bypass the AEF seal through an ordinary legacy scenarios folder.
        Directory.CreateDirectory(Path.Combine(tampered, "scenarios"));
        foreach (var path in Directory.GetFiles(Path.Combine(legacy, "scenarios")))
            File.Copy(path, Path.Combine(tampered, "scenarios", Path.GetFileName(path)));
        Assert.Equal(AefOutcome.Invalid, AefRunVerifier.Verify(tampered).Outcome);

        var (exit, output, error) = Capture(() => CompareCommand.Run(
            tamperBaseline ? tampered : legacy, tamperBaseline ? valid : tampered, asJson: true));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.DoesNotContain("\"deltas\"", output);
        Assert.Contains("invalid", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compare_ImportedEvidence_ReportsLimitedCustody()
    {
        using var temp = TempWorkspace.Create("CompareAefCustody");
        var legacy = await WriteRunAsync(temp, "source", [Scenario("one", .9, true, "sha256:aaa", Facts())]);
        var imported = await Import(legacy, Path.Combine(temp.Path, "imported"));
        var (exit, output, _) = Capture(() => CompareCommand.Run(imported, imported));
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("imported: not sealed when it ran", output, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> Import(string source, string destination)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await AefCommand.RunExportAsync(source, destination, null, "mocked", "on", false, null, false, stdout, stderr);
        Assert.True(exit == 0, stderr.ToString());
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(destination).Outcome);
        return destination;
    }

    private static (int Exit, string Output, string Error) Capture(Func<int> action)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try { return (action(), output.ToString(), error.ToString()); }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
