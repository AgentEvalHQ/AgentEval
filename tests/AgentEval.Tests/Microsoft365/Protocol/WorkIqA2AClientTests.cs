// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Protocol;

/// <summary>
/// <see cref="WorkIqA2AClient"/> blocking operations over the real pipeline and <see cref="FakeWorkIqHandler"/>:
/// discovery + interface selection, send → task / message, GetTask / CancelTask shapes and errors, JSON-RPC
/// errors on HTTP 200, bounded retry for safe reads only, 0.3-shape rejection, transport classification, and
/// the trace events every operation emits.
/// </summary>
public sealed class WorkIqA2AClientTests
{
    private const string SubmittedTaskId = "task-7a1e3c5b-9d2f-4e8a-b1c6-3f5d7e9a0b21";
    private const string SharedContextId = "ctx-5e0c7a2b-91d4-4f8e-b6a3-0d2f8c1e7b55";

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

    private static SendMessageRequest NewSend(string text = "Summarize the status of Project Atlas and cite the sources.") =>
        new() { Message = new Message { MessageId = "msg-user-test-0001", Role = Role.User, Parts = [Part.FromText(text)] } };

    private static JsonObject Body(RecordedRequest request) => (JsonObject)JsonNode.Parse(request.BodyJson!)!;

    // ---------------------------------------------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Discover_DefaultCard_SelectsJsonRpc10InterfaceAndCachesTheCard()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();

        var card = await h.Client.DiscoverAsync(CancellationToken.None);
        var again = await h.Client.DiscoverAsync(CancellationToken.None);

