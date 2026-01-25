// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class DelegateAgentFactoryTests
{
    [Fact]
    public void Constructor_WithNullModelId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new DelegateAgentFactory(null!, "name", () => new FakeAgent()));
    }
    
    [Fact]
    public void Constructor_WithNullModelName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new DelegateAgentFactory("id", null!, () => new FakeAgent()));
    }
    
    [Fact]
    public void Constructor_WithNullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => 
            new DelegateAgentFactory("id", "name", null!));
    }
    
    [Fact]
    public void Properties_ReturnExpectedValues()
    {
        var config = new ModelConfiguration { DeploymentName = "deployment" };
        var factory = new DelegateAgentFactory("gpt-4o", "GPT-4o", () => new FakeAgent(), config);
        
        Assert.Equal("gpt-4o", factory.ModelId);
        Assert.Equal("GPT-4o", factory.ModelName);
        Assert.Equal(config, factory.Configuration);
    }
    
    [Fact]
    public void CreateAgent_ReturnsNewInstanceEachTime()
    {
        int callCount = 0;
        var factory = new DelegateAgentFactory(
            "gpt-4o", 
            "GPT-4o",
            () => { callCount++; return new FakeAgent(); });
        
        var agent1 = factory.CreateAgent();
        var agent2 = factory.CreateAgent();
        
        Assert.Equal(2, callCount);
        Assert.NotSame(agent1, agent2);
    }
    
    [Fact]
    public void Configuration_CanBeNull()
    {
        var factory = new DelegateAgentFactory("id", "name", () => new FakeAgent());
        Assert.Null(factory.Configuration);
    }
    
    private class FakeAgent : IEvaluableAgent
    {
        public string Name => "FakeAgent";
        
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse { Text = "fake response" });
        }
    }
}

public class ModelConfigurationTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var config = new ModelConfiguration
        {
            DeploymentName = "gpt-4o-2024-08-06",
            Temperature = 0.7,
            MaxTokens = 1000,
            Seed = 42
        };
        
        Assert.Equal("gpt-4o-2024-08-06", config.DeploymentName);
        Assert.Equal(0.7, config.Temperature);
        Assert.Equal(1000, config.MaxTokens);
        Assert.Equal(42, config.Seed);
    }
    
    [Fact]
    public void AdditionalProperties_CanBeSet()
    {
        var additionalProps = new Dictionary<string, object>
        {
            { "top_p", 0.9 },
            { "frequency_penalty", 0.5 }
        };
        
        var config = new ModelConfiguration
        {
            DeploymentName = "gpt-4o",
            AdditionalProperties = additionalProps
        };
        
        Assert.NotNull(config.AdditionalProperties);
        Assert.Equal(0.9, config.AdditionalProperties["top_p"]);
    }
    
    [Fact]
    public void DefaultValues_AreNull()
    {
        var config = new ModelConfiguration { DeploymentName = "deployment" };
        
        Assert.Null(config.Temperature);
        Assert.Null(config.MaxTokens);
        Assert.Null(config.Seed);
        Assert.Null(config.AdditionalProperties);
    }
}
