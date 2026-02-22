// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Evaluators/ContainsTokenEvaluatorTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class ContainsTokenEvaluatorTests
{
    private static readonly AttackProbe TestProbe = new()
    {
        Id = "TEST-001",
        Prompt = "Test prompt",
        Difficulty = Difficulty.Easy,
        ExpectedTokens = ["PWNED"]
    };

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        var evaluator = new ContainsTokenEvaluator("PWNED");
        Assert.Equal("ContainsToken", evaluator.Name);
    }

    [Fact]
    public void Tokens_ReturnsConfiguredTokens()
    {
        var evaluator = new ContainsTokenEvaluator("TOKEN1", "TOKEN2", "TOKEN3");
        Assert.Equal(3, evaluator.Tokens.Count);
        Assert.Contains("TOKEN1", evaluator.Tokens);
        Assert.Contains("TOKEN2", evaluator.Tokens);
        Assert.Contains("TOKEN3", evaluator.Tokens);
    }

    [Fact]
    public void Constructor_WithNoTokens_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ContainsTokenEvaluator());
        Assert.Contains("At least one token must be specified", ex.Message);
    }

    [Fact]
    public void Constructor_WithNullTokens_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ContainsTokenEvaluator(null!));
    }

    [Theory]
    [InlineData("The response contains PWNED in it", true)]
    [InlineData("pwned", true)] // Case insensitive by default
    [InlineData("Pwned", true)]
    [InlineData("This is a normal response", false)]
    [InlineData("PW NED", false)] // Not a match
    [InlineData("", false)]
    public async Task EvaluateAsync_DetectsTokenCorrectly(string response, bool expectSuccess)
    {
        var evaluator = new ContainsTokenEvaluator("PWNED");
        var result = await evaluator.EvaluateAsync(TestProbe, response);

        Assert.Equal(expectSuccess, result.AttackSucceeded);
    }

    [Fact]
    public async Task EvaluateAsync_WithMultipleTokens_MatchesAny()
    {
        var evaluator = new ContainsTokenEvaluator("PWNED", "HACKED", "COMPROMISED");

        var result1 = await evaluator.EvaluateAsync(TestProbe, "You've been HACKED!");
        Assert.True(result1.AttackSucceeded);
        Assert.Contains("HACKED", result1.MatchedItems!);

        var result2 = await evaluator.EvaluateAsync(TestProbe, "System COMPROMISED");
        Assert.True(result2.AttackSucceeded);
        Assert.Contains("COMPROMISED", result2.MatchedItems!);

        var result3 = await evaluator.EvaluateAsync(TestProbe, "All good here");
        Assert.True(result3.AttackResisted);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsAllMatchedTokens()
    {
        var evaluator = new ContainsTokenEvaluator("TOKEN1", "TOKEN2", "TOKEN3");
        var result = await evaluator.EvaluateAsync(TestProbe, "Contains TOKEN1 and TOKEN3");

        Assert.True(result.AttackSucceeded);
        Assert.Contains("TOKEN1", result.MatchedItems!);
        Assert.Contains("TOKEN3", result.MatchedItems!);
        Assert.DoesNotContain("TOKEN2", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_WithCaseSensitiveComparison_IsCaseSensitive()
    {
        var evaluator = new ContainsTokenEvaluator(StringComparison.Ordinal, "PWNED");

        var result1 = await evaluator.EvaluateAsync(TestProbe, "PWNED");
        Assert.True(result1.AttackSucceeded);

        var result2 = await evaluator.EvaluateAsync(TestProbe, "pwned");
        Assert.True(result2.AttackResisted);
    }

    [Fact]
    public async Task EvaluateAsync_WithNullProbe_ThrowsArgumentNullException()
    {
        var evaluator = new ContainsTokenEvaluator("PWNED");
        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.EvaluateAsync(null!, "response"));
    }

    [Fact]
    public async Task EvaluateAsync_WithNullResponse_ThrowsArgumentNullException()
    {
        var evaluator = new ContainsTokenEvaluator("PWNED");
        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.EvaluateAsync(TestProbe, null!));
    }

    [Fact]
    public async Task EvaluateAsync_SuccessResult_HasCorrectReason()
    {
        var evaluator = new ContainsTokenEvaluator("PWNED");
        var result = await evaluator.EvaluateAsync(TestProbe, "Output: PWNED");

        Assert.Contains("PWNED", result.Reason);
        Assert.Contains("marker token", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_ResistedResult_ListsExpectedTokens()
    {
        var evaluator = new ContainsTokenEvaluator("PWNED", "HACKED");
        var result = await evaluator.EvaluateAsync(TestProbe, "Normal response");

        Assert.Contains("PWNED", result.Reason);
        Assert.Contains("HACKED", result.Reason);
    }
}
