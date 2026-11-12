// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Comparison;
using AgentEval.Tests.Core;
using AgentEval.Tracing;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using AgentEval.Results.Integrity;
using Microsoft.Extensions.AI;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentEval.Tests.MAF;

public sealed class MAFEvaluationHarnessCachedCostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEvaluation_ResponseOnlyModelIdentity_PricesReportedUsage(bool streaming)
    {
        var agent = new PricedAgent();
        var testCase = new TestCase { Name = "priced", Input = "hello" };
        var options = new EvaluationOptions { EvaluateResponse = false, Verbose = false };
        var harness = new MAFEvaluationHarness(verbose: false);
        var result = streaming
            ? await harness.RunEvaluationStreamingAsync(agent, testCase, options: options)
            : await harness.RunEvaluationAsync(agent, testCase, options);

        Assert.Equal("gpt-4o-mini", result.Performance!.ModelUsed);
        Assert.Equal(0.00075m, result.Performance.EstimatedCost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEvaluation_ProviderUsageToSealedArtifact_PreservesDiscountAndProvenance(bool streaming)
    {
        var folder = Path.Combine(Path.GetTempPath(), "agenteval-cost-" + Guid.NewGuid().ToString("N"));
        try
        {
            var agent = new ChatClientAgentAdapter(new TokenUsageMappingTests.UsageChatClient(new UsageDetails
            {
                InputTokenCount = 10000, OutputTokenCount = 1000, CachedInputTokenCount = 8000
            }));
            var options = new EvaluationOptions
            {
                EvaluateResponse = false, Verbose = false,
                PricingResolver = _ => new(0.001m, 0.002m, 0.0001m) { Source = "test-contract-v1" }
            };
            var start = DateTimeOffset.UtcNow;
            var result = await Run(agent, streaming, options);
            Assert.Equal(0.0048m, result.Performance!.EstimatedCost);
            Assert.Equal(TokenCostStatus.CacheAdjusted, result.Performance.CostEstimate!.Status);
            var (directory, _) = AefEvalWriter.WriteTrials(folder, [[result]], ["case"], 1,
                new AefEvalRunOptions
                {
                    SubjectRef = "agent:cost-fixture", TargetMode = AefTargetMode.Scripted,
                    DatasetName = "cost-fixture", DatasetBytes = "{\"id\":\"case\",\"input\":\"hello\"}\n"u8.ToArray(),
                    ContentCapture = AefContentCapture.Off
                }, start, DateTimeOffset.UtcNow);
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
            var trial = File.ReadLines(Path.Combine(directory, "results.ndjson"))
                .Select(line => JsonNode.Parse(line)!).Single(line => line["trial"] is not null);
            var evidence = trial["ext"]!["agenteval.costEstimate"]!;
            Assert.Equal(0.0048m, evidence["amount"]!.GetValue<decimal>());
            Assert.Equal("cacheAdjusted", evidence["status"]!.GetValue<string>());
            Assert.Equal(8000, evidence["cachedInputTokens"]!.GetValue<int>());
            Assert.Equal("response", evidence["modelSource"]!.GetValue<string>());
            Assert.Equal("test-contract-v1", evidence["pricing"]!["source"]!.GetValue<string>());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(null, "gpt-4o-mini", "response", true)]
    [InlineData("gpt-4o", "gpt-4o-mini", "override", true)]
    [InlineData("", "gpt-4o-mini", "override", false)]
    [InlineData(null, "unknown-model", "response", false)]
    [InlineData(null, "  ", "configured", true)]
    public async Task RunEvaluation_IdentityPrecedence_DoesNotFallbackFromUnknownResponse(
        string? modelOverride, string? returned, string source, bool priced)
    {
        foreach (var streaming in new[] { false, true })
        {
            var result = await Run(new ConfiguredAgent(returned), streaming,
                new() { EvaluateResponse = false, ModelName = modelOverride, Verbose = false });
            Assert.Equal(source, result.Performance!.CostEstimate!.ModelSource);
            Assert.Equal(priced, result.Performance.EstimatedCost.HasValue);
            Assert.Equal(modelOverride ?? (!string.IsNullOrWhiteSpace(returned) ? returned : "gpt-4o"), result.Performance.ModelUsed);
        }
    }

    [Fact]
    public async Task RunEvaluation_ConcurrentRateOverrides_AreIsolated()
    {
        var before = ModelPricing.GetTokenPricing("gpt-4o-mini");
        var tasks = Enumerable.Range(1, 12).Select(rate => Run(new PricedAgent(), false,
            new() { EvaluateResponse = false, Verbose = false, PricingResolver = _ => new(rate, rate) }));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (decimal?)(2 * i)), results.Select(r => r.Performance!.EstimatedCost));
        Assert.Equal(before, ModelPricing.GetTokenPricing("gpt-4o-mini"));
        var unpriced = await Run(new PricedAgent(), false,
            new() { EvaluateResponse = false, PricingResolver = _ => null });
        Assert.Null(unpriced.Performance!.EstimatedCost);
    }

    private static Task<TestResult> Run(IStreamableAgent agent, bool streaming, EvaluationOptions options)
    {
        var harness = new MAFEvaluationHarness(verbose: false);
        var test = new TestCase { Name = "cost", Input = "hello" };
        return streaming ? harness.RunEvaluationStreamingAsync(agent, test, options: options)
            : harness.RunEvaluationAsync(agent, test, options);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("absent")]
    [InlineData("missing-input")]
    [InlineData("missing-output")]
    [InlineData("negative")]
    [InlineData("overflow")]
    public async Task RunEvaluation_StreamingMixedModels_RequiresExplicitPricingAssumption(string usageMode)
    {
        var adapter = new ChatClientAgentAdapter(new MixedModelClient(usageMode), chatOptions: new() { ModelId = "gpt-4o" });
        var result = await Run(adapter, true, new() { EvaluateResponse = false });
        Assert.Null(result.Performance!.EstimatedCost);
        Assert.Equal("mixed", result.Performance.CostEstimate!.ModelSource);
        Assert.Null(result.Performance.ModelUsed);
        Assert.Equal(usageMode != "complete", result.Performance.CostEstimate.UsageEstimated);
        Assert.True(result.Performance.PromptTokens > 0);
        Assert.True(result.Performance.CompletionTokens > 0);
        var explicitPrice = await Run(adapter, true, new() { EvaluateResponse = false, ModelName = "gpt-4o" });
        Assert.NotNull(explicitPrice.Performance!.EstimatedCost);
        Assert.Equal("override", explicitPrice.Performance.CostEstimate!.ModelSource);
    }

    [Fact]
    public async Task RunEvaluation_StreamingSingleModelWithoutUsage_StillEstimatesCost()
    {
        var adapter = new ChatClientAgentAdapter(new MixedModelClient("absent", "GPT-4O"));
        var result = await Run(adapter, true, new() { EvaluateResponse = false });
        Assert.NotNull(result.Performance!.EstimatedCost);
        Assert.True(result.Performance.CostEstimate!.UsageEstimated);
        Assert.Equal("response", result.Performance.CostEstimate.ModelSource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunEvaluation_MixedStreamThroughTraceAndArtifact_RemainsUnpriced(bool streamingReplay)
    {
        var folder = Path.Combine(Path.GetTempPath(), "agenteval-mixed-cost-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var recorder = new TraceRecordingAgent(
                new ChatClientAgentAdapter(new MixedModelClient("absent")), "mixed",
                new() { ModelId = "gpt-4o" });
            await foreach (var _ in recorder.InvokeStreamingAsync("hello")) { }
            var trace = await TraceSerializer.DeserializeFromStringAsync(await TraceSerializer.SerializeToStringAsync(recorder.Trace));
            var entry = Assert.Single(trace.Entries, e => e.Type == TraceEntryType.Response);
            Assert.True(entry.HasMixedModelUsage);
            Assert.Null(entry.TokenUsage);
            var replay = await new TraceReplayingAgent(trace).InvokeAsync("hello");
            Assert.True(replay.HasMixedModelUsage);
            Assert.Null(replay.TokenUsage);

            var resolverCalls = 0;
            var start = DateTimeOffset.UtcNow;
            var result = await Run(new TraceReplayingAgent(trace), streamingReplay, new()
            {
                EvaluateResponse = false,
                PricingResolver = _ => { resolverCalls++; return new(1, 1); }
            });
            Assert.Equal(0, resolverCalls);
            Assert.Null(result.Performance!.EstimatedCost);
            Assert.Equal("mixed", result.Performance.CostEstimate!.ModelSource);
            var (directory, _) = AefEvalWriter.WriteTrials(folder, [[result]], ["case"], 1,
                new AefEvalRunOptions
                {
                    SubjectRef = "agent:mixed-fixture", TargetMode = AefTargetMode.Scripted,
                    DatasetName = "mixed-fixture", DatasetBytes = "{\"id\":\"case\",\"input\":\"hello\"}\n"u8.ToArray(),
                    ContentCapture = AefContentCapture.Off
                }, start, DateTimeOffset.UtcNow);
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
            var trial = File.ReadLines(Path.Combine(directory, "results.ndjson"))
                .Select(line => JsonNode.Parse(line)!).Single(line => line["trial"] is not null);
            var evidence = trial["ext"]!["agenteval.costEstimate"]!;
            Assert.Null(evidence["amount"]);
            Assert.Null(evidence["modelId"]);
            Assert.Equal("unpriced", evidence["status"]!.GetValue<string>());
            Assert.Equal("mixed", evidence["modelSource"]!.GetValue<string>());
            Assert.True(evidence["usageEstimated"]!.GetValue<bool>());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class MixedModelClient(string usageMode, string secondModel = "gpt-4o-mini") : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var model in new[] { "gpt-4o", secondModel })
            {
                var contents = new List<AIContent> { new TextContent("hello") };
                if (usageMode != "absent")
                {
                    contents.Add(new UsageContent(new()
                    {
                        InputTokenCount = usageMode switch
                        {
                            "missing-input" => null,
                            "negative" => -1,
                            "overflow" => model == "gpt-4o" ? long.MaxValue : 1,
                            _ => 100
                        },
                        OutputTokenCount = usageMode == "missing-output" ? null : 10,
                        CachedInputTokenCount = 0
                    }));
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, contents) { ModelId = model };
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ConfiguredAgent(string? returned) : PricedAgent(returned), IModelIdentifiable
    {
        public string ModelId => "gpt-4o";
        public string ModelDisplayName => Name;
    }

    private class PricedAgent(string? model = "gpt-4o-mini") : IStreamableAgent
    {
        public string Name => "display name, not a pricing identity";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse
            {
                Text = "hello", ModelId = model,
                TokenUsage = new TokenUsage { PromptTokens = 1000, CompletionTokens = 1000 }
            });

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseChunk
            {
                Text = "hello", IsComplete = true, ModelId = model,
                Usage = new TokenUsage { PromptTokens = 1000, CompletionTokens = 1000 }
            };
            await Task.CompletedTask;
        }
    }
}