        Assert.Same(card, again);
        Assert.Equal(1, h.Handler.CardRequestCount);
        Assert.Equal(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), card.SelectedEndpoint);
        Assert.Equal("1.0", card.SelectedProtocolVersion);
        Assert.Equal(new Uri("https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json"), card.CardUri);
        Assert.Equal(FixtureLoader.Text("cards/default-card-two-interfaces.json"), card.RawJson);
        Assert.Equal(WorkIqA2AClient.Sha256Hex(FixtureLoader.Bytes("cards/default-card-two-interfaces.json")), card.Sha256);
        Assert.Equal(WorkIqClientHarness.Start, card.FetchedAt);
        Assert.Equal("Work IQ", card.Card.Name);
        Assert.Null(h.Client.RoutingTenant);
        Assert.NotNull(h.Client.Client);
        Assert.Single(h.Events("discovery.card_fetched"));
        Assert.Single(h.Events("discovery.interface_selected"));
        Assert.Equal(0, h.Diagnostics.IssueCount);

        var get = Assert.Single(h.Handler.Requests);
        Assert.Equal(HttpMethod.Get, get.Method);
        Assert.Equal("1.0", get.Headers["A2A-Version"]);
        Assert.Equal("Bearer", get.AuthorizationScheme);
    }

    [Fact]
    public async Task Discover_UntrustedHostCard_RefusedBeforeAnyRpc()
    {
        using var h = WorkIqClientHarness.Create();
        h.Handler.OnCard("cards/card-untrusted-host.json").OnRpc("SendMessage", "envelopes/send-task-submitted.json");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));
        var send = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.UntrustedHost, ex.Failure.Category);
        Assert.Equal(WorkIqFailureCategory.UntrustedHost, send.Failure.Category);
        Assert.Equal(0, h.Handler.CountOf("SendMessage"));
        Assert.All(h.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.NotEmpty(h.Events("discovery.untrusted_host"));
        Assert.Contains(h.Issues.Issues, i => i.Category == WorkIqIssueCategory.Config && i.Code == "discovery.untrusted_host");
        Assert.DoesNotContain(h.Issues.Issues, i => i.Code.Contains("untrusted", StringComparison.Ordinal) && i.Category != WorkIqIssueCategory.Config);
        Assert.Throws<InvalidOperationException>(() => h.Client.Client);
    }

    [Fact]
    public async Task Discover_V03OnlyCard_ProtocolVersionMismatch()
    {
        using var h = WorkIqClientHarness.Create();
        h.Handler.OnCard("cards/card-v03-only.json");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.ProtocolVersionMismatch, ex.Failure.Category);
        Assert.Equal(WorkIqOperation.Discover, ex.Failure.Operation);
        Assert.NotEmpty(h.Events("discovery.version_mismatch"));
        Assert.Null(h.Client.Card);
    }

    [Fact]
    public async Task Discover_AgentSpecificCard_KeepsOpaqueIdAndRoutingTenant()
    {
        using var h = WorkIqClientHarness.Create(config: WorkIqClientHarness.DefaultConfig(agentId: "U_abc123.def456"));
        h.Handler.OnCard("cards/agent-specific-card.json");

        var card = await h.Client.DiscoverAsync(CancellationToken.None);

        Assert.Equal("/a2a/U_abc123.def456/.well-known/agent-card.json", card.CardUri.AbsolutePath);
        Assert.Equal("https://workiq.svc.cloud.microsoft/a2a/U_abc123.def456/", card.SelectedEndpoint.AbsoluteUri);
        Assert.Equal("rt-8d3f2a1c", h.Client.RoutingTenant);
    }

    [Fact]
    public async Task Discover_Card401_UnauthorizedWithDeviceCodeHint()
    {
        using var h = WorkIqClientHarness.Create();
        h.Handler.FailWith(FakeWorkIqHandler.CardMethod, HttpStatusCode.Unauthorized, headers: Headers(("request-id", "req-401")));

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Unauthorized, ex.Failure.Category);
        Assert.Equal(401, ex.Failure.HttpStatus);
        Assert.Equal("req-401", ex.Failure.RequestId);
        Assert.Equal(OutcomeCertainty.Known, ex.Failure.Certainty);
        Assert.Contains(h.Config.Scope, ex.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("device-code", ex.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.NotNull(ex.Failure.Capture);
    }

    [Fact]
    public async Task Discover_MalformedCardJson_Malformed()
    {
        using var h = WorkIqClientHarness.Create();
        h.Handler.OnCard("{ this is not json");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.DiscoverAsync(CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Malformed, ex.Failure.Category);
    }

    // ---------------------------------------------------------------------------------------------------------
    // SendMessage
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_TaskReply_TypedTaskWithRequestIdStatusAndCapture()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json", headers: Headers(("request-id", "req-send-1")));

        var result = await h.Client.SendAsync(NewSend(), CancellationToken.None);

        Assert.Equal(SendMessageResponseCase.Task, result.Value.PayloadCase);
        Assert.Equal(SubmittedTaskId, result.Value.Task!.Id);
        Assert.Equal(SharedContextId, result.Value.Task.ContextId);
        Assert.Equal(TaskState.Submitted, result.Value.Task.Status.State);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("req-send-1", result.RequestId);
        Assert.Equal(WorkIqClientHarness.Start, result.ReceivedAt);
        Assert.NotNull(result.Capture);
        Assert.StartsWith("send-", result.Capture.CaptureId, StringComparison.Ordinal);

        Assert.Equal(1, h.Handler.CardRequestCount);
        var post = Assert.Single(h.Handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("SendMessage", post.JsonRpcMethod);
        Assert.Equal("https://workiq.svc.cloud.microsoft/a2a/", post.Uri.AbsoluteUri);
        Assert.Equal("1.0", post.Headers["A2A-Version"]);
        Assert.Equal("Bearer", post.AuthorizationScheme);
        Assert.Equal("[redacted]", post.Headers["Authorization"]);

        var body = Body(post);
        Assert.Equal("2.0", body["jsonrpc"]!.GetValue<string>());
        var configuration = (JsonObject)body["params"]!["configuration"]!;
        Assert.Equal(new[] { "text/plain" }, configuration["acceptedOutputModes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.False(configuration.TryGetPropertyValue("returnImmediately", out var ri) && ri!.GetValue<bool>());
        Assert.Equal("msg-user-test-0001", body["params"]!["message"]!["messageId"]!.GetValue<string>());
        Assert.Equal("ROLE_USER", body["params"]!["message"]!["role"]!.GetValue<string>());

        var sent = Assert.Single(h.Events("rpc.sent"));
        Assert.Equal(post.JsonRpcId, JsonSerializer.Serialize(sent.RpcId));
        var response = Assert.Single(h.Events("rpc.response"));
        Assert.Equal(200, response.HttpStatus);
        Assert.Equal("req-send-1", response.RequestId);
        Assert.Equal(SubmittedTaskId, response.TaskId);
        Assert.Equal("TASK_STATE_SUBMITTED", response.A2AState);
        Assert.NotNull(response.ElapsedMs);
        Assert.Empty(h.Events("rpc.error"));
    }

    [Fact]
    public async Task Send_DirectMessageReply_TypedMessage()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json");

        var result = await h.Client.SendAsync(NewSend(), CancellationToken.None);

        Assert.Equal(SendMessageResponseCase.Message, result.Value.PayloadCase);
        Assert.Null(result.Value.Task);
        Assert.Equal("msg-agent-a7c1e3f5-0002", result.Value.Message!.MessageId);
        Assert.Equal(Role.Agent, result.Value.Message.Role);
        Assert.Equal(SharedContextId, result.Value.Message.ContextId);
        var text = Assert.Single(result.Value.Message.Parts);
        Assert.StartsWith("Your next meeting", text.Text, StringComparison.Ordinal);
        Assert.Null(result.RequestId);
    }

    [Fact]
    public async Task Send_CallRpcId_GoesOnTheWireForThatCallOnly()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json");

        await h.Client.SendAsync(NewSend(), CancellationToken.None, new WorkIqCallContext { RpcId = "rpc-turn-identity-1" });
        await h.Client.SendAsync(NewSend(), CancellationToken.None);

        var posts = h.Handler.Requests.Where(r => r.Method == HttpMethod.Post).ToArray();
        Assert.Equal("\"rpc-turn-identity-1\"", posts[0].JsonRpcId);
        Assert.NotEqual("\"rpc-turn-identity-1\"", posts[1].JsonRpcId);
        Assert.StartsWith("\"rpc-", posts[1].JsonRpcId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_CallerRegistersIssues_FailureIsTracedButNotRegistered()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.FailWith("SendMessage", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None, new WorkIqCallContext { CallerRegistersIssues = true }));

        Assert.Equal(WorkIqFailureCategory.Forbidden, ex.Failure.Category);
        Assert.Single(h.Events("rpc.error"));
        Assert.Single(h.Events("auth.forbidden"));
        Assert.Empty(h.Issues.Issues);
    }

    [Fact]
    public async Task Send_Http403OnItsOwn_ExactlyOneIssueFromTheClassifier()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.FailWith("SendMessage", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        // The auth handler traces the 403; the client, the layer that ended the call, registers its one issue, and its
        // category, code and cause are the classifier's.
        Assert.Single(h.Events("auth.forbidden"));
        var issue = Assert.Single(h.Issues.Issues);
        var expected = WorkIqIssueClassifier.FromFailure(ex.Failure, WorkIqTraceStage.Send);
        Assert.Equal(expected.Category, issue.Category);
        Assert.Equal(expected.Code, issue.Code);
        Assert.Equal(expected.SuggestedCause, issue.SuggestedCause);
        Assert.Equal(WorkIqIssueCategory.Permission, issue.Category);
    }

    [Fact]
    public async Task Send_Native118Profile_RequestsTheDiagnosticModes()
    {
        using var h = WorkIqClientHarness.Create(config: WorkIqClientHarness.DefaultConfig(diagnosticProfile: "Native118Diagnostics")).WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json");

        await h.Client.SendAsync(NewSend(), CancellationToken.None);

        var post = Assert.Single(h.Handler.Requests, r => r.Method == HttpMethod.Post);
        var modes = Body(post)["params"]!["configuration"]!["acceptedOutputModes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "text/plain", WorkIqA2AClient.RetrievalInfoMediaType, WorkIqA2AClient.DeveloperInfoMediaType }, modes);
    }

    [Fact]
    public async Task Send_CallerSuppliedModes_Kept()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json");
        var request = NewSend();
        request.Configuration = new SendMessageConfiguration { AcceptedOutputModes = ["text/markdown"], HistoryLength = 7 };

        await h.Client.SendAsync(request, CancellationToken.None);

        var configuration = Body(h.Handler.Requests.Single(r => r.Method == HttpMethod.Post))["params"]!["configuration"]!;
        Assert.Equal(new[] { "text/markdown" }, configuration["acceptedOutputModes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(7, configuration["historyLength"]!.GetValue<int>());
    }

    [Fact]
    public async Task Send_RpcErrorOnHttp200_VersionNotSupported_ProtocolVersionMismatch()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/rpc-error-on-http-200-version-not-supported.json", headers: Headers(("request-id", "req-v")));

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.ProtocolVersionMismatch, ex.Failure.Category);
        Assert.Equal(-32009, ex.Failure.RpcErrorCode);
        Assert.Equal(200, ex.Failure.HttpStatus);
        Assert.Equal("req-v", ex.Failure.RequestId);
        Assert.Equal(WorkIqOperation.Send, ex.Failure.Operation);
        Assert.Equal(OutcomeCertainty.Known, ex.Failure.Certainty);
        Assert.NotNull(ex.Failure.RpcRequestId);
        var error = Assert.Single(h.Events("rpc.error"));
        Assert.Equal(-32009, error.RpcErrorCode);
        Assert.Equal(WorkIqTraceLevel.Error, error.Level);
        var issue = Assert.Single(h.Issues.Issues);
        Assert.Equal(WorkIqIssueClassifier.FromFailure(ex.Failure).Code, issue.Code);
        Assert.Equal(WorkIqIssueCategory.Protocol, issue.Category);
        Assert.Equal(-32009, issue.RpcErrorCode);
    }

    [Fact]
    public async Task Send_RpcErrorOnHttp200_MethodNotFound_RemoteError()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/rpc-error-on-http-200-method-not-found.json");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.RemoteError, ex.Failure.Category);
        Assert.Equal(-32601, ex.Failure.RpcErrorCode);
    }

    [Fact]
    public async Task Send_Http429_NotRetried()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.FailWith("SendMessage", HttpStatusCode.TooManyRequests, "envelopes/http-429-body.json", Headers(("Retry-After", "7"), ("request-id", "req-429")));

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.RateLimited, ex.Failure.Category);
        Assert.Equal(429, ex.Failure.HttpStatus);
        Assert.Equal("req-429", ex.Failure.RequestId);
        Assert.Equal(1, h.Handler.CountOf("SendMessage"));
        Assert.Empty(h.Events("rpc.retry"));
        Assert.DoesNotContain("TooManyRequests", ex.Failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_V03ShapedResponse_ProtocolVersionMismatchNeverACompletedTask()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/v03-shaped-response.json");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.ProtocolVersionMismatch, ex.Failure.Category);
        Assert.Contains("kind", ex.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(200, ex.Failure.HttpStatus);
    }

    [Fact]
    public async Task Send_TransportException_TransportIndeterminateExactlyOnePost()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.Throw("SendMessage", new HttpRequestException("socket reset"));

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.Transport, ex.Failure.Category);
        Assert.Equal(OutcomeCertainty.Indeterminate, ex.Failure.Certainty);
        Assert.Equal(1, h.Handler.CountOf("SendMessage"));
        Assert.DoesNotContain("socket reset", ex.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.NotNull(ex.Failure.Capture);
        Assert.Contains(h.Issues.Issues, i => i.Category == WorkIqIssueCategory.Network);
    }

    [Fact]
    public async Task Send_Http503_RemoteErrorIndeterminateNotRetried()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.FailWith("SendMessage", HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.RemoteError, ex.Failure.Category);
        Assert.Equal(OutcomeCertainty.Indeterminate, ex.Failure.Certainty);
        Assert.Equal(1, h.Handler.CountOf("SendMessage"));
    }

    [Fact]
    public async Task Send_CallerCanceledBeforeDispatch_Canceled()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), cts.Token));

        Assert.Equal(WorkIqFailureCategory.Canceled, ex.Failure.Category);
    }

    // ---------------------------------------------------------------------------------------------------------
    // GetTask / CancelTask
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetTask_UnwrappedShape_TypedTaskWithHistoryLength()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("GetTask", "envelopes/gettask-completed.json");

        var result = await h.Client.GetTaskAsync(SubmittedTaskId, 20, CancellationToken.None);

        Assert.Equal(SubmittedTaskId, result.Value.Id);
        Assert.Equal(TaskState.Completed, result.Value.Status.State);
        Assert.Single(result.Value.Artifacts!);
        var post = Assert.Single(h.Handler.Requests, r => r.Method == HttpMethod.Post);
        var parameters = Body(post)["params"]!;
        Assert.Equal(SubmittedTaskId, parameters["id"]!.GetValue<string>());
        Assert.Equal(20, parameters["historyLength"]!.GetValue<int>());
        Assert.Equal("TASK_STATE_COMPLETED", Assert.Single(h.Events("rpc.response")).A2AState);
    }

    [Fact]
    public async Task GetTask_Http429_RetriedAfterRetryAfterThenSucceeds()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpcSequence(
            "GetTask",
            FakeWorkIqResponse.Fail(HttpStatusCode.TooManyRequests, "envelopes/http-429-body.json", Headers(("Retry-After", "7"), ("request-id", "req-429"))),
            FakeWorkIqResponse.Json("envelopes/gettask-completed.json"));

        var pending = h.Client.GetTaskAsync(SubmittedTaskId, null, CancellationToken.None);
        var result = await WorkIqClientHarness.PumpAsync(pending, h.Clock, TimeSpan.FromSeconds(1));

        Assert.Equal(TaskState.Completed, result.Value.Status.State);
        Assert.Equal(2, h.Handler.CountOf("GetTask"));
        var retry = Assert.Single(h.Events("rpc.retry"));
        Assert.Equal(429, retry.HttpStatus);
        Assert.Equal("req-429", retry.RequestId);
        Assert.Contains("Retry-After honoured", retry.Message, StringComparison.Ordinal);
        Assert.True(h.Clock.GetUtcNow() - WorkIqClientHarness.Start >= TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task GetTask_Http429Forever_GivesUpAfterMaxRetries()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.FailWith("GetTask", HttpStatusCode.TooManyRequests, "envelopes/http-429-body.json", Headers(("Retry-After", "1")));

        var pending = Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.GetTaskAsync(SubmittedTaskId, null, CancellationToken.None));
        var ex = await WorkIqClientHarness.PumpAsync(pending, h.Clock, TimeSpan.FromSeconds(1));

        Assert.Equal(WorkIqFailureCategory.RateLimited, ex.Failure.Category);
        Assert.Equal(WorkIqA2AClient.MaxRetries + 1, h.Handler.CountOf("GetTask"));
        Assert.Equal(WorkIqA2AClient.MaxRetries, h.Events("rpc.retry").Count);
    }

    [Fact]
    public async Task CancelTask_CompletedRace_ReturnsTheCompletedTask()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("CancelTask", "envelopes/canceltask-completed-race.json");

        var result = await h.Client.CancelTaskAsync("task-7b3f9d1e-6a2c-4e5b-8f0a-1d4c7b9e3a62", CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.Value.Status.State);
        Assert.Equal("CancelTask", Assert.Single(h.Handler.Requests, r => r.Method == HttpMethod.Post).JsonRpcMethod);
    }

    [Fact]
    public async Task CancelTask_NotFound_NotFoundWithRpcCode()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("CancelTask", "envelopes/canceltask-not-found-error.json");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.CancelTaskAsync("task-00000000-0000-4000-8000-000000000000", CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.NotFound, ex.Failure.Category);
        Assert.Equal(-32001, ex.Failure.RpcErrorCode);
        Assert.Equal(WorkIqOperation.CancelTask, ex.Failure.Operation);
        Assert.Equal("task-00000000-0000-4000-8000-000000000000", ex.Failure.TaskId);
    }

    [Fact]
    public async Task CancelTask_NotCancelable_RemoteErrorWithRpcCode()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("CancelTask", "envelopes/canceltask-not-cancelable-error.json");

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.CancelTaskAsync("task-7b3f9d1e-6a2c-4e5b-8f0a-1d4c7b9e3a62", CancellationToken.None));

        Assert.Equal(WorkIqFailureCategory.RemoteError, ex.Failure.Category);
        Assert.Equal(-32002, ex.Failure.RpcErrorCode);
    }

    [Fact]
    public async Task GetTask_OtherTaskReturned_TracesWarningButReturns()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("GetTask", "envelopes/gettask-completed.json");

        var result = await h.Client.GetTaskAsync("task-other", null, CancellationToken.None);

        Assert.Equal(SubmittedTaskId, result.Value.Id);
        Assert.Single(h.Events("rpc.task_id_mismatch"));
    }

    [Fact]
    public async Task GetTask_BlankTaskId_Throws()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.GetTaskAsync(" ", null, CancellationToken.None));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Diagnostics hygiene
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Traces_NeverCarryTheToken()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json").FailWith("GetTask", HttpStatusCode.Unauthorized);

        await h.Client.SendAsync(NewSend(), CancellationToken.None);
        await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.GetTaskAsync(SubmittedTaskId, null, CancellationToken.None));

        h.AssertNoSecretInDiagnostics();
        Assert.All(h.Trace.Events, e => Assert.Equal(WorkIqClientHarness.RunId, e.RunId));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Pure helpers
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Classify_TaskCanceledWithTimeoutInner_IsTimeoutIndeterminate()
    {
        var failure = WorkIqA2AClient.Classify(new TaskCanceledException("t", new TimeoutException()), WorkIqOperation.Send, "req-1");

        Assert.Equal(WorkIqFailureCategory.Timeout, failure.Category);
        Assert.Equal(OutcomeCertainty.Indeterminate, failure.Certainty);
        Assert.Equal("req-1", failure.RequestId);
    }

    [Fact]
    public void Classify_CallerTokenFired_IsCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var failure = WorkIqA2AClient.Classify(new TaskCanceledException(), WorkIqOperation.GetTask, null, "task-1", "ctx-1", cts.Token);

        Assert.Equal(WorkIqFailureCategory.Canceled, failure.Category);
        Assert.Equal("task-1", failure.TaskId);
        Assert.Equal("ctx-1", failure.ContextId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, WorkIqFailureCategory.Unauthorized, OutcomeCertainty.Known)]
    [InlineData(HttpStatusCode.Forbidden, WorkIqFailureCategory.Forbidden, OutcomeCertainty.Known)]
    [InlineData(HttpStatusCode.NotFound, WorkIqFailureCategory.NotFound, OutcomeCertainty.Known)]
    [InlineData(HttpStatusCode.TooManyRequests, WorkIqFailureCategory.RateLimited, OutcomeCertainty.Known)]
    [InlineData(HttpStatusCode.BadGateway, WorkIqFailureCategory.RemoteError, OutcomeCertainty.Indeterminate)]
    public void Classify_HttpRequestExceptionOnSend_ByStatus(HttpStatusCode status, WorkIqFailureCategory category, OutcomeCertainty certainty)
    {
        var failure = WorkIqA2AClient.Classify(new HttpRequestException("x", null, status), WorkIqOperation.Send, null, scope: "api://scope");

        Assert.Equal(category, failure.Category);
        Assert.Equal(certainty, failure.Certainty);
        Assert.Equal((int)status, failure.HttpStatus);
        Assert.DoesNotContain("x", failure.SafeMessage.Split(' '), StringComparer.Ordinal);
    }

    [Fact]
    public void Classify_Http503OnGetTask_IsKnown()
    {
        var failure = WorkIqA2AClient.Classify(new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), WorkIqOperation.GetTask, null);

        Assert.Equal(WorkIqFailureCategory.RemoteError, failure.Category);
        Assert.Equal(OutcomeCertainty.Known, failure.Certainty);
    }

    [Fact]
    public void Classify_A2AExceptionCodes_Mapped()
    {
        Assert.Equal(WorkIqFailureCategory.ProtocolVersionMismatch, WorkIqA2AClient.Classify(new A2AException("v", A2AErrorCode.VersionNotSupported), WorkIqOperation.Send, null).Category);
        Assert.Equal(WorkIqFailureCategory.NotFound, WorkIqA2AClient.Classify(new A2AException("n", A2AErrorCode.TaskNotFound), WorkIqOperation.GetTask, null).Category);
        var other = WorkIqA2AClient.Classify(new A2AException("i", A2AErrorCode.InternalError), WorkIqOperation.GetTask, null);
        Assert.Equal(WorkIqFailureCategory.RemoteError, other.Category);
        Assert.Equal(-32603, other.RpcErrorCode);
        var wrapped = WorkIqA2AClient.Classify(new A2AException("HTTP request failed", new HttpRequestException("x", null, HttpStatusCode.Forbidden)), WorkIqOperation.Discover, null);
        Assert.Equal(WorkIqFailureCategory.Forbidden, wrapped.Category);
    }

    [Fact]
    public void Classify_JsonAndUnknown()
    {
        Assert.Equal(WorkIqFailureCategory.Malformed, WorkIqA2AClient.Classify(new JsonException("bad"), WorkIqOperation.Send, null).Category);
        var unknown = WorkIqA2AClient.Classify(new InvalidOperationException("secret body text"), WorkIqOperation.Send, null);
        Assert.Equal(WorkIqFailureCategory.Unknown, unknown.Category);
        Assert.Equal(OutcomeCertainty.Indeterminate, unknown.Certainty);
        Assert.DoesNotContain("secret body text", unknown.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_ProtocolException_FillsMissingIds()
    {
        var inner = new WorkIqProtocolException(new WorkIqFailure(WorkIqFailureCategory.NotFound, "m", OutcomeCertainty.Known, WorkIqOperation.GetTask));

        var failure = WorkIqA2AClient.Classify(inner, WorkIqOperation.GetTask, "req-9", "task-9", "ctx-9");

        Assert.Equal("req-9", failure.RequestId);
        Assert.Equal("task-9", failure.TaskId);
        Assert.Equal("ctx-9", failure.ContextId);
    }

    [Fact]
    public void AcceptedOutputModesFor_Profiles()
    {
        Assert.Equal(new[] { "text/plain" }, WorkIqA2AClient.AcceptedOutputModesFor(DiagnosticProfile.PublicAnswerOnly));
        Assert.Equal(new[] { "text/plain", "application/vnd.ms-workiq-internal.retrieval", "application/vnd.ms-workiq.developer-info" }, WorkIqA2AClient.AcceptedOutputModesFor(DiagnosticProfile.Native118Diagnostics));
    }

    [Fact]
    public void ComputeRetryDelay_HonoursRetryAfterCapsAndGrows()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), WorkIqA2AClient.ComputeRetryDelay(0, TimeSpan.FromSeconds(7)));
        Assert.Equal(WorkIqA2AClient.RetryMaxDelay, WorkIqA2AClient.ComputeRetryDelay(0, TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.Zero, WorkIqA2AClient.ComputeRetryDelay(0, TimeSpan.FromSeconds(-3)));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var delay = WorkIqA2AClient.ComputeRetryDelay(attempt, null);
            var floor = WorkIqA2AClient.RetryBaseDelay * Math.Pow(2, attempt);
            Assert.InRange(delay, floor, floor + WorkIqA2AClient.RetryBaseDelay);
        }
    }

    [Fact]
    public void DetectLegacyShape_FindsKindAndLowercaseStates()
    {
        Assert.NotNull(WorkIqA2AClient.DetectLegacyShape(JsonNode.Parse("""{"kind":"task","id":"t"}""")));
        Assert.NotNull(WorkIqA2AClient.DetectLegacyShape(JsonNode.Parse("""{"task":{"kind":"task"}}""")));
        Assert.NotNull(WorkIqA2AClient.DetectLegacyShape(JsonNode.Parse("""{"id":"t","status":{"state":"completed"}}""")));
        Assert.NotNull(WorkIqA2AClient.DetectLegacyShape(JsonNode.Parse("""{"task":{"id":"t","status":{"state":"working"}}}""")));
        Assert.Null(WorkIqA2AClient.DetectLegacyShape(JsonNode.Parse("""{"task":{"id":"t","status":{"state":"TASK_STATE_COMPLETED"}}}""")));
        Assert.Null(WorkIqA2AClient.DetectLegacyShape(JsonNode.Parse("""{"message":{"messageId":"m"}}""")));
        Assert.Null(WorkIqA2AClient.DetectLegacyShape(null));
    }

    [Fact]
    public void RegistersIssue_OnlyForAFailureThisClientEnds()
    {
        var forbidden = new WorkIqFailure(WorkIqFailureCategory.Forbidden, "Send: HTTP 403", OutcomeCertainty.Known, WorkIqOperation.Send, HttpStatus: 403);

        Assert.True(WorkIqA2AClient.RegistersIssue(forbidden, call: null));
        Assert.True(WorkIqA2AClient.RegistersIssue(forbidden, new WorkIqCallContext()));
        Assert.False(WorkIqA2AClient.RegistersIssue(forbidden, new WorkIqCallContext { CallerRegistersIssues = true }));
        Assert.False(WorkIqA2AClient.RegistersIssue(forbidden with { Category = WorkIqFailureCategory.Canceled }, call: null));
        Assert.False(WorkIqA2AClient.RegistersIssue(forbidden with { TokenFailure = WorkIqTokenFailure.ProviderFailed }, call: null));
    }

    [Fact]
    public async Task Send_CanceledByTheCaller_NoIssue()
    {
        using var h = WorkIqClientHarness.Create().WithDefaultCard();
        h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json");
        await h.Client.DiscoverAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => h.Client.SendAsync(NewSend(), stop.Token));

        Assert.Equal(WorkIqFailureCategory.Canceled, ex.Failure.Category);
        Assert.Single(h.Events("rpc.error"));
        Assert.Empty(h.Issues.Issues);
    }

    [Fact]
    public void Constructor_ValidatesConfigAndOptions()
    {
        using var h = WorkIqClientHarness.Create();

        Assert.Throws<InvalidOperationException>(() => new WorkIqA2AClient(new Microsoft365Config(), h.Options, h.Http, h.Capture, h.Clock, h.Diagnostics));
        Assert.Throws<InvalidOperationException>(() => new WorkIqA2AClient(h.Config, new WorkIqOptions { MaxTurns = -1 }, h.Http, h.Capture, h.Clock, h.Diagnostics));
        Assert.Throws<ArgumentNullException>(() => new WorkIqA2AClient(h.Config, h.Options, null!, h.Capture, h.Clock, h.Diagnostics));
    }
}
