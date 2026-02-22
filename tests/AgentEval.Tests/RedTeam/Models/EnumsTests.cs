// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Models/EnumsTests.cs
using AgentEval.RedTeam;

namespace AgentEval.Tests.RedTeam.Models;

/// <summary>
/// Tests for RedTeam enumerations.
/// </summary>
public class EnumsTests
{
    [Fact]
    public void Intensity_HasExpectedValues()
    {
        var values = Enum.GetValues<Intensity>();

        Assert.Contains(Intensity.Quick, values);
        Assert.Contains(Intensity.Moderate, values);
        Assert.Contains(Intensity.Comprehensive, values);
        Assert.Equal(3, values.Length);
    }

    [Fact]
    public void Difficulty_HasExpectedValues()
    {
        var values = Enum.GetValues<Difficulty>();

        Assert.Contains(Difficulty.Easy, values);
        Assert.Contains(Difficulty.Moderate, values);
        Assert.Contains(Difficulty.Hard, values);
        Assert.Equal(3, values.Length);
    }

    [Fact]
    public void Verdict_HasExpectedValues()
    {
        var values = Enum.GetValues<Verdict>();

        Assert.Contains(Verdict.Pass, values);
        Assert.Contains(Verdict.Fail, values);
        Assert.Contains(Verdict.PartialPass, values);
        Assert.Contains(Verdict.Inconclusive, values);
        Assert.Equal(4, values.Length);
    }

    [Fact]
    public void Severity_HasExpectedValues()
    {
        var values = Enum.GetValues<Severity>();

        Assert.Contains(Severity.Critical, values);
        Assert.Contains(Severity.High, values);
        Assert.Contains(Severity.Medium, values);
        Assert.Contains(Severity.Low, values);
        Assert.Contains(Severity.Informational, values);
        Assert.Equal(5, values.Length);
    }

    [Fact]
    public void Severity_HasCorrectOrdering()
    {
        // Higher severity should have higher numeric value for proper comparison
        Assert.True(Severity.Critical > Severity.High);
        Assert.True(Severity.High > Severity.Medium);
        Assert.True(Severity.Medium > Severity.Low);
        Assert.True(Severity.Low > Severity.Informational);
    }

    [Fact]
    public void EvaluationOutcome_HasExpectedValues()
    {
        var values = Enum.GetValues<EvaluationOutcome>();

        Assert.Contains(EvaluationOutcome.Succeeded, values);
        Assert.Contains(EvaluationOutcome.Resisted, values);
        Assert.Contains(EvaluationOutcome.Inconclusive, values);
        Assert.Equal(3, values.Length);
    }
}
