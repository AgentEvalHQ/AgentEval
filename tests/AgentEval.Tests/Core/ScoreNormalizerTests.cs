// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ScoreNormalizer functionality.
/// </summary>
public class ScoreNormalizerTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 25.0)]
    [InlineData(3.0, 50.0)]
    [InlineData(4.0, 75.0)]
    [InlineData(5.0, 100.0)]
    [InlineData(1.5, 12.5)]
    [InlineData(4.5, 87.5)]
    public void FromOneToFive_ConvertsCorrectly(double input, double expected)
    {
        var result = ScoreNormalizer.FromOneToFive(input);
        Assert.Equal(expected, result, precision: 2);
    }
    
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(25.0, 2.0)]
    [InlineData(50.0, 3.0)]
    [InlineData(75.0, 4.0)]
    [InlineData(100.0, 5.0)]
    [InlineData(12.5, 1.5)]
    [InlineData(87.5, 4.5)]
    public void ToOneToFive_ConvertsCorrectly(double input, double expected)
    {
        var result = ScoreNormalizer.ToOneToFive(input);
        Assert.Equal(expected, result, precision: 2);
    }
    
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 50.0)]
    [InlineData(1.0, 100.0)]
    [InlineData(0.25, 25.0)]
    [InlineData(0.75, 75.0)]
    public void FromSimilarity_ConvertsCorrectly(double input, double expected)
    {
        var result = ScoreNormalizer.FromSimilarity(input);
        Assert.Equal(expected, result, precision: 2);
    }
    
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(50.0, 0.5)]
    [InlineData(100.0, 1.0)]
    [InlineData(25.0, 0.25)]
    [InlineData(75.0, 0.75)]
    public void ToSimilarity_ConvertsCorrectly(double input, double expected)
    {
        var result = ScoreNormalizer.ToSimilarity(input);
        Assert.Equal(expected, result, precision: 2);
    }
    
    [Fact]
    public void FromOneToFive_ClampsInputBelow1()
    {
        var result = ScoreNormalizer.FromOneToFive(0.0);
        Assert.Equal(0.0, result);
    }
    
    [Fact]
    public void FromOneToFive_ClampsInputAbove5()
    {
        var result = ScoreNormalizer.FromOneToFive(6.0);
        Assert.Equal(100.0, result);
    }
    
    [Fact]
    public void FromSimilarity_ClampsNegativeInput()
    {
        var result = ScoreNormalizer.FromSimilarity(-0.5);
        Assert.Equal(0.0, result);
    }
    
    [Fact]
    public void FromSimilarity_ClampsInputAbove1()
    {
        var result = ScoreNormalizer.FromSimilarity(1.5);
        Assert.Equal(100.0, result);
    }
    
    [Theory]
    [InlineData(95.0, "Excellent")]
    [InlineData(80.0, "Good")]
    [InlineData(65.0, "Satisfactory")]
    [InlineData(50.0, "Needs Improvement")]
    [InlineData(30.0, "Poor")]
    [InlineData(10.0, "Very Poor")]
    public void Interpret_ReturnsCorrectInterpretation(double score, string expected)
    {
        var result = ScoreNormalizer.Interpret(score);
        Assert.Equal(expected, result);
    }
    
    [Theory]
    [InlineData(70.0, 70.0, true)]
    [InlineData(69.9, 70.0, false)]
    [InlineData(100.0, 70.0, true)]
    [InlineData(0.0, 70.0, false)]
    [InlineData(50.0, 50.0, true)]
    public void Passes_ReturnsCorrectResult(double score, double threshold, bool expected)
    {
        var result = ScoreNormalizer.Passes(score, threshold);
        Assert.Equal(expected, result);
    }
    
    [Fact]
    public void Passes_UsesDefaultThreshold70()
    {
        Assert.True(ScoreNormalizer.Passes(70.0));
        Assert.False(ScoreNormalizer.Passes(69.9));
    }
    
    [Fact]
    public void RoundTrip_OneToFive_PreservesValue()
    {
        for (double i = 1.0; i <= 5.0; i += 0.5)
        {
            var normalized = ScoreNormalizer.FromOneToFive(i);
            var restored = ScoreNormalizer.ToOneToFive(normalized);
            Assert.Equal(i, restored, precision: 2);
        }
    }
    
    [Fact]
    public void RoundTrip_Similarity_PreservesValue()
    {
        for (double i = 0.0; i <= 1.0; i += 0.1)
        {
            var normalized = ScoreNormalizer.FromSimilarity(i);
            var restored = ScoreNormalizer.ToSimilarity(normalized);
            Assert.Equal(i, restored, precision: 2);
        }
    }
}
