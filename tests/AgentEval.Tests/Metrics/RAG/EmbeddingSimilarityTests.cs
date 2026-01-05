// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Embeddings;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for EmbeddingSimilarity calculations.
/// </summary>
public class EmbeddingSimilarityTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        // Arrange
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { 1.0f, 2.0f, 3.0f };
        
        // Act
        var result = EmbeddingSimilarity.CosineSimilarity(a, b);
        
        // Assert
        Assert.Equal(1.0f, result, precision: 5);
    }
    
    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        // Arrange
        var a = new float[] { 1.0f, 0.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f, 0.0f };
        
        // Act
        var result = EmbeddingSimilarity.CosineSimilarity(a, b);
        
        // Assert
        Assert.Equal(0.0f, result, precision: 5);
    }
    
    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        // Arrange
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { -1.0f, -2.0f, -3.0f };
        
        // Act
        var result = EmbeddingSimilarity.CosineSimilarity(a, b);
        
        // Assert
        Assert.Equal(-1.0f, result, precision: 5);
    }
    
    [Fact]
    public void CosineSimilarity_SimilarVectors_ReturnsHighValue()
    {
        // Arrange
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { 1.1f, 2.1f, 3.1f };
        
        // Act
        var result = EmbeddingSimilarity.CosineSimilarity(a, b);
        
        // Assert
        Assert.True(result > 0.99f, $"Expected > 0.99, got {result}");
    }
    
    [Fact]
    public void CosineSimilarity_DifferentDimensions_ThrowsArgumentException()
    {
        // Arrange
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { 1.0f, 2.0f };
        
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => 
            EmbeddingSimilarity.CosineSimilarity(a, b));
        
        Assert.Contains("dimensions must match", ex.Message);
    }
    
    [Fact]
    public void CosineSimilarity_ReadOnlyMemory_WorksCorrectly()
    {
        // Arrange
        ReadOnlyMemory<float> a = new float[] { 1.0f, 0.0f };
        ReadOnlyMemory<float> b = new float[] { 0.7071f, 0.7071f }; // 45 degrees
        
        // Act
        var result = EmbeddingSimilarity.CosineSimilarity(a, b);
        
        // Assert - cos(45°) ≈ 0.707
        Assert.True(result > 0.70f && result < 0.72f, $"Expected ~0.707, got {result}");
    }
    
    [Fact]
    public void EuclideanDistance_IdenticalVectors_ReturnsZero()
    {
        // Arrange
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { 1.0f, 2.0f, 3.0f };
        
        // Act
        var result = EmbeddingSimilarity.EuclideanDistance(a, b);
        
        // Assert
        Assert.Equal(0.0f, result, precision: 5);
    }
    
    [Fact]
    public void EuclideanDistance_KnownDistance_ReturnsCorrectValue()
    {
        // Arrange - distance should be sqrt((1-4)^2 + (2-6)^2) = sqrt(9+16) = 5
        var a = new float[] { 1.0f, 2.0f };
        var b = new float[] { 4.0f, 6.0f };
        
        // Act
        var result = EmbeddingSimilarity.EuclideanDistance(a, b);
        
        // Assert
        Assert.Equal(5.0f, result, precision: 5);
    }
    
    [Fact]
    public void DotProduct_KnownValue_ReturnsCorrectValue()
    {
        // Arrange - dot = 1*4 + 2*5 + 3*6 = 4 + 10 + 18 = 32
        var a = new float[] { 1.0f, 2.0f, 3.0f };
        var b = new float[] { 4.0f, 5.0f, 6.0f };
        
        // Act
        var result = EmbeddingSimilarity.DotProduct(a, b);
        
        // Assert
        Assert.Equal(32.0f, result, precision: 5);
    }
    
    [Fact]
    public void ComputeSimilarities_ReturnsSimilaritiesInOrder()
    {
        // Arrange
        ReadOnlyMemory<float> query = new float[] { 1.0f, 0.0f };
        var candidates = new List<ReadOnlyMemory<float>>
        {
            new float[] { 1.0f, 0.0f },      // Identical - should be 1.0
            new float[] { 0.0f, 1.0f },      // Orthogonal - should be 0.0
            new float[] { 0.7071f, 0.7071f } // 45 degrees - should be ~0.707
        };
        
        // Act
        var results = EmbeddingSimilarity.ComputeSimilarities(query, candidates);
        
        // Assert
        Assert.Equal(3, results.Length);
        Assert.Equal(1.0f, results[0], precision: 3);
        Assert.Equal(0.0f, results[1], precision: 3);
        Assert.True(results[2] > 0.70f && results[2] < 0.72f);
    }
    
    [Fact]
    public void TopK_ReturnsTopKBySimlarity()
    {
        // Arrange
        ReadOnlyMemory<float> query = new float[] { 1.0f, 0.0f };
        var candidates = new List<(string Item, ReadOnlyMemory<float> Embedding)>
        {
            ("A", new float[] { 1.0f, 0.0f }),      // Most similar
            ("B", new float[] { 0.0f, 1.0f }),      // Least similar  
            ("C", new float[] { 0.7071f, 0.7071f }) // Middle
        };
        
        // Act
        var results = EmbeddingSimilarity.TopK(query, candidates, 2).ToList();
        
        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].Item);
        Assert.Equal("C", results[1].Item);
        Assert.True(results[0].Similarity > results[1].Similarity);
    }
}
