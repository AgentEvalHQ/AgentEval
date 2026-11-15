// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Tests.Evals;
using Xunit;

namespace AgentEval.Tests.Agentic.Quality;

public sealed class GroundednessPolicyTests
{
    private static readonly EvalInput ShortAnswer = new("When does Aurora launch?", "15 May.",
        Context: "Aurora launches on 15 May. Its office has five rooms. The building was painted blue in 1998.");

    [Fact]
    public async Task Evaluate_ShortSupportedAnswer_RecommendedPolicyAvoidsContextCoveragePenalty()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(messages =>
            messages.Any(m => m.Text?.Contains("leverages a substantial portion", StringComparison.Ordinal) == true)
                ? """{"score":0,"reasoning":"Only one of the context facts appears in the answer."}"""
                : """{"score":1,"reasoning":"The answer's claim is fully supported."}""");
        var judge = new ChatClientEvaluator(client);
        var legacy = await new GroundednessEval(judge).EvaluateAsync(ShortAnswer);
        var current = await Create(judge).EvaluateAsync(ShortAnswer);
        Assert.Equal(0.75, legacy.Score.Value, 8);
        Assert.False(legacy.Score.Passed);
        Assert.Equal(1, current.Score.Value);
        Assert.True(current.Score.Passed);
        Assert.Equal("2.0.0", current.Metric.Version);
        Assert.DoesNotContain(current.Details.SubResults!, r => r.Metric.Key == "evidence_coverage");
        Assert.All(current.Details.SubResults!, r => Assert.Equal("agenteval.groundedness.v2", r.Provenance.PromptId));
        Assert.All(client.Calls.Skip(4), call => Assert.Contains("Do not lower its score", call[0].Text!));
        var stored = EvalResultPersistence.ToScenarioResult(current, "short", "short");
        var restored = EvalResultPersistence.FromScenarioResult(stored)!;
        Assert.Equal(current.Score.Value, restored.Score.Value);
        Assert.Equal(current.Details.SubResults![0].Provenance.RubricDigest, restored.Details.SubResults![0].Provenance.RubricDigest);
    }

    [Fact]
    public async Task Evaluate_MissingContext_IsUnmeasuredWithoutJudgeCalls()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => """{"score":1}""");
        var result = await Create(new ChatClientEvaluator(client)).EvaluateAsync(new("q", "a"));
        Assert.Empty(client.Calls);
        Assert.False(result.Score.Passed);
        Assert.False(result.Score.CountsTowardAggregate());
        Assert.Equal("skipped", result.Score.Label);
        Assert.All(result.Details.SubResults!, r => Assert.False(r.Score.CountsTowardAggregate()));
    }

    [Fact]
    public async Task Evaluate_UnsupportedClaimOrContradiction_CannotBeAveragedIntoPass()
    {
        foreach (var failedDimension in new[] { "claim_support", "claim_contradicted" })
        {
            using var client = new RubricJudgeTests.RecordingChatClient(messages =>
                messages[0].Text!.Contains($"**{failedDimension}**", StringComparison.Ordinal)
                    ? """{"score":0,"reasoning":"A material claim is unsupported or contradicted."}"""
                    : """{"score":1}""");
            var result = await Create(new ChatClientEvaluator(client)).EvaluateAsync(ShortAnswer);
            Assert.False(result.Score.Passed);
            Assert.Equal("fail", result.Score.Label);
        }
    }

    [Fact]
    public async Task Evaluate_DisabledDimension_IsNeverCalledOrAllowedToVeto()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => """{"score":1}""");
        var options = GroundednessOptions.SupportFocused with
        {
            ClaimSupport = new(1), ClaimContradicted = new(100, Enabled: false), CitationAccuracy = new(100, Enabled: false)
        };
        var result = await Create(new ChatClientEvaluator(client), options).EvaluateAsync(ShortAnswer);
        Assert.Single(client.Calls);
        Assert.Single(result.Details.SubResults!);
        Assert.True(result.Score.Passed);
    }

    [Fact]
    public async Task Evaluate_RequiredCitations_ChangesActualCriteriaWithoutMutatingOtherInstances()
    {
        using var first = new RubricJudgeTests.RecordingChatClient(_ => """{"score":1}""");
        using var second = new RubricJudgeTests.RecordingChatClient(_ => """{"score":1}""");
        await Task.WhenAll(Create(new ChatClientEvaluator(first)).EvaluateAsync(ShortAnswer),
            Create(new ChatClientEvaluator(second), GroundednessOptions.SupportFocused with { RequireCitations = true }).EvaluateAsync(ShortAnswer));
        Assert.Contains(first.Calls, call => call[1].Text!.Contains("no citations trivially pass", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Calls, call => call[1].Text!.Contains("no citations trivially pass", StringComparison.Ordinal));
        Assert.Contains(second.Calls, call => call[1].Text!.Contains("absent citations do not pass", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_InvalidWeights_RejectsBeforeJudgeCall(double weight) =>
        Assert.Throws<ArgumentException>(() => Create(new FixedJudge(), GroundednessOptions.SupportFocused with { ClaimSupport = new(weight) }));

    [Fact]
    public void Constructor_EmptyPolicyOrConflictingCitations_Rejects()
    {
        var empty = GroundednessOptions.SupportFocused with { ClaimSupport = new(0), ClaimContradicted = new(0), CitationAccuracy = new(0) };
        Assert.Throws<ArgumentException>(() => Create(new FixedJudge(), empty));
        Assert.Throws<ArgumentException>(() => Create(new FixedJudge(), GroundednessOptions.SupportFocused with { ClaimSupport = new(double.MaxValue), ClaimContradicted = new(double.MaxValue) }));
        Assert.Throws<ArgumentException>(() => Create(new FixedJudge(), GroundednessOptions.SupportFocused with { RequireCitations = true, CitationAccuracy = new(1, Enabled: false) }));
        Assert.Throws<ArgumentException>(() => Create(new FixedJudge(), GroundednessOptions.Legacy with { RequireCitations = true }));
    }

    [Fact]
    public async Task Evaluate_CapturedEmptyContext_IsJudgedRatherThanTreatedAsUnavailable()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => """{"score":0,"reasoning":"No evidence supports the claim."}""");
        var result = await Create(new ChatClientEvaluator(client)).EvaluateAsync(new("q", "unsupported claim", Context: ""));
        Assert.Equal(3, client.Calls.Count);
        Assert.False(result.Score.Passed);
        Assert.True(result.Score.CountsTowardAggregate());
        Assert.All(client.Calls, call => Assert.Contains("Context the response must be faithful to:", call[1].Text!));
    }

    [Fact]
    public async Task Evaluate_ZeroWeightRequiredDimension_StillVetoesUnlessDisabled()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(messages =>
            messages[0].Text!.Contains("**claim_contradicted**", StringComparison.Ordinal) ? """{"score":0}""" : """{"score":1}""");
        var result = await Create(new ChatClientEvaluator(client), GroundednessOptions.SupportFocused with { ClaimContradicted = new(0) })
            .EvaluateAsync(ShortAnswer);
        Assert.Equal(1, result.Score.Value);
        Assert.False(result.Score.Passed);
        Assert.Equal(3, client.Calls.Count);
    }

    [Fact]
    public async Task Evaluate_OptionalCompleteness_UsesQueryInsteadOfSourceCoverage()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => """{"score":1}""");
        var result = await Create(new ChatClientEvaluator(client), GroundednessOptions.SupportFocused with { Completeness = new(0.25) })
            .EvaluateAsync(ShortAnswer);
        Assert.Equal(4, client.Calls.Count);
        Assert.Contains(result.Details.SubResults!, r => r.Metric.Key == "answer_completeness");
        Assert.Contains(client.Calls, call => call[1].Text!.Contains("without requiring unrelated context", StringComparison.Ordinal));
        Assert.DoesNotContain(client.Calls, call => call[1].Text!.Contains("leverages a substantial portion", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_WeightAndCitationPolicies_ChangeDefinitionIdentity()
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ => "{}");
        var judge = new ChatClientEvaluator(client);
        string Digest(GroundednessOptions options) => AgentEval.Cli.Infrastructure.CompositeSuiteDefinition.TryCreate(
            "groundedness", (CompositeEval)Create(judge, options).DefinitionInner, ShortAnswer, 1)!.Digest;
        var baseline = Digest(GroundednessOptions.SupportFocused);
        Assert.NotEqual(baseline, Digest(GroundednessOptions.SupportFocused with { ClaimSupport = new(0.7) }));
        Assert.NotEqual(baseline, Digest(GroundednessOptions.SupportFocused with { RequireCitations = true }));
        Assert.NotEqual(baseline, Digest(GroundednessOptions.SupportFocused with { RequireContext = false }));
        Assert.Empty(client.Calls);
    }

    private static GroundednessEval Create(IEvaluator judge, GroundednessOptions? options = null) =>
        new(judge, judgeModel: "scripted", passThreshold: 0.75, options ?? GroundednessOptions.SupportFocused);

    private sealed class FixedJudge : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvaluationResult { OverallScore = 100 });
    }
}
