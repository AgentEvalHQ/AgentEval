// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>ADR-030 Slice 1.5 (defect D13) and Slice 1.6 (defect D11).</summary>
public class SkippedReasonAndCaseIdentityTests
{
    private sealed class StubEval : IEval
    {
        public string Key => "tool-input-accuracy";
        public string Name => "Tool Input Accuracy";
        public string Category => "agentic";
        public string Version => "2.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // ── 1.5 ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SkippedResult_ExposesReasonInSummary()
    {
        // The reason went to Recommendations only, so every renderer reading Summary — the field
        // named for exactly this — printed a bare n/a with nothing beside it. A blank cell where the
        // explanation belongs reads as "nothing to say" when the truth is "nobody carried it across".
        const string reason = "No tool definitions were supplied; schema validation cannot run.";

        var result = EvalResult.Skipped(new StubEval(), reason);

        Assert.Equal(reason, result.Details.Summary);
    }

    [Fact]
    public void SkippedResult_KeepsTheReasonInRecommendationsToo()
    {
        // Additive on both sides: no existing reader loses the text it already reads.
        const string reason = "budget filter";

        var result = EvalResult.Skipped(new StubEval(), reason);

        Assert.Equal(new[] { reason }, result.Details.Recommendations);
        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public void SkippedResult_ReasonSurvivesSerialisation()
    {
        // details.summary is already in schema v1 and already nullable, so this costs no schema change.
        var json = JsonSerializer.Serialize(
            EvalResult.Skipped(new StubEval(), "no distractor in this case"),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "no distractor in this case",
            doc.RootElement.GetProperty("details").GetProperty("summary").GetString());
    }

    [Fact]
    public void SkippedResult_RejectsANullEval()
        => Assert.Throws<ArgumentNullException>(() => EvalResult.Skipped(null!, "reason"));

    // ── 1.6 ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvalInput_CarriesACaseId_AndDefaultsToNull()
    {
        // Null means "this producer has not declared case identity". The library must never invent
        // one: an id it generates is an id that changes between runs, and a join key that changes
        // between runs is worse than an absent one because it fails silently.
        Assert.Null(new EvalInput("q").CaseId);

        var input = new EvalInput("q", "r") { CaseId = "galaxus/persona-nadia/case-07" };

        Assert.Equal("galaxus/persona-nadia/case-07", input.CaseId);
        Assert.Equal("q", input.Query);
        Assert.Equal("r", input.Response);
    }

    [Fact]
    public void EvalInput_CaseId_SurvivesAWithCopy_AndKeepsPositionalMembers()
    {
        var original = new EvalInput("q", "r", "ctx") { CaseId = "case-1" };

        var copy = original with { Response = "r2" };

        Assert.Equal("case-1", copy.CaseId);
        Assert.Equal("ctx", copy.Context);
        Assert.Equal("case-2", (original with { CaseId = "case-2" }).CaseId);
    }

    [Fact]
    public void TestCase_CarriesAnId_DistinctFromItsDisplayName()
    {
        // Name is a display string — harnesses format it ($"{id} — {group}" is the recorded example)
        // and reports render it. Joining runs on it re-points the join the moment anyone edits a label.
        var testCase = new TestCase { Id = "cat-integrity-07", Name = "cat-integrity-07 — German personas", Input = "x" };

        Assert.Equal("cat-integrity-07", testCase.Id);
        Assert.NotEqual(testCase.Id, testCase.Name);
        Assert.Null(new TestCase { Name = "n", Input = "x" }.Id);
    }
}
