// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Adapters;
using AgentEval.Core;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for MicrosoftEvaluatorAdapter.
/// </summary>
public class MicrosoftEvaluatorAdapterTests
{
    [Fact]
    public void CreateFluencyEvaluator_ReturnsValidAdapter()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = MicrosoftEvaluatorAdapter.CreateFluencyEvaluator(fakeChatClient);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Equal("Fluency", adapter.Name);
        Assert.Contains("fluency", adapter.Description, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public void CreateCoherenceEvaluator_ReturnsValidAdapter()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = MicrosoftEvaluatorAdapter.CreateCoherenceEvaluator(fakeChatClient);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Equal("Coherence", adapter.Name);
        Assert.Contains("logical flow", adapter.Description, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public void CreateRelevanceEvaluator_ReturnsValidAdapter()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = MicrosoftEvaluatorAdapter.CreateRelevanceEvaluator(fakeChatClient);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Equal("Relevance", adapter.Name);
    }
    
    [Fact]
    public void CreateGroundednessEvaluator_ReturnsValidAdapter()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = MicrosoftEvaluatorAdapter.CreateGroundednessEvaluator(fakeChatClient);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Equal("Groundedness", adapter.Name);
    }
    
    [Fact]
    public void CreateEquivalenceEvaluator_ReturnsValidAdapter()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = MicrosoftEvaluatorAdapter.CreateEquivalenceEvaluator(fakeChatClient);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Equal("Equivalence", adapter.Name);
    }
    
    [Fact]
    public void CreateCompletenessEvaluator_ReturnsValidAdapter()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = MicrosoftEvaluatorAdapter.CreateCompletenessEvaluator(fakeChatClient);
        
        // Assert
        Assert.NotNull(adapter);
        Assert.Equal("Completeness", adapter.Name);
    }
    
    [Fact]
    public void CreateAllQualityEvaluators_Returns6Evaluators()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var evaluators = MicrosoftEvaluatorExtensions.CreateAllQualityEvaluators(fakeChatClient).ToList();
        
        // Assert
        Assert.Equal(6, evaluators.Count);
        Assert.Contains(evaluators, e => e.Name == "Fluency");
        Assert.Contains(evaluators, e => e.Name == "Coherence");
        Assert.Contains(evaluators, e => e.Name == "Relevance");
        Assert.Contains(evaluators, e => e.Name == "Groundedness");
        Assert.Contains(evaluators, e => e.Name == "Equivalence");
        Assert.Contains(evaluators, e => e.Name == "Completeness");
    }
    
    [Fact]
    public void Constructor_NullEvaluator_Throws()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient();
        
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new MicrosoftEvaluatorAdapter(null!, fakeChatClient));
    }
    
    [Fact]
    public void Constructor_NullChatClient_Throws()
    {
        // Arrange
        var evaluator = new Microsoft.Extensions.AI.Evaluation.Quality.FluencyEvaluator();
        
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new MicrosoftEvaluatorAdapter(evaluator, null!));
    }
    
    [Fact]
    public void Constructor_CustomNameAndDescription_Applied()
    {
        // Arrange
        var evaluator = new Microsoft.Extensions.AI.Evaluation.Quality.FluencyEvaluator();
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = new MicrosoftEvaluatorAdapter(
            evaluator, 
            fakeChatClient, 
            name: "CustomName", 
            description: "Custom description");
        
        // Assert
        Assert.Equal("CustomName", adapter.Name);
        Assert.Equal("Custom description", adapter.Description);
    }
    
    [Fact]
    public void Constructor_DefaultName_DerivedFromEvaluatorType()
    {
        // Arrange
        var evaluator = new Microsoft.Extensions.AI.Evaluation.Quality.FluencyEvaluator();
        var fakeChatClient = new FakeChatClient();
        
        // Act
        var adapter = new MicrosoftEvaluatorAdapter(evaluator, fakeChatClient);
        
        // Assert
        Assert.Equal("Fluency", adapter.Name); // "Evaluator" suffix removed
    }
}
