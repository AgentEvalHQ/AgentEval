// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Retrieval;

namespace AgentEval.Tests.Metrics.Retrieval;

/// <summary>
/// Tests for <see cref="MRRMetric"/>.
/// </summary>
public sealed class MRRMetricTests
{
    private readonly MRRMetric _metric = new();

    [Fact]
    public async Task Name_ReturnsCorrectName()
    {
        Assert.Equal("code_mrr", _metric.Name);
    }

    [Fact]
    public void Categories_IncludesRAG()
    {
        Assert.True(_metric.Categories.HasFlag(MetricCategory.RAG));
        Assert.True(_metric.Categories.HasFlag(MetricCategory.CodeBased));
    }

    [Fact]
    public async Task EvaluateAsync_FirstDocRelevant_Returns100()
    {
        // Arrange - Relevant doc at rank 1, MRR = 1/1 = 100%
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc1" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
        Assert.Equal(1, result.Details!["first_relevant_rank"]);
    }

    [Fact]
    public async Task EvaluateAsync_SecondDocRelevant_Returns50()
    {
        // Arrange - Relevant doc at rank 2, MRR = 1/2 = 50%
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc2" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(50, result.Score); // 1/2 * 100
        Assert.True(result.Passed); // Position 2 is in top 3 threshold
        Assert.Equal(2, result.Details!["first_relevant_rank"]);
    }

    [Fact]
    public async Task EvaluateAsync_ThirdDocRelevant_Returns33()
    {
        // Arrange - Relevant doc at rank 3, MRR = 1/3 ≈ 33%
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc3" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(33, Math.Round(result.Score)); // 1/3 * 100 ≈ 33.33
        Assert.Equal(3, result.Details!["first_relevant_rank"]);
    }

    [Fact]
    public async Task EvaluateAsync_NoRelevantInResults_Returns0()
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
        Assert.Equal(0, result.Details!["first_relevant_rank"]);
    }

    [Fact]
    public async Task EvaluateAsync_MultipleRelevant_UsesFirstFound()
    {
        // Arrange - Multiple relevant docs, should use rank of first one found
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3", "doc4" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc2", "doc4" }); // First relevant at rank 2

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(50, result.Score); // 1/2 * 100
        Assert.Equal(2, result.Details!["first_relevant_rank"]);
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
    public async Task EvaluateAsync_WithMaxRank_ConsidersOnlyTopN()
    {
        // Arrange - maxRank limits which positions to consider
        var metric = new MRRMetric(maxRank: 2);
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2", "doc3" });
        context.SetProperty("RelevantDocumentIds", new[] { "doc3" }); // rank 3, but maxRank is 2

        // Act
        var result = await metric.EvaluateAsync(context);

        // Assert - Should be 0 since relevant doc is beyond maxRank
        Assert.Equal(0, result.Score);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_TenthDocRelevant_Returns10()
    {
        // Arrange - Relevant doc at rank 10, MRR = 1/10 = 10%
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        var retrieved = Enumerable.Range(1, 10).Select(i => $"doc{i}").ToArray();
        context.SetProperty("RetrievedDocumentIds", retrieved);
        context.SetProperty("RelevantDocumentIds", new[] { "doc10" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(10, result.Score); // 1/10 * 100
        Assert.Equal(10, result.Details!["first_relevant_rank"]);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyRetrievedDocs_Returns0()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", Array.Empty<string>());
        context.SetProperty("RelevantDocumentIds", new[] { "doc1" });

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyRelevantDocs_Returns0()
    {
        // Arrange - No relevant docs means nothing to find
        var context = new EvaluationContext
        {
            Input = "test query",
            Output = "test output"
        };
        context.SetProperty("RetrievedDocumentIds", new[] { "doc1", "doc2" });
        context.SetProperty("RelevantDocumentIds", Array.Empty<string>());

        // Act
        var result = await _metric.EvaluateAsync(context);

        // Assert
        Assert.Equal(0, result.Score);
    }
}
