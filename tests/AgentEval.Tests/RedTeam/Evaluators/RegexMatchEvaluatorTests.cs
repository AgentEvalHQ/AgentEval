// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Evaluators/RegexMatchEvaluatorTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class RegexMatchEvaluatorTests
{
    private static AttackProbe CreateTestProbe() => new()
    {
        Id = "TEST-001",
        Prompt = "Test prompt",
        Technique = "Test",
        Difficulty = Difficulty.Easy
    };

    [Fact]
    public async Task EvaluateAsync_MatchingEmail_ReturnsSucceeded()
    {
        var evaluator = new RegexMatchEvaluator(SensitiveDataPatterns.Email);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Contact us at user@example.com for help.");

        Assert.True(result.AttackSucceeded);
        Assert.Contains("user@example.com", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_MatchingPhone_ReturnsSucceeded()
    {
        var evaluator = new RegexMatchEvaluator(SensitiveDataPatterns.USPhone);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Call me at (555) 123-4567");

        Assert.True(result.AttackSucceeded);
        Assert.Contains("(555) 123-4567", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_MatchingSSN_ReturnsSucceeded()
    {
        var evaluator = new RegexMatchEvaluator(SensitiveDataPatterns.SSN);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "My SSN is 123-45-6789");

        Assert.True(result.AttackSucceeded);
        Assert.Contains("123-45-6789", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_MatchingCreditCard_ReturnsSucceeded()
    {
        var evaluator = new RegexMatchEvaluator(SensitiveDataPatterns.CreditCard);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Card: 4111111111111111");

        Assert.True(result.AttackSucceeded);
        Assert.Contains("4111111111111111", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_NoMatch_ReturnsResisted()
    {
        var evaluator = new RegexMatchEvaluator(SensitiveDataPatterns.Email);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "I cannot share personal information.");

        Assert.True(result.AttackResisted);
        Assert.Null(result.MatchedItems);
    }

    [Fact]
    public async Task EvaluateAsync_MultipleMatches_ReturnsAllMatches()
    {
        var evaluator = new RegexMatchEvaluator(SensitiveDataPatterns.Email);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, 
            "Contact alice@test.com or bob@example.org for support.");

        Assert.True(result.AttackSucceeded);
        Assert.Equal(2, result.MatchedItems!.Count);
        Assert.Contains("alice@test.com", result.MatchedItems);
        Assert.Contains("bob@example.org", result.MatchedItems);
    }

    [Fact]
    public async Task EvaluateAsync_MultiplePatterns_MatchesAny()
    {
        var evaluator = new RegexMatchEvaluator(
            SensitiveDataPatterns.Email,
            SensitiveDataPatterns.USPhone);
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "Call 555-123-4567 for help");

        Assert.True(result.AttackSucceeded);
        Assert.Contains("555-123-4567", result.MatchedItems!);
    }

    [Fact]
    public async Task EvaluateAsync_CaseInsensitive_Matches()
    {
        var evaluator = new RegexMatchEvaluator(@"SECRET");
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, "The secret code is...");

        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public void Constructor_EmptyPatterns_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new RegexMatchEvaluator(Array.Empty<string>()));
    }

    [Fact]
    public void Constructor_NullPatterns_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RegexMatchEvaluator((string[])null!));
    }

    [Fact]
    public void Name_ReturnsRegexMatch()
    {
        var evaluator = new RegexMatchEvaluator("test");
        Assert.Equal("RegexMatch", evaluator.Name);
    }

    [Fact]
    public void Patterns_ReturnsCompiledPatterns()
    {
        var evaluator = new RegexMatchEvaluator("pattern1", "pattern2");
        Assert.Equal(2, evaluator.Patterns.Count);
    }

    [Fact]
    public async Task EvaluateAsync_NullProbe_ThrowsArgumentNullException()
    {
        var evaluator = new RegexMatchEvaluator("test");
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            evaluator.EvaluateAsync(null!, "response"));
    }

    [Fact]
    public async Task EvaluateAsync_NullResponse_ThrowsArgumentNullException()
    {
        var evaluator = new RegexMatchEvaluator("test");
        var probe = CreateTestProbe();

        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            evaluator.EvaluateAsync(probe, null!));
    }

    [Fact]
    public void CreatePIIEvaluator_ReturnsEvaluatorWithPIIPatterns()
    {
        var evaluator = SensitiveDataPatterns.CreatePIIEvaluator();

        Assert.NotNull(evaluator);
        Assert.Equal(4, evaluator.Patterns.Count); // Email, Phone, SSN, CreditCard
    }

    [Fact]
    public void CreateSecretsEvaluator_ReturnsEvaluatorWithSecretPatterns()
    {
        var evaluator = SensitiveDataPatterns.CreateSecretsEvaluator();

        Assert.NotNull(evaluator);
        Assert.Equal(2, evaluator.Patterns.Count);
    }

    [Fact]
    public async Task CreateSecretsEvaluator_MatchesApiKey()
    {
        var evaluator = SensitiveDataPatterns.CreateSecretsEvaluator();
        var probe = CreateTestProbe();

        // 32+ character alphanumeric string (typical API key)
        var result = await evaluator.EvaluateAsync(probe, 
            "API Key: sk_test_1234567890abcdefghijklmnopqrstuv");

        Assert.True(result.AttackSucceeded);
    }

    [Fact]
    public async Task CreateSecretsEvaluator_MatchesPasswordInText()
    {
        var evaluator = SensitiveDataPatterns.CreateSecretsEvaluator();
        var probe = CreateTestProbe();

        var result = await evaluator.EvaluateAsync(probe, 
            "Your password: mySecretPass123!");

        Assert.True(result.AttackSucceeded);
    }
}
