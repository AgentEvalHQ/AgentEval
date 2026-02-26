// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.DataLoaders;

/// <summary>
/// Tests for DatasetTestCaseBenchmarkExtensions (bridge from DatasetTestCase to benchmark types).
/// </summary>
public class DatasetTestCaseBenchmarkExtensionsTests
{
    #region ToToolAccuracyTestCase Tests

    [Fact]
    public void ToToolAccuracyTestCase_BasicMapping_CorrectFields()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "weather_test",
            Input = "What is the weather in Seattle?",
            ExpectedTools = ["GetWeather"]
        };

        // Act
        var result = dataset.ToToolAccuracyTestCase();

        // Assert
        Assert.Equal("weather_test", result.Name);
        Assert.Equal("What is the weather in Seattle?", result.Prompt);
        Assert.Single(result.ExpectedTools);
        Assert.Equal("GetWeather", result.ExpectedTools[0].Name);
        Assert.True(result.AllowExtraTools);
    }

    [Fact]
    public void ToToolAccuracyTestCase_WithRequiredParams_ParsesJsonMetadata()
    {
        // Arrange — simulate JSONL deserialization where metadata values are JsonElement
        var jsonDoc = JsonDocument.Parse("""{"GetWeather": ["city", "unit"]}""");
        var metadataDict = new Dictionary<string, object?> { ["required_params"] = jsonDoc.RootElement.Clone() };

        var dataset = new DatasetTestCase
        {
            Id = "weather_params",
            Input = "Check weather",
            ExpectedTools = ["GetWeather"],
            Metadata = metadataDict!
        };

        // Act
        var result = dataset.ToToolAccuracyTestCase();

        // Assert
        Assert.Single(result.ExpectedTools);
        Assert.Equal("GetWeather", result.ExpectedTools[0].Name);
        Assert.Equal(2, result.ExpectedTools[0].RequiredParameters.Count);
        Assert.Contains("city", result.ExpectedTools[0].RequiredParameters);
        Assert.Contains("unit", result.ExpectedTools[0].RequiredParameters);
    }

    [Fact]
    public void ToToolAccuracyTestCase_NoExpectedTools_ReturnsEmptyList()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "no_tools",
            Input = "Just chat"
        };

        // Act
        var result = dataset.ToToolAccuracyTestCase();

        // Assert
        Assert.Empty(result.ExpectedTools);
    }

    [Fact]
    public void ToToolAccuracyTestCase_AllowExtraToolsFalse_SetCorrectly()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "strict",
            Input = "Strict test",
            ExpectedTools = ["Tool1"]
        };

        // Act
        var result = dataset.ToToolAccuracyTestCase(allowExtraTools: false);

        // Assert
        Assert.False(result.AllowExtraTools);
    }

    [Fact]
    public void ToToolAccuracyTestCase_MultipleTools_MapsAll()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "multi_tool",
            Input = "Call multiple tools",
            ExpectedTools = ["ToolA", "ToolB", "ToolC"]
        };

        // Act
        var result = dataset.ToToolAccuracyTestCase();

        // Assert
        Assert.Equal(3, result.ExpectedTools.Count);
        Assert.Equal("ToolA", result.ExpectedTools[0].Name);
        Assert.Equal("ToolB", result.ExpectedTools[1].Name);
        Assert.Equal("ToolC", result.ExpectedTools[2].Name);
    }

    [Fact]
    public void ToToolAccuracyTestCase_NullDataset_ThrowsArgumentNull()
    {
        DatasetTestCase? dataset = null;
        Assert.Throws<ArgumentNullException>(() => dataset!.ToToolAccuracyTestCase());
    }

    [Fact]
    public void ToToolAccuracyTestCase_MetadataWithoutRequiredParams_NoParameters()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "no_params",
            Input = "Simple tool call",
            ExpectedTools = ["SimpleTool"],
            Metadata = new Dictionary<string, object?> { ["other_key"] = "value" }
        };

        // Act
        var result = dataset.ToToolAccuracyTestCase();

        // Assert
        Assert.Single(result.ExpectedTools);
        Assert.Empty(result.ExpectedTools[0].RequiredParameters);
    }

    #endregion

    #region ToTaskCompletionTestCase Tests

    [Fact]
    public void ToTaskCompletionTestCase_BasicMapping_CorrectFields()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "task_test",
            Input = "Complete this task",
            EvaluationCriteria = ["Responds correctly", "Is complete"],
            PassingScore = 80
        };

        // Act
        var result = dataset.ToTaskCompletionTestCase();

        // Assert
        Assert.Equal("task_test", result.Name);
        Assert.Equal("Complete this task", result.Prompt);
        Assert.Equal(2, result.CompletionCriteria.Count);
        Assert.Contains("Responds correctly", result.CompletionCriteria);
        Assert.Contains("Is complete", result.CompletionCriteria);
        Assert.Equal(80, result.PassingScore);
    }

    [Fact]
    public void ToTaskCompletionTestCase_NoPassingScore_UsesDefault()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "default_score",
            Input = "Test",
            PassingScore = null
        };

        // Act
        var result = dataset.ToTaskCompletionTestCase();

        // Assert
        Assert.Equal(EvaluationDefaults.DefaultPassingScore, result.PassingScore);
    }

    [Fact]
    public void ToTaskCompletionTestCase_NoCriteria_EmptyList()
    {
        // Arrange
        var dataset = new DatasetTestCase
        {
            Id = "no_criteria",
            Input = "Test"
        };

        // Act
        var result = dataset.ToTaskCompletionTestCase();

        // Assert
        Assert.Empty(result.CompletionCriteria);
    }

    [Fact]
    public void ToTaskCompletionTestCase_NullDataset_ThrowsArgumentNull()
    {
        DatasetTestCase? dataset = null;
        Assert.Throws<ArgumentNullException>(() => dataset!.ToTaskCompletionTestCase());
    }

    #endregion
}
