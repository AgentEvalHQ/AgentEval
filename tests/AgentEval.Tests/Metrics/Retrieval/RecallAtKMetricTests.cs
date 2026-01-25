// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Retrieval;

namespace AgentEval.Tests.Metrics.Retrieval;

/// <summary>
/// Tests for <see cref="RecallAtKMetric"/>.
/// </summary>
public sealed class RecallAtKMetricTests
{
    private readonly RecallAtKMetric _metric = new();

    [Fact]
    public async Task Name_ReturnsCorrectName()
    {
        // k=10 by default, so name includes "10"
        Assert.Equal("code_recall_at_10", _metric.Name);
    }

    [Fact]
    public void Categories_IncludesRAG()
    {
        Assert.True(_metric.Categories.HasFlag(MetricCategory.RAG));
        Assert.True(_metric.Categories.HasFlag(MetricCategory.CodeBased));
    }

    [Fact]
    public async Task EvaluateAsync_AllRelevantInTopK_Returns100()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc1", "doc2" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
        Assert.Equal(2, result.Details!["relevant_in_top_k"]);
        Assert.Equal(2, result.Details!["total_relevant"]);
    }

    [Fact]
    public async Task EvaluateAsync_SomeRelevantInTopK_ReturnsPartialScore()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc1", "doc4", "doc5", "doc6" }); // 1 of 4 relevant

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(25, result.Score); // 1/4 = 25%
        Assert.False(result.Passed); // Default threshold is 70
    }

    [Fact]
    public async Task EvaluateAsync_NoRelevantInTopK_Returns0()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc4", "doc5" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(0, result.Score);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_WithCustomK_LimitsRetrievedDocs()
    {
        // Arrange
        var metric = new RecallAtKMetric(k: 2);
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3", "doc4" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc3", "doc4" }); // Only in positions 3, 4

        // Act
        var result = await metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(0, result.Score); // Only checking top 2, relevant are in 3 and 4
        Assert.Equal(2, result.Details!["k"]);
    }

    [Fact]
    public async Task EvaluateAsync_MissingRetrievedDocs_Returns0()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RelevantDocumentIds", new[] { "doc1" });
        // Missing RetrievedDocumentIds

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(0, result.Score);
        Assert.Contains("RetrievedDocumentIds", result.Explanation!);
    }

    [Fact]
    public async Task EvaluateAsync_MissingRelevantDocs_Returns0()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1" });
        // Missing RelevantDocumentIds

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(0, result.Score);
        Assert.Contains("RelevantDocumentIds", result.Explanation!);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyRelevantDocs_Returns0()
    {
        // Arrange - No relevant docs means nothing to recall
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2" });
        context.SetProperty("RelevantDocumentIds", Array.Empty<string>());

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert - 0 because there are no relevant docs to find
        Assert.Equal(0, result.Score);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_At70PercentThreshold_PassesCorrectly()
    {
        // Arrange - Default threshold is 70%
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc1", "doc2", "doc4" }); // 2 of 3 = 66.7%

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(67, Math.Round(result.Score));
        Assert.False(result.Passed); // 66.7% < 70% threshold
    }

    [Fact]
    public async Task EvaluateAsync_IncludesMissedDocuments()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc1", "doc2", "doc3" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        var missed = result.Details!["relevant_missed"] as IEnumerable<string>;
        Assert.NotNull(missed);
        Assert.Contains("doc2", missed);
        Assert.Contains("doc3", missed);
    }
}
