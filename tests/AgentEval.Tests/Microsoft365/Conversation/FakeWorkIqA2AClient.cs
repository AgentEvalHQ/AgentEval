// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365.Capture;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;

namespace AgentEval.Tests.Microsoft365.Conversation;

/// <summary>
/// A scripted <see cref="IWorkIqA2AClient"/> for conversation tests that need control the HTTP fake cannot give:
/// a send that blocks until released (concurrency, reset-while-active, abandonment, cancellation and deadline
/// cases) and counted GetTask/CancelTask calls. Discovery returns the default fixture card.
/// </summary>
internal sealed class FakeWorkIqA2AClient : IWorkIqA2AClient
{
    public static readonly DateTimeOffset At = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    private int _discoverCalls;
    private int _getTaskCalls;
    private int _cancelCalls;

    public RawAgentCard Card { get; set; } = DefaultCard();

    public Exception? DiscoverFailure { get; set; }

    public Func<SendMessageRequest, CancellationToken, Task<WorkIqRpcResult<SendMessageResponse>>> OnSend { get; set; } =
        (_, _) => Task.FromResult(DirectMessageReply("ok"));

    public Func<string, CancellationToken, Task<WorkIqRpcResult<AgentTask>>>? OnGetTask { get; set; }

    public Func<string, CancellationToken, Task<WorkIqRpcResult<AgentTask>>>? OnCancelTask { get; set; }

    /// <summary>When set, <see cref="SendAsync"/> waits for it (honouring the token) before replying.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<SendMessageRequest> Sends { get; } = [];

    public int DiscoverCalls => Volatile.Read(ref _discoverCalls);

    public int GetTaskCalls => Volatile.Read(ref _getTaskCalls);

    public int CancelCalls => Volatile.Read(ref _cancelCalls);

    /// <summary>
    /// When set, <see cref="DiscoverAsync"/> waits for it before replying, as a discovery that waits for a sign-in does.
    /// It honours the token unless <see cref="DiscoverIgnoresToken"/> is set (a sign-in that finishes anyway).
    /// </summary>
    public TaskCompletionSource? DiscoverGate { get; set; }

    /// <summary>Whether a gated discovery ignores the token and completes once the gate opens.</summary>
    public bool DiscoverIgnoresToken { get; set; }

    public async Task<RawAgentCard> DiscoverAsync(CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        Interlocked.Increment(ref _discoverCalls);
        cancellationToken.ThrowIfCancellationRequested();
        if (DiscoverGate is { } gate)
        {
            await (DiscoverIgnoresToken ? gate.Task : gate.Task.WaitAsync(cancellationToken));
        }

        return DiscoverFailure is null ? Card : throw DiscoverFailure;
    }

    public async Task<WorkIqRpcResult<SendMessageResponse>> SendAsync(SendMessageRequest request, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        Sends.Add(request);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return await OnSend(request, cancellationToken);
    }

    public IAsyncEnumerable<WorkIqStreamFrame> StreamAsync(SendMessageRequest request, CancellationToken cancellationToken, WorkIqCallContext? call = null) =>
        throw new NotImplementedException("Streaming is not scripted by this fake.");

    public Task<WorkIqRpcResult<AgentTask>> GetTaskAsync(string taskId, int? historyLength, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        Interlocked.Increment(ref _getTaskCalls);
        return OnGetTask?.Invoke(taskId, cancellationToken) ?? throw new InvalidOperationException("No GetTask reply is scripted.");
    }

    public Task<WorkIqRpcResult<AgentTask>> CancelTaskAsync(string taskId, CancellationToken cancellationToken, WorkIqCallContext? call = null)
    {
        Interlocked.Increment(ref _cancelCalls);
        return OnCancelTask?.Invoke(taskId, cancellationToken) ?? throw new InvalidOperationException("No CancelTask reply is scripted.");
    }

    public IAsyncEnumerable<WorkIqStreamFrame> SubscribeAsync(string taskId, CancellationToken cancellationToken, WorkIqCallContext? call = null) =>
        throw new NotImplementedException("Subscription is not scripted by this fake.");

    /// <summary>The default two-interface fixture card as a discovered card.</summary>
    public static RawAgentCard DefaultCard()
    {
        var json = FixtureLoader.Text("cards/default-card-two-interfaces.json");
        var card = JsonSerializer.Deserialize<AgentCard>(json, A2AJsonUtilities.DefaultOptions)!;
        return new RawAgentCard(
            card,
            json,
            WorkIqA2AClient.Sha256Hex(Encoding.UTF8.GetBytes(json)),
            new Uri("https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json"),
            new Uri("https://workiq.svc.cloud.microsoft/a2a/"),
            "1.0",
            At);
    }

    /// <summary>A metadata-only capture reference.</summary>
    public static RawCaptureRef Capture(string id = "send-0001") =>
        new(id, CaptureState.NotRetained, RawCaptureSink.PolicyId, null, null, null);

    /// <summary>A direct-message reply with one text part.</summary>
    public static WorkIqRpcResult<SendMessageResponse> DirectMessageReply(string text, string? contextId = "ctx-fake-1") =>
        new(
            new SendMessageResponse
            {
                Message = new Message { MessageId = "msg-agent-fake-1", Role = Role.Agent, Parts = [Part.FromText(text)], ContextId = contextId },
            },
            Capture(),
            "req-fake-1",
            200,
            At);

    /// <summary>A <c>SendMessage</c> reply parsed from an envelope fixture.</summary>
    public static WorkIqRpcResult<SendMessageResponse> SendReply(string fixture, string captureId = "send-0001")
    {
        var envelope = JsonSerializer.Deserialize<JsonRpcResponse>(FixtureLoader.Text(fixture), A2AJsonUtilities.DefaultOptions)!;
        var value = envelope.Result!.Deserialize<SendMessageResponse>(A2AJsonUtilities.DefaultOptions)!;
        return new WorkIqRpcResult<SendMessageResponse>(value, Capture(captureId), "req-" + captureId, 200, At);
    }

    /// <summary>A <c>GetTask</c>/<c>CancelTask</c> reply parsed from an envelope fixture (task directly under <c>result</c>).</summary>
    public static WorkIqRpcResult<AgentTask> TaskReply(string fixture, string captureId = "gettask-0001")
    {
        var envelope = JsonSerializer.Deserialize<JsonRpcResponse>(FixtureLoader.Text(fixture), A2AJsonUtilities.DefaultOptions)!;
        var value = envelope.Result!.Deserialize<AgentTask>(A2AJsonUtilities.DefaultOptions)!;
        return new WorkIqRpcResult<AgentTask>(value, Capture(captureId), "req-" + captureId, 200, At);
    }
}
