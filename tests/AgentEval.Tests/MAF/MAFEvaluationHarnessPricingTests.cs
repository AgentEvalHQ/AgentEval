// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Which model a run is priced at. The harness priced every run at <see cref="EvaluationOptions.ModelName"/> only, and
/// <see cref="StochasticRunner"/> shares one options object across every model of a comparison: a comparison either
/// priced every model at one model's rate (comparing token counts, not prices) or priced nothing. With no ModelName the
/// harness now prices a run at the model the agent names (<see cref="IModelIdentifiable"/>).
/// </summary>
public sealed class MAFEvaluationHarnessPricingTests
{
    private static readonly TestCase Case = new() { Name = "priced run", Input = "Say hello." };

    private static readonly EvaluationOptions SharedOptions = new()
    {
        TrackPerformance = true,
        EvaluateResponse = false,
        Verbose = false,
    };

    // 1,000 input and 1,000 output tokens: the cost is the model's per-1K input rate plus its per-1K output rate.
    private static IEvaluableAgent Agent(string? modelId)
    {
        var inner = new FakeAIAgent("agent", "Hello.", new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 1000 });
        return modelId is null ? new MAFAgentAdapter(inner) : new MAFIdentifiableAgentAdapter(inner, modelId, modelId);
    }

    [Fact]
    public async Task WithoutAModelName_ARunIsPricedAtTheModelTheAgentNames()
    {
        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(Agent("gpt-4o-mini"), Case, SharedOptions);

        Assert.Equal("gpt-4o-mini", result.Performance!.ModelUsed);
        Assert.Equal(0.00015m + 0.0006m, result.Performance.EstimatedCost);
    }

    [Fact]
    public async Task AModelNameInTheOptions_StillWins()
    {
        var options = new EvaluationOptions { TrackPerformance = true, EvaluateResponse = false, Verbose = false, ModelName = "gpt-4o" };

        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(Agent("gpt-4o-mini"), Case, options);

        Assert.Equal("gpt-4o", result.Performance!.ModelUsed);
        Assert.Equal(0.0025m + 0.01m, result.Performance.EstimatedCost);
    }

    [Fact]
    public async Task AnAgentThatNamesNoModel_IsNotPriced()
    {
        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(Agent(modelId: null), Case, SharedOptions);

        Assert.Null(result.Performance!.ModelUsed);
        Assert.Null(result.Performance.EstimatedCost);
    }

    [Fact]
    public async Task AComparisonWithSharedOptions_PricesEachModelAtItsOwnRate()
    {
        var runner = new StochasticRunner(new MAFEvaluationHarness(verbose: false), statisticsCalculator: null, SharedOptions);
        var factories = new IAgentFactory[]
        {
            new DelegateAgentFactory("gpt-4o", "GPT-4o", () => Agent("gpt-4o")),
            new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => Agent("gpt-4o-mini")),
        };

        var result = await new ModelComparer(runner).CompareModelsAsync(factories, Case,
            new ModelComparisonOptions(RunsPerModel: 3, EnableCostAnalysis: true, EnableStatistics: false));

        Assert.Null(result.CostNotMeasuredReason);
        Assert.Equal(0.0025m + 0.01m, result.ModelResults.Single(m => m.ModelId == "gpt-4o").AverageCost);
        Assert.Equal(0.00015m + 0.0006m, result.ModelResults.Single(m => m.ModelId == "gpt-4o-mini").AverageCost);
        Assert.Equal(100.0, result.Ranking.Single(r => r.ModelId == "gpt-4o-mini").CostScore);
        Assert.Equal(0.0, result.Ranking.Single(r => r.ModelId == "gpt-4o").CostScore);
    }

    [Theory]
    [InlineData(null, "gpt-4o-mini", 0.00075)]
    [InlineData("gpt-4o", "gpt-4o", 0.0125)]
    public async Task AChatClientAgent_PricesItsRequestModelOrClientDefault(
        string? requestedModel, string expectedModel, double expectedCost)
    {
        using var client = new MetadataChatClient("gpt-4o-mini");
        var chatOptions = requestedModel is null ? null : new ChatOptions { ModelId = requestedModel };
        var agent = client.AsEvaluableAgent("Display label", chatOptions: chatOptions);

        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(agent, Case, SharedOptions);

        Assert.Equal(expectedModel, result.Performance!.ModelUsed);
        Assert.Equal((decimal)expectedCost, result.Performance.EstimatedCost);
        var identity = Assert.IsAssignableFrom<IModelIdentifiable>(agent);
        Assert.Equal(expectedModel, identity.ModelId);
        Assert.Equal("Display label", identity.ModelDisplayName);
        Assert.Equal(requestedModel, client.LastOptions?.ModelId);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("deployment-without-a-price", true)]
    public async Task AChatClientAgent_WithoutAPricedIdentity_DoesNotGuessFromItsName(
        string? modelId, bool supplyMetadata)
    {
        using var client = new MetadataChatClient(modelId, supplyMetadata);
        var agent = client.AsEvaluableAgent("gpt-4o");

        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(agent, Case, SharedOptions);

        Assert.Null(result.Performance!.EstimatedCost);
        Assert.NotEqual("gpt-4o", result.Performance.ModelUsed);
    }

    [Fact]
    public async Task AChatClientAgent_ExplicitEvaluationModel_StillWinsOverClientMetadata()
    {
        using var client = new MetadataChatClient("gpt-4o-mini");
        var agent = client.AsEvaluableAgent();
        var options = new EvaluationOptions
        {
            TrackPerformance = true, EvaluateResponse = false, Verbose = false, ModelName = "gpt-4o"
        };

        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(agent, Case, options);

        Assert.Equal("gpt-4o", result.Performance!.ModelUsed);
        Assert.Equal(0.0125m, result.Performance.EstimatedCost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AChatClientAgent_BlankRequestOverride_DoesNotFallBackToClientDefault(string requestedModel)
    {
        using var client = new MetadataChatClient("gpt-4o-mini");
        var agent = client.AsEvaluableAgent("gpt-4o", chatOptions: new ChatOptions { ModelId = requestedModel });

        var result = await new MAFEvaluationHarness(verbose: false).RunEvaluationAsync(agent, Case, SharedOptions);

        Assert.Null(result.Performance!.EstimatedCost);
        Assert.True(string.IsNullOrEmpty(result.Performance.ModelUsed));
        Assert.Equal(requestedModel, client.LastOptions?.ModelId);
    }

    [Fact]
    public async Task AComparisonUsingChatClients_PricesEachMetadataModelAtItsOwnRate()
    {
        var runner = new StochasticRunner(new MAFEvaluationHarness(verbose: false), statisticsCalculator: null, SharedOptions);
        var factories = new IAgentFactory[]
        {
            new DelegateAgentFactory("gpt-4o", "GPT-4o", () => new MetadataChatClient("gpt-4o").AsEvaluableAgent()),
            new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => new MetadataChatClient("gpt-4o-mini").AsEvaluableAgent()),
        };

        var result = await new ModelComparer(runner).CompareModelsAsync(factories, Case,
            new ModelComparisonOptions(RunsPerModel: 3, EnableCostAnalysis: true, EnableStatistics: false));

        Assert.Null(result.CostNotMeasuredReason);
        Assert.Equal(0.0125m, result.ModelResults.Single(m => m.ModelId == "gpt-4o").AverageCost);
        Assert.Equal(0.00075m, result.ModelResults.Single(m => m.ModelId == "gpt-4o-mini").AverageCost);
    }

    private sealed class MetadataChatClient(string? modelId, bool supplyMetadata = true) : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello."))
            {
                Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 1000 }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            supplyMetadata && serviceKey is null && serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("synthetic", defaultModelId: modelId) : null;

        public void Dispose() { }
    }
}
