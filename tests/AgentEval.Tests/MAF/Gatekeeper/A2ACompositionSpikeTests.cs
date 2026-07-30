// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using A2A;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Phase 4, Task 4.1 — proves the real MAF <see cref="A2AAgent"/> can be wrapped by Gatekeeper at the
/// run boundary. A2A tools execute remotely and are intentionally outside the local function middleware.
/// </summary>
public sealed class A2ACompositionSpikeTests
{
    [Fact]
    public async Task RunPreBlock_PreventsRemoteA2ACallAndRecordsEvidence()
    {
        var client = new FakeA2AClient("remote reply");
        var sink = new CapturingSink();
        var agent = BuildGatedAgent(
            client,
            sink,
            pre: [new KeywordGate("unsafe")]);

        var response = await agent.RunAsync("unsafe request");

        Assert.Equal(0, client.SendCount);
        Assert.True(GatekeeperRefusalContract.TryParse(
            response.Text,
            out var referenceId,
            out var disposition,
            out var attempts));
        Assert.Equal(RefusalDisposition.Denied, disposition);
        Assert.Null(attempts);

        var evidence = Assert.Single(sink.Records, item => item.Stage == "run-pre");
        Assert.Equal("run-pre", evidence.Stage);
        Assert.Equal("Block", evidence.Action);
        Assert.Equal("A2ABoundaryPolicy", evidence.Policy);
        Assert.Equal(referenceId, evidence.ReferenceId);
        Assert.DoesNotContain("unsafe request", string.Join("|", evidence.ToMetadata().Values));
    }

    [Fact]
    public async Task RunPostBlock_ReplacesRemoteReplyAfterExactlyOneA2ACall()
    {
        var client = new FakeA2AClient("remote unsafe reply");
        var sink = new CapturingSink();
        var agent = BuildGatedAgent(
            client,
            sink,
            post: [new KeywordGate("unsafe")]);

        var response = await agent.RunAsync("safe request");

        Assert.Equal(1, client.SendCount);
        Assert.DoesNotContain("remote unsafe reply", response.Text, StringComparison.Ordinal);
        Assert.True(GatekeeperRefusalContract.TryParse(response.Text, out var referenceId, out _, out _));

        var evidence = Assert.Single(sink.Records, item => item.Stage == "run-post");
        Assert.Equal("run-post", evidence.Stage);
        Assert.Equal("Block", evidence.Action);
        Assert.Equal(referenceId, evidence.ReferenceId);
        Assert.DoesNotContain("remote unsafe reply", string.Join("|", evidence.ToMetadata().Values));
    }

    [Fact]
    public async Task RunAllow_PassesRemoteReplyThroughWithoutGateEvidence()
    {
        var client = new FakeA2AClient("remote safe reply");
        var sink = new CapturingSink();
        var agent = BuildGatedAgent(
            client,
            sink,
            pre: [new KeywordGate("unsafe")],
            post: [new KeywordGate("unsafe")]);

        var response = await agent.RunAsync("safe request");

        Assert.Equal(1, client.SendCount);
        Assert.Equal("remote safe reply", response.Text);
        Assert.DoesNotContain(sink.Records, item => item.Action == "Block");
        Assert.Single(sink.Records, item => item.Stage == "receipt" && item.Action == "Receipt");
    }

    [Fact]
    public async Task StreamingRunPreBlock_PreventsRemoteA2AStream()
    {
        var client = new FakeA2AClient("remote stream should not run");
        var sink = new CapturingSink();
        var agent = BuildGatedAgent(client, sink, pre: [new KeywordGate("unsafe")]);

        var chunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync("unsafe request"))
        {
            chunks.Add(update.Text);
        }

        Assert.Equal(0, client.SendCount);
        Assert.True(GatekeeperRefusalContract.TryParse(string.Concat(chunks), out _, out _, out _));
        Assert.Single(sink.Records, item => item.Stage == "run-pre" && item.Action == "Block");
    }

    [Fact]
    public async Task StreamingRunAllow_PassesRemoteA2AStreamThrough()
    {
        var client = new FakeA2AClient("remote streamed reply");
        var sink = new CapturingSink();
        var agent = BuildGatedAgent(client, sink, pre: [new KeywordGate("unsafe")]);

        var chunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync("safe request"))
        {
            chunks.Add(update.Text);
        }

        Assert.Equal(1, client.SendCount);
        Assert.Equal("remote streamed reply", string.Concat(chunks));
        Assert.DoesNotContain(sink.Records, item => item.Action == "Block");
        Assert.Single(sink.Records, item => item.Stage == "receipt" && item.Action == "Receipt");
    }

    private static AIAgent BuildGatedAgent(
        IA2AClient client,
        IGateEvidenceSink sink,
        IReadOnlyList<IChatGate>? pre = null,
        IReadOnlyList<IChatGate>? post = null)
        => new A2AAgent(client, "remote-id", "remote-agent", "Test-only remote agent", loggerFactory: null)
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                foreach (var gate in pre ?? [])
                {
                    options.AddPreGate(gate);
                }

                foreach (var gate in post ?? [])
                {
                    options.AddPostGate(gate);
                }

                options.EvidenceSink = sink;
            })
            .Build();

    private sealed class KeywordGate(string keyword) : IChatGate
    {
        public string PolicyName => "A2ABoundaryPolicy";

        public ValueTask<GateVerdict> InspectAsync(
            string text,
            CancellationToken cancellationToken = default)
            => new(text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                ? GateVerdict.Block(PolicyName, "A2A boundary content rejected")
                : GateVerdict.Allow(PolicyName));
    }

    private sealed class CapturingSink : IGateEvidenceSink
    {
        public List<GateEvidence> Records { get; } = [];

        public void Record(GateEvidence evidence, int sequence) => Records.Add(evidence);
    }

    internal sealed class FakeA2AClient(string reply) : IA2AClient
    {
        public int SendCount { get; private set; }

        public Task<SendMessageResponse> SendMessageAsync(
            SendMessageRequest request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new SendMessageResponse
            {
                Message = AgentMessage(reply),
            });
        }

        public async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(
            SendMessageRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SendCount++;
            yield return new StreamResponse { Message = AgentMessage(reply) };
            await Task.CompletedTask;
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ListTasksResponse> ListTasksAsync(
            ListTasksRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(
            SubscribeToTaskRequest request,
            CancellationToken cancellationToken)
            => EmptyStream();

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
            CreateTaskPushNotificationConfigRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
            GetTaskPushNotificationConfigRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(
            ListTaskPushNotificationConfigRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteTaskPushNotificationConfigAsync(
            DeleteTaskPushNotificationConfigRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AgentCard> GetExtendedAgentCardAsync(
            GetExtendedAgentCardRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        private static A2A.Message AgentMessage(string text)
            => new()
            {
                Role = Role.Agent,
                MessageId = "response-1",
                Parts = [new Part { Text = text }],
            };

        private static async IAsyncEnumerable<StreamResponse> EmptyStream()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
