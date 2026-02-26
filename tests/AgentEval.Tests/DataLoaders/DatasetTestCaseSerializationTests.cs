// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.DataLoaders;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.DataLoaders;

/// <summary>
/// Round-trip serialization tests for <see cref="DatasetTestCase"/> and <see cref="GroundTruthToolCall"/>.
/// </summary>
public class DatasetTestCaseSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void DatasetTestCase_FullyPopulated_RoundTrips()
    {
        // Arrange
        var original = new DatasetTestCase
        {
            Id = "tc-001",
            Category = "rag",
            Input = "What is the capital of France?",
            ExpectedOutput = "Paris",
            Context = new[] { "France is in Europe.", "Paris is the capital." },
            ExpectedTools = new[] { "SearchTool", "LookupTool" },
            GroundTruth = new GroundTruthToolCall
            {
                Name = "SearchTool",
                Arguments = new Dictionary<string, object?>
                {
                    ["query"] = "capital of France",
                    ["limit"] = 5
                }
            },
            EvaluationCriteria = new[] { "Must mention Paris", "Be concise" },
            Tags = new[] { "geography", "europe" },
            PassingScore = 80,
            Metadata = new Dictionary<string, object?> { ["source"] = "quiz_bank" }
        };

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<DatasetTestCase>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized!.Id);
        Assert.Equal(original.Category, deserialized.Category);
        Assert.Equal(original.Input, deserialized.Input);
        Assert.Equal(original.ExpectedOutput, deserialized.ExpectedOutput);
        Assert.Equal(original.PassingScore, deserialized.PassingScore);
    }

    [Fact]
    public void DatasetTestCase_MinimalFields_RoundTrips()
    {
        // Arrange
        var original = new DatasetTestCase
        {
            Input = "Hello"
        };

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<DatasetTestCase>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("Hello", deserialized!.Input);
        Assert.Equal("", deserialized.Id);
        Assert.Null(deserialized.Category);
        Assert.Null(deserialized.ExpectedOutput);
        Assert.Null(deserialized.Context);
        Assert.Null(deserialized.ExpectedTools);
        Assert.Null(deserialized.GroundTruth);
        Assert.Null(deserialized.EvaluationCriteria);
        Assert.Null(deserialized.Tags);
        Assert.Null(deserialized.PassingScore);
    }

    [Fact]
    public void GroundTruthToolCall_RoundTrips()
    {
        // Arrange
        var original = new GroundTruthToolCall
        {
            Name = "WeatherTool",
            Arguments = new Dictionary<string, object?>
            {
                ["city"] = "Seattle",
                ["units"] = "metric",
                ["detailed"] = true,
                ["nullArg"] = null
            }
        };

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<GroundTruthToolCall>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("WeatherTool", deserialized!.Name);
        Assert.NotNull(deserialized.Arguments);
        Assert.Equal(4, deserialized.Arguments.Count);
    }

    [Fact]
    public void GroundTruthToolCall_EmptyArguments_RoundTrips()
    {
        // Arrange
        var original = new GroundTruthToolCall
        {
            Name = "NoArgsTool",
            Arguments = new Dictionary<string, object?>()
        };

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<GroundTruthToolCall>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("NoArgsTool", deserialized!.Name);
        Assert.Empty(deserialized.Arguments);
    }

    [Fact]
    public void DatasetTestCase_WithUnicode_RoundTrips()
    {
        // Arrange
        var original = new DatasetTestCase
        {
            Id = "unicode-test",
            Input = "日本語の質問 🚀",
            ExpectedOutput = "Réponse en français 🎉",
            Tags = new[] { "日本語", "emoji" }
        };

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<DatasetTestCase>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Input, deserialized!.Input);
        Assert.Equal(original.ExpectedOutput, deserialized.ExpectedOutput);
    }

    [Fact]
    public void DatasetTestCase_MetadataWithNullValues_RoundTrips()
    {
        // Arrange
        var original = new DatasetTestCase
        {
            Input = "test",
            Metadata = new Dictionary<string, object?>
            {
                ["key1"] = "value1",
                ["key2"] = null,
                ["key3"] = 42
            }
        };

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<DatasetTestCase>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized!.Metadata.Count);
    }
}
