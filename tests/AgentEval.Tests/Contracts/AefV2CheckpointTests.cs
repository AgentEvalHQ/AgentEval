// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using Xunit;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// AEF v2 checkpoints: the manifest schema and the decision function. The decision vectors' expected outputs are written
/// by hand from the rules in contracts/aef/v2/README.md; the Python reference (tools/aef_decide.py) and the .NET
/// implementation (AgentEval.Results) both have to reproduce them.
/// </summary>
public class AefV2CheckpointTests
{
    private static readonly string Conformance = Path.Combine(AefSchemaSet.V2, "conformance");

    public static TheoryData<string> DecisionVectors() =>
        new(Directory.GetFiles(Path.Combine(Conformance, "decision-vectors"), "*.json").Select(Path.GetFileNameWithoutExtension)!);

    [Theory]
    [MemberData(nameof(DecisionVectors))]
    public void TheDecisionFunction_ReproducesEveryVector(string name)
    {
        var vector = JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance, "decision-vectors", name + ".json")))!;

        var actual = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(vector["input"]!)));

        Assert.True(JsonNode.DeepEquals(vector["expected"], actual), $"{name}: expected {vector["expected"]!.ToJsonString()}, got {actual.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(DecisionVectors))]
    public void EveryDecisionVector_IsValidAgainstTheSchemas(string name)
    {
        var vector = JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance, "decision-vectors", name + ".json")))!;

        foreach (var set in new[] { AefSchemaSet.Writer.Value, AefSchemaSet.Reader.Value })
        {
            Assert.True(set.IsValid("decision#/$defs/input", vector["input"], out var inputErrors), $"{name} input: {inputErrors}");
            Assert.True(set.IsValid("decision", vector["expected"], out var outputErrors), $"{name} expected: {outputErrors}");
        }
    }

    [Fact]
    public void TheVectorsCoverEveryOutcome_AndEveryLaneStatus()
    {
        var expected = Directory.GetFiles(Path.Combine(Conformance, "decision-vectors"), "*.json")
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["expected"]!).ToList();

        Assert.Equal(["approved", "blocked", "expired", "inconclusive"],
            expected.Select(e => (string)e["outcome"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["failed", "incomparable", "missing", "not_measured", "passed", "stale"],
            expected.SelectMany(e => e["lanes"]!.AsArray()).Select(l => (string)l!["status"]!).Distinct().Order(StringComparer.Ordinal));
    }

    public static TheoryData<string> Checkpoints() =>
        new(Directory.GetDirectories(Path.Combine(Conformance, "checkpoints")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Checkpoints))]
    public void ACheckpointManifest_IsAcceptedOrRefused_AsItsExpectationSays(string name)
    {
        var dir = Path.Combine(Conformance, "checkpoints", name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "document.json")));

        Assert.Equal((string)expected["writer"]! == "valid", AefSchemaSet.Writer.Value.IsValid("checkpoint", document, out var w));
        Assert.Equal((string)expected["reader"]! == "valid", AefSchemaSet.Reader.Value.IsValid("checkpoint", document, out var r));
        _ = (w, r);
    }

    [Theory]
    [InlineData("P14D", 14 * 24)]
    [InlineData("PT36H", 36)]
    [InlineData("P1DT12H", 36)]
    public void Freshness_IsADurationInDaysAndHours(string text, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), CheckpointDecision.ParseDuration(text));

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P2W")]
    [InlineData("14D")]
    [InlineData("P1M")]
    public void AnythingElse_IsNotAFreshness(string text) =>
        Assert.Throws<FormatException>(() => CheckpointDecision.ParseDuration(text));

    [Fact]
    public void ATimeWithoutZ_IsRefused_NotReadAsLocal()
    {
        var input = JsonNode.Parse("""{"subjectVersion":"v","evaluatedAt":"2026-10-08T12:00:00+02:00","lanes":[{"lane":"q","blocking":true,"result":null}]}""")!;

        Assert.Throws<FormatException>(() => CheckpointDecisionJson.ReadInput(input));
    }
}
