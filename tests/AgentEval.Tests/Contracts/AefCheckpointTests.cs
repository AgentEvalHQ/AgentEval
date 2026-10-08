// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Checkpoints;
using Xunit;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// AEF 1.0 checkpoints: the manifest schema, the checkpoint verifier and the decision function. The expected outputs and
/// problems are written by hand from contracts/aef/1/spec/05-checkpoints.md; the Python reference (tools/aef_decide.py)
/// and the .NET implementation (AgentEval.Results) both have to reproduce them.
/// </summary>
public class AefCheckpointTests
{
    private static readonly string Conformance = Path.Combine(AefSchemaSet.Root, "conformance");

    public static TheoryData<string> DecisionVectors() =>
        new(Directory.GetFiles(Path.Combine(Conformance, "decision-vectors"), "*.json").Select(Path.GetFileNameWithoutExtension)!);

    private static JsonNode Vector(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Conformance, "decision-vectors", name + ".json")))!;

    [Theory]
    [MemberData(nameof(DecisionVectors))]
    public void TheDecisionFunction_ReproducesEveryVector(string name)
    {
        var vector = Vector(name);
        if (vector["expectedError"] is not null)
        {
            // Refused rather than decided: no lane, a lane twice, an exception for no lane, or one never in force.
            Assert.Throws<ArgumentException>(() => CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(vector["input"]!)));
            return;
        }

        var actual = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(vector["input"]!)));

        Assert.True(JsonNode.DeepEquals(vector["expected"], actual), $"{name}: expected {vector["expected"]!.ToJsonString()}, got {actual.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(DecisionVectors))]
    public void EveryDecisionVector_IsValidAgainstTheSchemas(string name)
    {
        var vector = Vector(name);
        var readerOnly = (bool?)vector["readerOnly"] == true;
        var schemaInvalid = (bool?)vector["schemaInvalid"] == true;

        Assert.Equal(!readerOnly && !schemaInvalid, AefSchemaSet.Writer.Value.IsValid("decision#/$defs/input", vector["input"], out _));
        Assert.Equal(!schemaInvalid, AefSchemaSet.Reader.Value.IsValid("decision#/$defs/input", vector["input"], out _));
        if (vector["expected"] is { } expected)
        {
            foreach (var set in new[] { AefSchemaSet.Writer.Value, AefSchemaSet.Reader.Value })
                Assert.True(set.IsValid("decision", expected, out var outputErrors), $"{name} expected: {outputErrors}");
        }
    }

    [Fact]
    public void TheVectorsCoverEveryOutcome_EveryLaneStatus_AndEveryReason()
    {
        var expected = Directory.GetFiles(Path.Combine(Conformance, "decision-vectors"), "*.json")
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["expected"]).OfType<JsonNode>().ToList();

        Assert.Equal(["approved", "approved_with_exceptions", "blocked", "expired", "inconclusive"],
            expected.Select(e => (string)e["outcome"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["failed", "incomparable", "missing", "not_measured", "passed", "stale", "waived"],
            expected.SelectMany(e => e["lanes"]!.AsArray()).Select(l => (string)l!["status"]!).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["advisory-failed", "exception-expired", "exception-other-evidence", "failed", "future-evidence", "incomparable", "missing",
                      "not-measured", "outcome", "stale", "superseded", "waived", "wrong-version"],
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

    [Fact]
    public void AnExceptionForNoLane_NoEvidence_OrNeverInForce_IsRefused_NeverIgnored()
    {
        var at = AefTime.Parse("2026-10-08T12:00:00Z");
        string run = new('b', 64), rerun = new('f', 64);
        var failed = new LaneInput("security", true, new LaneEvidence(LaneEvidenceStatus.Failed, "v", AefTime.Parse("2026-10-07T10:00:00Z")),
            Evidence: [run]);
        var by = new TrustedIdentity("oidc:https://login.example.com/u-7f3a", "authenticated");
        ExceptionGrant Grant(string lane, string from, string until, params string[] evidence) =>
            new(lane, evidence, "Accepted.", by, AefTime.Parse(from), AefTime.Parse(until));
        CheckpointDecisionResult Decide(ExceptionGrant grant) => CheckpointDecision.Decide(new CheckpointDecisionInput("v", at, [failed], Exceptions: [grant]));

        Assert.Throws<ArgumentException>(() => Decide(Grant("memory", "2026-10-07T15:00:00Z", "2026-10-21T00:00:00Z", run)));
        Assert.Throws<ArgumentException>(() => Decide(Grant("security", "2026-10-07T15:00:00Z", "2026-10-21T00:00:00Z")));
        Assert.Throws<ArgumentException>(() => Decide(Grant("security", "2026-10-07T15:00:00Z", "2026-10-07T15:00:00Z", run)));
        Assert.Equal(CheckpointOutcome.ApprovedWithExceptions, Decide(Grant("security", "2026-10-07T15:00:00Z", "2026-10-21T00:00:00Z", run)).Outcome);

        // A re-run has another run hash: the exception for the first run does not accept it.
        var other = Decide(Grant("security", "2026-10-07T15:00:00Z", "2026-10-21T00:00:00Z", rerun));
        Assert.Equal(CheckpointOutcome.Blocked, other.Outcome);
        Assert.Equal(["failed:security", "exception-other-evidence:security", "outcome:blocked"], other.Reasons);
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
        var schema = (string)expected["schema"]!;
        var readable = (string)expected["reader"]! == "valid";

        Assert.Equal((string)expected["writer"]! == "valid", AefSchemaSet.Writer.Value.IsValid(schema, document, out _));
        Assert.Equal(readable, AefSchemaSet.Reader.Value.IsValid(schema, document, out _));
        // [CKP-7] codes, in code order, for a manifest the reader accepts; none for one it refuses (spec 09 §9.2.1).
        Assert.Equal(readable, expected["problems"] is JsonArray);
        if (expected["problems"] is JsonArray problems)
        {
            Assert.Equal(problems.Select(p => (string)p!), CheckpointManifest.Verify(document));
        }
    }

    [Fact]
    public void TheCheckpointVectors_CoverEveryManifestProblem()
    {
        var problems = Directory.GetFiles(Path.Combine(Conformance, "checkpoints"), "expected.json", SearchOption.AllDirectories)
            .SelectMany(f => JsonNode.Parse(File.ReadAllText(f))!["problems"]?.AsArray() ?? [])
            .Select(p => (string)p!).Distinct().Order(StringComparer.Ordinal);

        // Every code of [CKP-7].
        Assert.Equal(["decision", "evidence", "exception-evidence", "lane-evidence", "lanes", "outcome", "unverifiable", "version"], problems);
    }

    [Theory]
    [InlineData("P14D", 14 * 24 * 60)]
    [InlineData("PT36H", 36 * 60)]
    [InlineData("P1DT12H", 36 * 60)]
    [InlineData("P99999D", 99_999 * 24 * 60)]
    [InlineData("PT90M", 90)]
    [InlineData("PT45M", 45)]
    [InlineData("P1DT2H30M", (26 * 60) + 30)]
    [InlineData("PT99999H99999M", (99_999 * 60) + 99_999)]
    public void Freshness_IsADurationInDaysHoursAndMinutes(string text, long minutes)
    {
        // [ENC-9]: one grammar for a lane's freshness and a plan's timeout.
        Assert.Equal(minutes * 60, CheckpointDecision.DurationSeconds(text));
        Assert.Equal(minutes * 60, AgentEval.Results.Runner.RunnerEventStream.TimeoutSeconds(text));
    }

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P1DT")]       // a T with no part after it
    [InlineData("PT30S")]      // no seconds
    [InlineData("P1W")]
    [InlineData("P2W")]
    [InlineData("PT1M1H")]     // hours before minutes
    [InlineData("P1M")]        // months: M is minutes, and only after T
    [InlineData("14D")]
    [InlineData("P100000D")]
    [InlineData("PT123456M")]
    [InlineData("P14D\n")]
    [InlineData("P١٤D")]   // Arabic-Indic digits: not [0-9]
    public void AnythingElse_IsNotAFreshness(string text)
    {
        Assert.Throws<FormatException>(() => CheckpointDecision.DurationSeconds(text));
        Assert.Throws<FormatException>(() => AgentEval.Results.Runner.RunnerEventStream.TimeoutSeconds(text));
    }

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
    [InlineData("2026-02-31T00:00:00Z")]   // the pattern allows day 31 in any month: an impossible date is refused, never rolled over
    public void ATimeThatIsNotRfc3339Utc_IsRefused(string text) =>
        Assert.Throws<FormatException>(() => AefTime.Parse(text));
}
