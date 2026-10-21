// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

/// <summary>
/// T63 / T13-8 on the MAF side: <see cref="MAFAgentAdapter"/> (both paths) and <see cref="MAFWorkflowEventBridge"/>
/// reported a usage with a missing count as 0, and the harness then marked those counts as real
/// (<c>TokensAreEstimated = false</c>) and priced them. Usage is now null unless both counts were reported.
/// </summary>
public sealed class TokenUsageMappingMafTests
{
    private static UsageDetails InputOnly => new() { InputTokenCount = 120 };
    private static UsageDetails Full => new() { InputTokenCount = 10, OutputTokenCount = 5 };

    [Fact]
    public async Task MAFAgentAdapter_PartialUsage_IsNotReported()
    {
        var adapter = new MAFAgentAdapter(new FakeAIAgent("agent", "Hello.", InputOnly));

        var response = await adapter.InvokeAsync("hi");

        Assert.Null(response.TokenUsage); // was {120, 0}
    }

    [Fact]
    public async Task MAFAgentAdapter_Streaming_PartialUsage_IsNotReported()
    {
        var adapter = new MAFAgentAdapter(new UsageStreamingAgent("agent", "Hello.", InputOnly));

        AgentResponseChunk? last = null;
        await foreach (var chunk in adapter.InvokeStreamingAsync("hi"))
            last = chunk;

        Assert.True(last!.IsComplete);
        Assert.Null(last.Usage);
    }

    [Fact]
    public async Task MAFAgentAdapter_FullUsage_MapsBothCounts_OnBothPaths()
    {
        var response = await new MAFAgentAdapter(new FakeAIAgent("agent", "Hello.", Full)).InvokeAsync("hi");
        AgentResponseChunk? last = null;
        await foreach (var chunk in new MAFAgentAdapter(new UsageStreamingAgent("agent", "Hello.", Full)).InvokeStreamingAsync("hi"))
            last = chunk;

        Assert.Equal((10, 5), (response.TokenUsage!.PromptTokens, response.TokenUsage.CompletionTokens));
        Assert.Equal((10, 5), (last!.Usage!.PromptTokens, last.Usage.CompletionTokens));
    }

    [Fact]
    public async Task WorkflowBridge_PartialUsage_IsNotReported_FullUsageIs()
    {
        static async Task<ExecutorAgentResponseEvent> RunAsync(UsageDetails usage)
        {
            var binding = new UsageStreamingAgent("agent", "Hello.", usage)
                .BindAsExecutor(new MAFWorkflows.AIAgentHostOptions { EmitAgentResponseEvents = true });
            var workflow = new MAFWorkflows.WorkflowBuilder(binding).WithOutputFrom(binding).Build(validateOrphans: false);
            var events = new List<WorkflowEvent>();
            await foreach (var evt in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, "hi"))
                events.Add(evt);
            return Assert.Single(events.OfType<ExecutorAgentResponseEvent>());
        }

        var partial = await RunAsync(InputOnly);
        var full = await RunAsync(Full);

        Assert.Null(partial.Usage); // was {120, 0}
        Assert.Equal((10, 5), (full.Usage!.PromptTokens, full.Usage.CompletionTokens));
    }

    [Fact]
    public async Task Harness_PartialUsage_TakesTheEstimatedPath()
    {
        // The recheck's trigger: with {120, 0} the harness set TokensAreEstimated=false and priced 0 output tokens.
        var options = new EvaluationOptions { TrackPerformance = true, EvaluateResponse = false, Verbose = false, ModelName = "gpt-4o" };
        var testCase = new TestCase { Name = "partial usage", Input = "Say hello." };

        var result = await new MAFEvaluationHarness(verbose: false)
            .RunEvaluationAsync(new MAFAgentAdapter(new FakeAIAgent("agent", "Hello there, how are you today?", InputOnly)), testCase, options);

        Assert.True(result.Performance!.TokensAreEstimated);
        Assert.True(result.Performance.PromptTokens > 0);
        Assert.True(result.Performance.CompletionTokens > 0);
    }

    /// <summary>Streams a text update and then a <see cref="UsageContent"/> carrying the given usage.</summary>
    private sealed class UsageStreamingAgent(string name, string responseText, UsageDetails usage) : AIAgent
    {
        public override string? Name { get; } = name;
        protected override string? IdCore => Name;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => new(new FakeStreamableAgentSession());

        protected override Task<Microsoft.Agents.AI.AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.RunStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var messageId = Guid.NewGuid().ToString("N");
            var responseId = Guid.NewGuid().ToString("N");
            yield return new AgentResponseUpdate
            {
                AgentId = Id, AuthorName = Name, MessageId = messageId, ResponseId = responseId,
                Contents = [new TextContent(responseText)], Role = ChatRole.Assistant,
            };
            yield return new AgentResponseUpdate
            {
                AgentId = Id, AuthorName = Name, MessageId = messageId, ResponseId = responseId,
                Contents = [new UsageContent(usage)], Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop,
            };
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => new(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => new(new FakeStreamableAgentSession());
    }
}
