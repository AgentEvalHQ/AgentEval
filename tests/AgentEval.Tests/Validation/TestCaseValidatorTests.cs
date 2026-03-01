// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using AgentEval.Validation;
using Xunit;

namespace AgentEval.Tests.Validation;

/// <summary>
/// Tests for <see cref="TestCaseValidator"/>.
/// </summary>
public class TestCaseValidatorTests
{
    #region Null TestCase

    [Fact]
    public void Validate_NullTestCase_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TestCaseValidator.Validate(null!));
    }

    #endregion

    #region Name Validation

    [Fact]
    public void Validate_EmptyName_ThrowsArgumentException()
    {
        var testCase = new TestCase { Name = "", Input = "Valid input" };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void Validate_WhitespaceName_ThrowsArgumentException()
    {
        var testCase = new TestCase { Name = "   ", Input = "Valid input" };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("Name", ex.Message);
    }

    #endregion

    #region Input Validation

    [Fact]
    public void Validate_EmptyInput_ThrowsArgumentException()
    {
        var testCase = new TestCase { Name = "Valid Name", Input = "" };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("Input", ex.Message);
    }

    [Fact]
    public void Validate_WhitespaceInput_ThrowsArgumentException()
    {
        var testCase = new TestCase { Name = "Valid Name", Input = "   " };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("Input", ex.Message);
    }

    [Fact]
    public void Validate_EmptyInput_IncludesTestCaseNameInMessage()
    {
        var testCase = new TestCase { Name = "My Cool Test", Input = "" };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("My Cool Test", ex.Message);
    }

    #endregion

    #region PassingScore Validation

    [Fact]
    public void Validate_NegativePassingScore_ThrowsArgumentException()
    {
        var testCase = new TestCase { Name = "Test", Input = "Input", PassingScore = -1 };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("PassingScore", ex.Message);
        Assert.Contains("-1", ex.Message);
    }

    [Fact]
    public void Validate_PassingScoreOver100_ThrowsArgumentException()
    {
        var testCase = new TestCase { Name = "Test", Input = "Input", PassingScore = 101 };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("PassingScore", ex.Message);
        Assert.Contains("101", ex.Message);
    }

    [Fact]
    public void Validate_PassingScoreOver100_IncludesTestCaseName()
    {
        var testCase = new TestCase { Name = "Score Test", Input = "Input", PassingScore = 200 };

        var ex = Assert.Throws<ArgumentException>(() => TestCaseValidator.Validate(testCase));
        Assert.Contains("Score Test", ex.Message);
    }

    #endregion

    #region Valid Test Cases

    [Fact]
    public void Validate_ValidTestCase_DoesNotThrow()
    {
        var testCase = new TestCase { Name = "Valid Test", Input = "Say hello" };

        // Should not throw
        TestCaseValidator.Validate(testCase);
    }

    [Fact]
    public void Validate_PassingScoreZero_DoesNotThrow()
    {
        var testCase = new TestCase { Name = "Always Pass", Input = "Input", PassingScore = 0 };

        TestCaseValidator.Validate(testCase);
    }

    [Fact]
    public void Validate_PassingScore100_DoesNotThrow()
    {
        var testCase = new TestCase { Name = "Perfect Only", Input = "Input", PassingScore = 100 };

        TestCaseValidator.Validate(testCase);
    }

    [Fact]
    public void Validate_DefaultPassingScore_DoesNotThrow()
    {
        // Default PassingScore (70) should be valid
        var testCase = new TestCase { Name = "Default Score", Input = "Input" };

        TestCaseValidator.Validate(testCase);
    }

    [Fact]
    public void Validate_FullyPopulatedTestCase_DoesNotThrow()
    {
        var testCase = new TestCase
        {
            Name = "Full Test",
            Input = "What is the capital of France?",
            ExpectedOutputContains = "Paris",
            GroundTruth = "The capital of France is Paris.",
            PassingScore = 80,
            EvaluationCriteria = ["accuracy", "completeness"],
            ExpectedTools = ["SearchTool"],
            Tags = ["geography"],
            Metadata = new Dictionary<string, object> { ["category"] = "qa" }
        };

        TestCaseValidator.Validate(testCase);
    }

    #endregion

    #region Integration with MAFEvaluationHarness

    [Fact]
    public async Task MAFEvaluationHarness_EmptyInput_ThrowsBeforeRunning()
    {
        // Arrange
        var harness = new AgentEval.MAF.MAFEvaluationHarness(verbose: false);
        var agent = new SimpleTestableAgent("Agent", "Response");
        var badTestCase = new TestCase { Name = "Bad Test", Input = "" };

        // Act & Assert — should throw ArgumentException from validator, not run agent
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.RunEvaluationAsync(agent, badTestCase));
    }

    [Fact]
    public async Task MAFEvaluationHarness_InvalidPassingScore_ThrowsBeforeRunning()
    {
        var harness = new AgentEval.MAF.MAFEvaluationHarness(verbose: false);
        var agent = new SimpleTestableAgent("Agent", "Response");
        var badTestCase = new TestCase { Name = "Bad Score", Input = "Input", PassingScore = 150 };

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.RunEvaluationAsync(agent, badTestCase));
    }

    #endregion
}

#region Test Helpers

internal class SimpleTestableAgent : AgentEval.Core.IEvaluableAgent
{
    private readonly string _responseText;
    public string Name { get; }

    public SimpleTestableAgent(string name, string responseText)
    {
        Name = name;
        _responseText = responseText;
    }

    public Task<AgentEval.Core.AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentEval.Core.AgentResponse { Text = _responseText });
}

#endregion
