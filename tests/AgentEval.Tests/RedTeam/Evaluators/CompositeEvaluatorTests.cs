// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Evaluators/CompositeEvaluatorTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class CompositeEvaluatorTests
{
    private static readonly AttackProbe TestProbe = new()
    {
        Id = "TEST-001",
        Prompt = "Test prompt",
        Difficulty = Difficulty.Easy
    };

    [Fact]
    public void Name_ContainsChildEvaluatorNames()
    {
        var evaluator = new CompositeEvaluator(
            new ContainsTokenEvaluator("A"),
            new ContainsTokenEvaluator("B"));

        Assert.Contains("ContainsToken", evaluator.Name);
        Assert.StartsWith("Composite(", evaluator.Name);
    }

    [Fact]
    public void Constructor_WithNoEvaluators_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CompositeEvaluator());
        Assert.Contains("At least one evaluator must be specified", ex.Message);
    }

    [Fact]
    public void Constructor_WithNullEvaluators_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeEvaluator(null!));
    }

    [Fact]
    public void Evaluators_ReturnsConfiguredEvaluators()
    {
        var eval1 = new ContainsTokenEvaluator("A");
        var eval2 = new ContainsTokenEvaluator("B");
        var composite = new CompositeEvaluator(eval1, eval2);

        Assert.Equal(2, composite.Evaluators.Count);
        Assert.Contains(eval1, composite.Evaluators);
        Assert.Contains(eval2, composite.Evaluators);
    }

    [Fact]
    public async Task EvaluateAsync_AnyStrategy_SucceedsIfAnySucceeds()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Any,
            new ContainsTokenEvaluator("TOKEN1"),
            new ContainsTokenEvaluator("TOKEN2"));

        // Only TOKEN2 present
        var result = await composite.EvaluateAsync(TestProbe, "Contains TOKEN2");

        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task EvaluateAsync_AnyStrategy_ResistsIfAllResist()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Any,
            new ContainsTokenEvaluator("TOKEN1"),
            new ContainsTokenEvaluator("TOKEN2"));

        var result = await composite.EvaluateAsync(TestProbe, "No tokens here");

        Assert.True(result.AttackResisted);
    }

    [Fact]
    public async Task EvaluateAsync_AllStrategy_SucceedsOnlyIfAllSucceed()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.All,
            new ContainsTokenEvaluator("TOKEN1"),
            new ContainsTokenEvaluator("TOKEN2"));

        // Only one token
        var result1 = await composite.EvaluateAsync(TestProbe, "TOKEN1 only");
        Assert.False(result1.AttackSucceeded);

        // Both tokens
        var result2 = await composite.EvaluateAsync(TestProbe, "TOKEN1 and TOKEN2");
        Assert.True(result2.AttackSucceeded);
    }

    [Fact]
    public async Task EvaluateAsync_MajorityStrategy_SucceedsIfMajoritySucceeds()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Majority,
            new ContainsTokenEvaluator("TOKEN1"),
            new ContainsTokenEvaluator("TOKEN2"),
            new ContainsTokenEvaluator("TOKEN3"));

        // 1 of 3 = not majority
        var result1 = await composite.EvaluateAsync(TestProbe, "TOKEN1 only");
        Assert.False(result1.AttackSucceeded);

        // 2 of 3 = majority
        var result2 = await composite.EvaluateAsync(TestProbe, "TOKEN1 and TOKEN2");
        Assert.True(result2.AttackSucceeded);
    }

    [Fact]
    public async Task EvaluateAsync_DefaultsToAnyStrategy()
    {
        var composite = new CompositeEvaluator(
            new ContainsTokenEvaluator("TOKEN1"),
            new ContainsTokenEvaluator("TOKEN2"));

        // Only one token should succeed with Any strategy
        var result = await composite.EvaluateAsync(TestProbe, "TOKEN1");
        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task EvaluateAsync_AggregatesMatchedItems()
    {
        var composite = new CompositeEvaluator(
            new ContainsTokenEvaluator("TOKEN1"),
            new ContainsTokenEvaluator("TOKEN2"));

        var result = await composite.EvaluateAsync(TestProbe, "TOKEN1 and TOKEN2");

        Assert.NotNull(result.MatchedItems);
        Assert.Contains("TOKEN1", result.MatchedItems!);
        Assert.Contains("TOKEN2", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_CalculatesConfidenceFromSuccessRatio()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Any,
            new ContainsTokenEvaluator("TOKEN1"),
            new ContainsTokenEvaluator("TOKEN2"),
            new ContainsTokenEvaluator("TOKEN3"),
            new ContainsTokenEvaluator("TOKEN4"));

        // 2 of 4 succeed
        var result = await composite.EvaluateAsync(TestProbe, "TOKEN1 TOKEN2");

        Assert.Equal(0.5, result.Confidence); // 2/4
    }

    [Fact]
    public async Task EvaluateAsync_WithNullProbe_ThrowsArgumentNullException()
    {
        var composite = new CompositeEvaluator(new ContainsTokenEvaluator("TOKEN"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.EvaluateAsync(null!, "response"));
    }

    [Fact]
    public async Task EvaluateAsync_WithNullResponse_ThrowsArgumentNullException()
    {
        var composite = new CompositeEvaluator(new ContainsTokenEvaluator("TOKEN"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => composite.EvaluateAsync(TestProbe, null!));
    }

    [Fact]
    public async Task EvaluateAsync_AllEvaluatorsInconclusive_ReturnsInconclusive()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Any,
            new AlwaysInconclusiveEvaluator(),
            new AlwaysInconclusiveEvaluator());

        var result = await composite.EvaluateAsync(TestProbe, "some response");

        Assert.True(result.IsInconclusive);
        Assert.Contains("inconclusive", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0.5, result.Confidence);
    }

    [Fact]
    public async Task EvaluateAsync_AllStrategy_AllInconclusive_ReturnsInconclusive()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.All,
            new AlwaysInconclusiveEvaluator(),
            new AlwaysInconclusiveEvaluator(),
            new AlwaysInconclusiveEvaluator());

        var result = await composite.EvaluateAsync(TestProbe, "test");

        Assert.True(result.IsInconclusive);
    }

    [Fact]
    public async Task EvaluateAsync_MajorityStrategy_AllInconclusive_ReturnsInconclusive()
    {
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Majority,
            new AlwaysInconclusiveEvaluator(),
            new AlwaysInconclusiveEvaluator());

        var result = await composite.EvaluateAsync(TestProbe, "test");

        Assert.True(result.IsInconclusive);
    }

    [Fact]
    public async Task EvaluateAsync_MixOfInconclusiveAndResisted_ReturnsResisted()
    {
        // One inconclusive + one resisted → not all inconclusive, so normal logic applies
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Any,
            new AlwaysInconclusiveEvaluator(),
            new ContainsTokenEvaluator("NOT_PRESENT"));

        var result = await composite.EvaluateAsync(TestProbe, "no match here");

        // Neither succeeded → resisted via normal path
        Assert.True(result.AttackResisted);
    }

    [Fact]
    public async Task EvaluateAsync_InconclusiveAndSucceeded_ReturnsSucceeded()
    {
        // One inconclusive + one succeeded → attack succeeded (Any strategy)
        var composite = new CompositeEvaluator(
            CompositeEvaluator.AggregationStrategy.Any,
            new AlwaysInconclusiveEvaluator(),
            new ContainsTokenEvaluator("FOUND"));

        var result = await composite.EvaluateAsync(TestProbe, "FOUND in response");

        Assert.True(result.AttackSucceeded);
    }

    private class AlwaysInconclusiveEvaluator : IProbeEvaluator
    {
        public string Name => "AlwaysInconclusive";

        public Task<EvaluationResult> EvaluateAsync(
            AttackProbe probe, string response,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(EvaluationResult.Inconclusive("Test inconclusive"));
        }
    }
}
