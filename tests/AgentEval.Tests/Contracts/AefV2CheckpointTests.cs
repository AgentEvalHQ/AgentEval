// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Checkpoints;
using Xunit;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// AEF v2 checkpoints: the manifest schema, the checkpoint verifier and the decision function. The expected outputs and
/// problems are written by hand from contracts/aef/v2/README.md; the Python reference (tools/aef_decide.py) and the .NET
/// implementation (AgentEval.Results) both have to reproduce them.
/// </summary>
public class AefV2CheckpointTests
{
    private static readonly string Conformance = Path.Combine(AefSchemaSet.V2, "conformance");

    public static TheoryData<string> DecisionVectors() =>
        new(Directory.GetFiles(Path.Combine(Conformance, "decision-vectors"), "*.json").Select(Path.GetFileNameWithoutExtension)!);

    private static JsonNode Vector(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance, "decision-vectors", name + ".json")))!;

    [Theory]
    [MemberData(nameof(DecisionVectors))]
    public void TheDecisionFunction_ReproducesEveryVector(string name)
    {
        var vector = Vector(name);

        var actual = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(vector["input"]!)));

        Assert.True(JsonNode.DeepEquals(vector["expected"], actual), $"{name}: expected {vector["expected"]!.ToJsonString()}, got {actual.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(DecisionVectors))]
    public void EveryDecisionVector_IsValidAgainstTheSchemas(string name)
    {
        var vector = Vector(name);
        var readerOnly = (bool?)vector["readerOnly"] == true;

        Assert.Equal(!readerOnly, AefSchemaSet.Writer.Value.IsValid("decision#/$defs/input", vector["input"], out _));
        Assert.True(AefSchemaSet.Reader.Value.IsValid("decision#/$defs/input", vector["input"], out var inputErrors), $"{name} input: {inputErrors}");
        foreach (var set in new[] { AefSchemaSet.Writer.Value, AefSchemaSet.Reader.Value })
            Assert.True(set.IsValid("decision", vector["expected"], out var outputErrors), $"{name} expected: {outputErrors}");
    }

    [Fact]
    public void TheVectorsCoverEveryOutcome_EveryLaneStatus_AndEveryReason()
    {
        var expected = Directory.GetFiles(Path.Combine(Conformance, "decision-vectors"), "*.json")
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["expected"]!).ToList();

        Assert.Equal(["approved", "blocked", "expired", "inconclusive"],
            expected.Select(e => (string)e["outcome"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["failed", "incomparable", "missing", "not_measured", "passed", "stale"],
            expected.SelectMany(e => e["lanes"]!.AsArray()).Select(l => (string)l!["status"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["advisory-failed", "failed", "future-evidence", "incomparable", "missing", "not-measured", "outcome", "stale", "superseded", "wrong-version"],
            expected.SelectMany(e => e["reasons"]!.AsArray()).Select(r => ((string)r!).Split(':')[0]).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NoLane_OrALaneListedTwice_IsRefused_NeverApproved()
    {
        var at = AefTime.Parse("2026-10-08T12:00:00Z");
        var lane = new LaneInput("quality", true, null);

        Assert.Throws<ArgumentException>(() => CheckpointDecision.Decide(new CheckpointDecisionInput("v", at, [])));
        Assert.Throws<ArgumentException>(() => CheckpointDecision.Decide(new CheckpointDecisionInput("v", at, [lane, lane])));
    }

    public static TheoryData<string> Checkpoints() =>
        new(Directory.GetDirectories(Path.Combine(Conformance, "checkpoints")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Checkpoints))]
    public void ACheckpointManifest_IsAcceptedOrRefused_AndVerified_AsItsExpectationSays(string name)
    {
        var dir = Path.Combine(Conformance, "checkpoints", name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "document.json")))!;

        Assert.Equal((string)expected["writer"]! == "valid", AefSchemaSet.Writer.Value.IsValid("checkpoint", document, out _));
        Assert.Equal((string)expected["reader"]! == "valid", AefSchemaSet.Reader.Value.IsValid("checkpoint", document, out _));
        if (expected["problems"] is JsonArray problems)
        {
            Assert.Equal(problems.Select(p => (string)p!), CheckpointManifest.Verify(document));
        }
    }

    [Theory]
    [InlineData("P14D", 14 * 24)]
    [InlineData("PT36H", 36)]
    [InlineData("P1DT12H", 36)]
    [InlineData("P99999D", 99_999 * 24)]
    public void Freshness_IsADurationInDaysAndHours(string text, long hours) =>
        Assert.Equal(hours * 3600, CheckpointDecision.DurationSeconds(text));

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P2W")]
    [InlineData("14D")]
    [InlineData("P1M")]
    [InlineData("P100000D")]
    [InlineData("P14D\n")]
    [InlineData("P١٤D")]   // Arabic-Indic digits: not [0-9]
    public void AnythingElse_IsNotAFreshness(string text) =>
        Assert.Throws<FormatException>(() => CheckpointDecision.DurationSeconds(text));

    [Fact]
    public void Times_CompareAtFullPrecision()
    {
        Assert.True(AefTime.Parse("2026-10-08T12:00:00.000000001Z") > AefTime.Parse("2026-10-08T12:00:00Z"));
        Assert.Equal(AefTime.Parse("2026-10-08T12:00:00.5Z"), AefTime.Parse("2026-10-08T12:00:00.500000000Z"));
    }

    [Theory]
    [InlineData("2026-10-08T12:00:00+02:00")]
    [InlineData("2026-10-08 12:00:00Z")]
    [InlineData("2026-10-08T12:00:00.0000000001Z")]
    [InlineData("2026-10-08T12:00:00Z\n")]
    public void ATimeThatIsNotRfc3339Utc_IsRefused(string text) =>
        Assert.Throws<FormatException>(() => AefTime.Parse(text));
}
