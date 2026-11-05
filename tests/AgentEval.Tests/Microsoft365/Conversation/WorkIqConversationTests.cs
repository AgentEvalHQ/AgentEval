// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.Protocol;
using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Conversation;

/// <summary>
/// <see cref="WorkIqConversation"/> lifecycle over the real client + <see cref="FakeWorkIqHandler"/> (and a
/// scripted <see cref="FakeWorkIqA2AClient"/> where a send must block): context ownership, the blocking turn path
/// with <c>GetTask</c> polling, every outcome mapping, pending interactions, the single-active-turn guard, reset,
/// abandonment (never a <c>CancelTask</c>), local stops, budgets, handle validation and the Location metadata.
/// Reduction and normalisation run through lane C's reducer/mapping and lane F's normaliser.
/// </summary>
public sealed class WorkIqConversationTests
{
    private const string SharedContextId = "ctx-5e0c7a2b-91d4-4f8e-b6a3-0d2f8c1e7b55";
    private const string SubmittedTaskId = "task-7a1e3c5b-9d2f-4e8a-b1c6-3f5d7e9a0b21";
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    private static WorkIqTurnRequest Turn(string input = "Summarize the status of Project Atlas and cite the sources.", string caseId = "case-1", string? turnId = null) =>
        new(input, caseId, turnId ?? "turn-" + Guid.NewGuid().ToString("N"));

    private static (WorkIqClientHarness Harness, WorkIqConversation Conversation) Real(
        WorkIqOptions? options = null,
        Microsoft365Config? config = null,
        bool withCard = true)
    {
        var harness = WorkIqClientHarness.Create(config: config, options: options);
        if (withCard)
        {
            harness.WithDefaultCard();
        }

        var conversation = new WorkIqConversation(harness.Client, harness.Config, harness.Options, harness.Clock, harness.Diagnostics);
        return (harness, conversation);
    }

    private static (FakeWorkIqA2AClient Client, WorkIqConversation Conversation, ListTraceSink Trace, ListIssueSink Issues, WaiterTrackingTimeProvider Clock) Fake(WorkIqOptions? options = null)
    {
        var client = new FakeWorkIqA2AClient();
        var clock = new WaiterTrackingTimeProvider(WorkIqClientHarness.Start);
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        var diagnostics = new WorkIqDiagnostics("run-fake", null, [trace], [issues], clock);
        var conversation = new WorkIqConversation(client, WorkIqClientHarness.DefaultConfig(), options ?? new WorkIqOptions(), clock, diagnostics);
        return (client, conversation, trace, issues, clock);
    }

    /// <summary>Real-time bound for <see cref="PumpRecoveryAsync"/>: every GetTask/CancelTask round-trip in these tests completes in milliseconds.</summary>
    private static readonly TimeSpan RecoveryWatchdog = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Drives a turn whose recovery loop sleeps on the fake clock. The clock advances by <paramref name="step"/> only
    /// after the loop has armed its poll timer and announced the sleep (<c>recovery.waiting</c>), never while a
    /// GetTask or CancelTask round-trip is still travelling through the HTTP pipeline on real thread-pool hops.
    /// <see cref="WorkIqClientHarness.PumpAsync{T}"/> steps the clock on every yield instead, which spends the whole
    /// recovery budget and turn deadline within a few milliseconds of real time while the first poll is still in
    /// flight; that is only right where nothing real is meant to complete (the turn-deadline test).
    /// </summary>
    private static async Task<WorkIqTurnResult> PumpRecoveryAsync(Task<WorkIqTurnResult> turn, WorkIqClientHarness h, TimeSpan step)
    {
        var driven = 0;
        var watchdog = Stopwatch.StartNew();
        while (true)
        {
            while (!turn.IsCompleted && h.Events("recovery.waiting").Count <= driven)
            {
                if (watchdog.Elapsed > RecoveryWatchdog)
                {
                    throw new TimeoutException($"The turn neither completed nor armed another GetTask poll within {RecoveryWatchdog} of real time ({driven} poll(s) driven).");
                }

                await Task.Delay(1);
            }

            if (turn.IsCompleted)
            {
                return await turn;
            }

            driven++;
            h.Clock.Advance(step);
        }
    }

    private static JsonObject Message(RecordedRequest post) => (JsonObject)JsonNode.Parse(post.BodyJson!)!["params"]!["message"]!;

    private static RecordedRequest[] Posts(WorkIqClientHarness h, string method) =>
        h.Handler.Requests.Where(r => r.JsonRpcMethod == method).ToArray();

    // ---------------------------------------------------------------------------------------------------------
    // Completed turns
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunTurn_CompletedTask_AnswerTextHandleAndIdentity()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");
            var request = Turn(caseId: "atlas-1", turnId: "turn-atlas-1");

            var result = await conversation.RunTurnAsync(request);

            Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
            Assert.True(result.IsCompleted);
            Assert.Equal(TransportOutcome.Completed, result.Transport);
            Assert.Equal(OutputCompleteness.Complete, result.AnswerCompleteness);
            Assert.Equal(TelemetryCompleteness.NotRequested, result.Telemetry);
            Assert.StartsWith("Project Atlas is on schedule", result.AnswerText, StringComparison.Ordinal);
            Assert.Contains(" Two risks stay open", result.AnswerText, StringComparison.Ordinal);
            Assert.NotNull(result.Task);
            Assert.Equal("task-2c9f4e7a-3b1d-4a6e-8f5c-7d0e1b9a2c43", result.Task.TaskId);
            Assert.Equal(SharedContextId, result.Task.ContextId);
            Assert.Equal(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), result.Task.Endpoint);
            Assert.Equal("1.0", result.Task.ProtocolVersion);
            Assert.Equal(conversation.PrincipalKey, result.Task.PrincipalKey);
            Assert.Equal(SharedContextId, result.ContextId);
            Assert.Null(result.Failure);
            Assert.Null(result.PendingInteraction);
            Assert.NotNull(result.Evidence);
            Assert.Equal(0, result.Timing.RecoveryRequests);
            Assert.NotNull(result.Timing.FirstEventAt);
            Assert.NotNull(result.Timing.TerminalAt);
            Assert.Equal("atlas-1", result.Identity.CaseId);
            Assert.Equal("turn-atlas-1", result.Identity.TurnId);
            Assert.Equal(WorkIqClientHarness.RunId, result.Identity.RunId);
            Assert.Equal(0, result.Identity.TurnIndex);
            Assert.Equal(result.Task.TaskId, result.Identity.ProviderTaskId);
            Assert.Equal(SharedContextId, result.Identity.ProviderContextId);

            Assert.Equal(SharedContextId, conversation.ContextId);
            Assert.Null(conversation.ActiveTask);
            Assert.False(conversation.IsTurnActive);
            Assert.NotNull(conversation.Card);

            var post = Assert.Single(Posts(h, "SendMessage"));
            var message = Message(post);
            Assert.Equal(result.Identity.ClientMessageId, message["messageId"]!.GetValue<string>());
            Assert.False(message.ContainsKey("contextId") && message["contextId"] is not null);
            Assert.False(message.ContainsKey("taskId") && message["taskId"] is not null);
            Assert.Equal(JsonSerializer.Serialize(result.Identity.RpcRequestId), post.JsonRpcId);

            var outcome = Assert.Single(h.Events("turn.outcome"));
            Assert.Equal(WorkIqTraceLevel.Info, outcome.Level);
            Assert.Equal("TASK_STATE_COMPLETED", outcome.A2AState);
            Assert.Single(h.Events("task.state"));
            Assert.Single(h.Events("discovery.card_recorded"));
            Assert.Equal(0, h.Diagnostics.IssueCount);
        }
    }

    [Fact]
    public async Task RunTurn_FreshContextPerConversation_FirstSendOmitsContextId()
    {
        var (h1, c1) = Real();
        var (h2, c2) = Real();
        using (h1)
        using (h2)
        await using (c1)
        await using (c2)
        {
            h1.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");
            h2.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");

            await c1.RunTurnAsync(Turn());
            await c2.RunTurnAsync(Turn());

            Assert.False(Message(Posts(h1, "SendMessage")[0]).ContainsKey("contextId"));
            Assert.False(Message(Posts(h2, "SendMessage")[0]).ContainsKey("contextId"));
            Assert.Equal(SharedContextId, c1.ContextId);
            Assert.Equal(SharedContextId, c2.ContextId);
        }
    }

    [Fact]
    public async Task RunTurn_ContinueContext_ReusesContextIdAndOmitsCompletedTaskId()
    {
        var (h, conversation) = Real(new WorkIqOptions { ConversationPolicy = ConversationPolicy.ContinueContext, HistoryLength = 20 });
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");

            var first = await conversation.RunTurnAsync(Turn(caseId: "c-1"));
            var second = await conversation.RunTurnAsync(Turn("And the risks?", caseId: "c-2"));

            var posts = Posts(h, "SendMessage");
            Assert.Equal(2, posts.Length);
            Assert.False(Message(posts[0]).ContainsKey("contextId"));
            Assert.Equal(SharedContextId, Message(posts[1])["contextId"]!.GetValue<string>());
            Assert.False(Message(posts[1]).ContainsKey("taskId"));
            Assert.Equal(20, JsonNode.Parse(posts[1].BodyJson!)!["params"]!["configuration"]!["historyLength"]!.GetValue<int>());
            Assert.Equal(1, second.Identity.TurnIndex);
            Assert.NotEqual(first.Identity.ClientMessageId, second.Identity.ClientMessageId);
            Assert.NotEqual(first.Identity.RpcRequestId, second.Identity.RpcRequestId);
        }
    }

    [Fact]
    public async Task RunTurn_DefaultPolicy_SecondTurnStillOmitsContextId()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");

            await conversation.RunTurnAsync(Turn());
            await conversation.RunTurnAsync(Turn());

            Assert.All(Posts(h, "SendMessage"), p => Assert.False(Message(p).ContainsKey("contextId")));
        }
    }

    [Fact]
    public async Task RunTurn_SubmittedThenWorkingThenCompleted_PollsGetTaskAndCountsRecovery()
    {
        var options = new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromSeconds(1), RecoveryBudget = TimeSpan.FromMinutes(10) };
        var (h, conversation) = Real(options);
        using (h)
        await using (conversation)
        {
            h.Handler
                .OnRpc("SendMessage", "envelopes/send-task-submitted.json")
                .OnRpcSequence("GetTask", "envelopes/gettask-working.json", "envelopes/gettask-completed.json");

            var result = await PumpRecoveryAsync(conversation.RunTurnAsync(Turn()), h, Step);

            Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
            Assert.Equal(TransportOutcome.Completed, result.Transport);
            Assert.Equal(2, result.Timing.RecoveryRequests);
            Assert.Equal(2, h.Handler.CountOf("GetTask"));
            Assert.Equal(1, h.Handler.CountOf("SendMessage"));
            Assert.Equal(SubmittedTaskId, result.Task!.TaskId);
            Assert.StartsWith("Project Atlas is on schedule", result.AnswerText, StringComparison.Ordinal);
            Assert.Equal(new[] { "TASK_STATE_SUBMITTED", "TASK_STATE_WORKING", "TASK_STATE_COMPLETED" }, h.Events("task.state").Select(e => e.A2AState).ToArray());
            Assert.Single(h.Events("recovery.started"));
            Assert.Equal(2, h.Events("recovery.waiting").Count);
            Assert.Single(h.Events("recovery.finished"));
            Assert.Null(conversation.ActiveTask);
            Assert.True(result.Timing.TerminalAt > result.Timing.FirstEventAt);
        }
    }

    [Fact]
    public async Task RunTurn_DirectMessage_CompletedWithoutTask()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json");

            var result = await conversation.RunTurnAsync(Turn("What is my next meeting?"));

            Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
            Assert.Null(result.Task);
            Assert.Equal(OutputCompleteness.Complete, result.AnswerCompleteness);
            Assert.StartsWith("Your next meeting", result.AnswerText, StringComparison.Ordinal);
            Assert.Equal(SharedContextId, result.ContextId);
            Assert.Equal("msg-agent-a7c1e3f5-0002", result.Identity.ProviderMessageId);
            Assert.Null(result.Identity.ProviderTaskId);
            Assert.Equal(SharedContextId, conversation.ContextId);
            Assert.Null(conversation.ActiveTask);
        }
    }

    [Fact]
    public async Task RunTurn_EmptyAnswer_CompletedEmptyWithEmptyText()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-empty-answer.json");

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
            Assert.Equal(OutputCompleteness.Empty, result.AnswerCompleteness);
            Assert.Equal("", result.AnswerText);
            Assert.Null(result.Failure);
        }
    }

    [Fact]
    public async Task RunTurn_TwoAnswerArtifacts_CompletedAmbiguousNoTextAndIssue()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-two-text-artifacts-ambiguous.json");

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
            Assert.Equal(OutputCompleteness.Ambiguous, result.AnswerCompleteness);
            Assert.Null(result.AnswerText);

            // Registered once, by the normaliser on the run's diagnostics; the selection step only traces it.
            var issue = Assert.Single(h.Issues.Issues, i => i.Code == "answer.ambiguous");
            Assert.Equal(WorkIqTraceStage.Normalize, issue.Stage);
            Assert.Contains(h.Events("answer.ambiguous"), e => e.Stage == WorkIqTraceStage.Reduce);
        }
    }

    [Fact]
    public async Task RunTurn_AnswerWithDiagnostics_OnlyAnswerTextProjected()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-developer-and-retrieval-info.json");

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(OutputCompleteness.Complete, result.AnswerCompleteness);
            Assert.StartsWith("Project Atlas is on schedule", result.AnswerText, StringComparison.Ordinal);
            Assert.DoesNotContain("plugins", result.AnswerText, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Suspended and failed turns
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunTurn_InputRequiredConfirmation_PendingInteractionWithProposedCallsAndTaskKept()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-input-required-confirmation.json");

            var result = await conversation.RunTurnAsync(Turn("Create a 30-minute Atlas pilot kickoff on 17 November 2026 at 09:00 and invite the Atlas team."));

            Assert.Equal(WorkIqTurnOutcome.InputRequired, result.Outcome);
            Assert.False(result.IsCompleted);
            Assert.Null(result.AnswerText);
            Assert.Equal(OutputCompleteness.None, result.AnswerCompleteness);
            Assert.Null(result.Failure);
            Assert.Equal("task-9e3a7c1d-5f2b-4d8e-a6c0-1b4f7e2d9a58", result.Task!.TaskId);
            Assert.Equal(result.Task.TaskId, conversation.ActiveTask?.TaskId);

            var pending = result.PendingInteraction;
            Assert.NotNull(pending);
            Assert.Equal(WorkIqInteractionKind.ConfirmationRequired, pending.Kind);
            Assert.Equal("req-c3d1f7a9-5e2b-4d8c-9a6f-1b4e7c0d2a58", pending.SourceRequestId);
            Assert.Equal(WorkIqConversation.InteractionProfileId, pending.ProfileId);
            Assert.Equal(result.Task.TaskId, pending.Task.TaskId);
            Assert.StartsWith("Copilot needs your confirmation", pending.PromptText, StringComparison.Ordinal);
            Assert.Equal(new[] { "Allow", "Cancel" }, pending.RecognizedOptions);
            Assert.NotNull(pending.RawInteractionData);
            Assert.Equal("confirmation_required", pending.RawInteractionData.Value.GetProperty("input-required").GetProperty("type").GetString());
            var source = Assert.Single(pending.Sources);
            Assert.Contains("/status/message/parts/1", source.JsonPointer, StringComparison.Ordinal);

            var call = Assert.Single(pending.ProposedCalls);
            Assert.Equal("call-0a1b2c3d4e5f", call.CallId);
            Assert.Equal("function", call.CallType);
            Assert.Equal("CreateDemoEvent", call.FunctionName);
            Assert.StartsWith("{\"subject\":\"Atlas pilot kickoff\"", call.ArgumentsRawText, StringComparison.Ordinal);
            Assert.True(call.ParsedArguments.IsPresent);
            Assert.Equal("Atlas pilot kickoff", call.ParsedArguments.Value.GetProperty("subject").GetString());
            Assert.NotEmpty(call.Sources);

            Assert.Equal("Allow", WorkIqInteractionResponse.ForOption(pending, "Allow").SelectedOption);
            Assert.Throws<ArgumentException>(() => WorkIqInteractionResponse.ForOption(pending, "Yes"));
            Assert.Contains(h.Issues.Issues, i => i.Code == "turn.inputrequired");
            Assert.Equal(WorkIqTraceLevel.Warning, Assert.Single(h.Events("turn.outcome")).Level);
        }
    }

    [Fact]
    public async Task RunTurn_RepeatedFunctionNames_MultiplicityPreserved()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            var twoCalls = JsonSerializer.Serialize("""[{"id":"call-1","type":"function","function":{"name":"CreateDemoEvent","arguments":"{\"subject\":\"A\"}"}},{"id":"call-2","type":"function","function":{"name":"CreateDemoEvent","arguments":"{\"subject\":\"B\"}"}}]""");
            var oneCall = JsonSerializer.Serialize("""[{"id":"call-3","type":"function","function":{"name":"CreateDemoEvent","arguments":null}}]""");
            var envelope = $$"""
                {
                  "jsonrpc": "2.0",
                  "id": "rpc-x",
                  "result": {
                    "task": {
                      "id": "task-multi",
                      "contextId": "ctx-multi",
                      "status": {
                        "state": "TASK_STATE_INPUT_REQUIRED",
                        "message": {
                          "messageId": "msg-multi",
                          "role": "ROLE_AGENT",
                          "parts": [
                            { "data": { "input-required": { "type": "confirmation_required", "sourceRequestId": "req-multi", "adaptiveCard": { "type": "AdaptiveCard", "actions": [
                              { "type": "Action.Submit", "title": "Allow", "data": { "message": { "action": {{twoCalls}}, "actionId": {{oneCall}} }, "confirmationOption": "Allow" } },
                              { "type": "Action.Submit", "title": "Cancel", "data": { "confirmationOption": "Cancel" } } ] } } }, "mediaType": "application/vnd.ms-workiq.interaction+json" }
                          ]
                        }
                      }
                    }
                  }
                }
                """;
            h.Handler.OnRpc("SendMessage", envelope);

            var result = await conversation.RunTurnAsync(Turn());

            var pending = result.PendingInteraction!;
            var ids = pending.ProposedCalls.Select(c => c.CallId).ToArray();
            Assert.True(pending.ProposedCalls.Length >= 3, "every proposed call must be kept (no de-duplication by name): " + string.Join(",", ids));
            Assert.Contains("call-1", ids);
            Assert.Contains("call-2", ids);
            Assert.Contains("call-3", ids);
            Assert.All(pending.ProposedCalls, c => Assert.Equal("CreateDemoEvent", c.FunctionName));
            Assert.Contains(pending.ProposedCalls, c => c.CallId == "call-2" && c.ArgumentsRawText == "{\"subject\":\"B\"}");
            Assert.Equal(new[] { "Allow", "Cancel" }, pending.RecognizedOptions);
        }
    }

    [Fact]
    public async Task RunTurn_AuthRequired_PendingAuthenticationRequired()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-auth-required.json");

            var result = await conversation.RunTurnAsync(Turn("List my open tickets in Demo Ticketing."));

            Assert.Equal(WorkIqTurnOutcome.AuthRequired, result.Outcome);
            Assert.Null(result.AnswerText);
            Assert.Null(result.Failure);
            var pending = result.PendingInteraction!;
            Assert.Equal(WorkIqInteractionKind.AuthenticationRequired, pending.Kind);
            Assert.Equal("req-9b4e2d7c-1f6a-4c3e-8d5b-7a0f2e9c4b16", pending.SourceRequestId);
            Assert.Empty(pending.ProposedCalls);
            Assert.Empty(pending.RecognizedOptions);
            Assert.Contains("/status/message/parts/1", Assert.Single(pending.Sources).JsonPointer, StringComparison.Ordinal);
            Assert.Equal("task-1b6d9f3a-7c4e-4a2b-8d5f-0e3c6a9b2d74", conversation.ActiveTask?.TaskId);
        }
    }

    [Fact]
    public async Task RunTurn_MetadataOnlyPolicy_PendingInteractionWithoutContent()
    {
        var (h, conversation) = Real(new WorkIqOptions { ContentPolicy = EvidenceContentPolicy.MetadataOnly });
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-input-required-confirmation.json");

            var result = await conversation.RunTurnAsync(Turn());

            var pending = result.PendingInteraction!;
            Assert.Null(pending.PromptText);
            Assert.Null(pending.RawInteractionData);
            Assert.Equal(WorkIqInteractionKind.ConfirmationRequired, pending.Kind);
            Assert.Single(pending.ProposedCalls);
        }
    }

    [Fact]
    public async Task RunTurn_FailedWithPartialArtifact_FailedWithoutAnswerText()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-failed-with-partial-artifact.json");

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(WorkIqTurnOutcome.Failed, result.Outcome);
            Assert.Null(result.AnswerText);
            Assert.Equal(OutputCompleteness.Partial, result.AnswerCompleteness);
            Assert.Equal(TransportOutcome.Completed, result.Transport);
            Assert.NotNull(result.Failure);
            Assert.Equal(WorkIqFailureCategory.RemoteError, result.Failure.Category);
            Assert.Equal(OutcomeCertainty.Known, result.Failure.Certainty);
            Assert.Equal("TASK_STATE_FAILED", result.Failure.RemoteTaskState);
            Assert.Equal("task-4d8b2f6e-1a3c-4b7d-9e0f-5c2a8d6b1e37", result.Failure.TaskId);
            Assert.Null(conversation.ActiveTask);
            Assert.Contains(h.Issues.Issues, i => i.Code == "turn.failed" && i.A2AState == "TASK_STATE_FAILED");
            Assert.Same(result, new WorkIqTurnNotCompletedException(result).TurnResult);
        }
    }

    [Theory]
    [InlineData("envelopes/send-task-rejected.json", WorkIqTurnOutcome.Rejected, "TASK_STATE_REJECTED")]
    [InlineData("envelopes/send-task-canceled.json", WorkIqTurnOutcome.Canceled, "TASK_STATE_CANCELED")]
    public async Task RunTurn_RejectedOrCanceled_MappedWithTerminalFailure(string fixture, WorkIqTurnOutcome expected, string rawState)
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", fixture);

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(expected, result.Outcome);
            Assert.Null(result.AnswerText);
            Assert.Equal(rawState, result.Failure?.RemoteTaskState);
            Assert.Equal(WorkIqFailureCategory.RemoteError, result.Failure?.Category);
            Assert.Null(result.PendingInteraction);
        }
    }

    [Fact]
    public async Task RunTurn_TransportExceptionBeforeTaskId_IndeterminateExactlyOnePost()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.Throw("SendMessage", new HttpRequestException("connection reset"));

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(WorkIqTurnOutcome.Indeterminate, result.Outcome);
            Assert.Equal(TransportOutcome.Failed, result.Transport);
            Assert.Null(result.Task);
            Assert.Null(result.AnswerText);
            Assert.Equal(OutputCompleteness.None, result.AnswerCompleteness);
            Assert.Equal(WorkIqFailureCategory.Transport, result.Failure!.Category);
            Assert.Equal(OutcomeCertainty.Indeterminate, result.Failure.Certainty);
            Assert.Equal(result.Identity.RpcRequestId, result.Failure.RpcRequestId);
            Assert.Equal(1, h.Handler.CountOf("SendMessage"));
            Assert.Null(conversation.ContextId);
            Assert.False(conversation.IsTurnActive);
            Assert.Single(h.Events("send.failed"));
            Assert.Contains(h.Issues.Issues, i => i.Code == "turn.indeterminate");
        }
    }

    [Fact]
    public async Task RunTurn_HttpErrorOnSend_FailedWithKnownState()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.FailWith("SendMessage", HttpStatusCode.Forbidden);

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(WorkIqTurnOutcome.Failed, result.Outcome);
            Assert.Equal(TransportOutcome.Failed, result.Transport);
            Assert.Equal(WorkIqFailureCategory.Forbidden, result.Failure!.Category);
            Assert.Equal(403, result.Failure.HttpStatus);
            Assert.Equal(1, h.Handler.CountOf("SendMessage"));
        }
    }

    [Fact]
    public async Task RunTurn_UntrustedCard_FailedWithoutAnyRpc()
    {
        var (h, conversation) = Real(withCard: false);
        using (h)
        await using (conversation)
        {
            h.Handler.OnCard("cards/card-untrusted-host.json").OnRpc("SendMessage", "envelopes/send-direct-message.json");

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(WorkIqTurnOutcome.Failed, result.Outcome);
            Assert.Equal(WorkIqFailureCategory.UntrustedHost, result.Failure!.Category);
            Assert.Equal(0, h.Handler.CountOf("SendMessage"));
            Assert.Null(conversation.Card);
            Assert.Single(h.Events("discovery.turn_blocked"));
        }
    }

    [Fact]
    public async Task RunTurn_V03ShapedReply_FailedProtocolVersionMismatch()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/v03-shaped-response.json");

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(WorkIqTurnOutcome.Failed, result.Outcome);
            Assert.Equal(WorkIqFailureCategory.ProtocolVersionMismatch, result.Failure!.Category);
            Assert.Null(result.AnswerText);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Guard, reset, abandon, local stops
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunTurn_Concurrent_SecondCallThrowsImmediately()
    {
        var (client, conversation, _, _, _) = Fake();
        await using (conversation)
        {
            client.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = conversation.RunTurnAsync(Turn(caseId: "first"));
            await Task.Yield();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.RunTurnAsync(Turn(caseId: "second")));

            Assert.Contains("one active turn per conversation", ex.Message, StringComparison.Ordinal);
            Assert.True(conversation.IsTurnActive);
            Assert.Single(client.Sends);

            client.Gate.SetResult();
            var result = await first;
            Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
            Assert.False(conversation.IsTurnActive);
        }
    }

    [Fact]
    public async Task ResetSession_WhileActive_ThrowsAndAfterwardsClearsContext()
    {
        var (client, conversation, trace, _, _) = Fake();
        await using (conversation)
        {
            client.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = conversation.RunTurnAsync(Turn());
            await Task.Yield();

            await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.ResetSessionAsync());

            client.Gate.SetResult();
            await pending;
            Assert.Equal("ctx-fake-1", conversation.ContextId);

            await conversation.ResetSessionAsync();

            Assert.Null(conversation.ContextId);
            Assert.Null(conversation.ActiveTask);
            Assert.Contains(trace.Events, e => e.Code == "turn.context_reset" && e.ContextId == "ctx-fake-1");
            Assert.Equal(0, client.CancelCalls);
        }
    }

    [Fact]
    public async Task AbandonAndReset_StopsTheTurnNeverSendsCancelTask()
    {
        var (client, conversation, trace, _, _) = Fake();
        await using (conversation)
        {
            client.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = conversation.RunTurnAsync(Turn());
            await Task.Yield();
            Assert.True(conversation.IsTurnActive);

            var receipt = await conversation.AbandonAndResetAsync("tester gave up");

            Assert.True(receipt.Quiesced);
            Assert.False(receipt.RemoteCancellationConfirmed);
            Assert.Equal("tester gave up", receipt.Reason);
            Assert.Null(receipt.PriorContextId);
            Assert.Null(receipt.PriorTask);
            Assert.Equal(0, client.CancelCalls);
            Assert.False(conversation.IsTurnActive);
            Assert.Null(conversation.ContextId);

            var ex = await Assert.ThrowsAsync<WorkIqTurnCanceledException>(() => pending);
            Assert.Equal(WorkIqTurnOutcome.Indeterminate, ex.TurnResult.Outcome);
            Assert.Equal(WorkIqFailureCategory.Canceled, ex.TurnResult.Failure!.Category);
            Assert.Contains("abandoned", ex.TurnResult.Failure.SafeMessage, StringComparison.Ordinal);
            Assert.Contains(trace.Events, e => e.Code == "turn.abandoned");

            client.Gate = null;
            var next = await conversation.RunTurnAsync(Turn(caseId: "after"));
            Assert.Equal(WorkIqTurnOutcome.Completed, next.Outcome);
        }
    }

    [Fact]
    public async Task AbandonAndReset_NothingActive_ClearsContextWithReceipt()
    {
        var (client, conversation, _, _, _) = Fake();
        await using (conversation)
        {
            var result = await conversation.RunTurnAsync(Turn());
            Assert.Equal("ctx-fake-1", conversation.ContextId);

            var receipt = await conversation.AbandonAndResetAsync("fresh start");

            Assert.True(receipt.Quiesced);
            Assert.Equal("ctx-fake-1", receipt.PriorContextId);
            Assert.Equal(WorkIqTurnOutcome.Completed, receipt.LastOutcome);
            Assert.NotNull(receipt.LastCapture);
            Assert.Null(conversation.ContextId);
            Assert.Equal(0, client.CancelCalls);
            _ = result;
        }
    }

    [Fact]
    public async Task RunTurn_CallerCanceled_ThrowsCanceledExceptionCarryingIndeterminateResult()
    {
        var (client, conversation, _, _, _) = Fake();
        await using (conversation)
        {
            client.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource();
            var pending = conversation.RunTurnAsync(Turn(), cts.Token);
            await Task.Yield();

            cts.Cancel();
            var ex = await Assert.ThrowsAsync<WorkIqTurnCanceledException>(() => pending);

            Assert.IsAssignableFrom<OperationCanceledException>(ex);
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.Equal(WorkIqTurnOutcome.Indeterminate, ex.TurnResult.Outcome);
            Assert.Equal(TransportOutcome.Failed, ex.TurnResult.Transport);
            Assert.Equal(WorkIqFailureCategory.Canceled, ex.TurnResult.Failure!.Category);
            Assert.Equal(OutcomeCertainty.Indeterminate, ex.TurnResult.Failure.Certainty);
            Assert.Null(ex.TurnResult.Task);
            Assert.False(conversation.IsTurnActive);
            Assert.Equal(0, client.CancelCalls);
        }
    }

    [Fact]
    public async Task RunTurn_TurnDeadline_IndeterminateTimeoutWithoutThrow()
    {
        var (client, conversation, _, _, clock) = Fake(new WorkIqOptions { TurnDeadline = TimeSpan.FromSeconds(5) });
        await using (conversation)
        {
            client.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var result = await WorkIqClientHarness.PumpAsync(conversation.RunTurnAsync(Turn()), clock, Step);

            Assert.Equal(WorkIqTurnOutcome.Indeterminate, result.Outcome);
            Assert.Equal(WorkIqFailureCategory.Timeout, result.Failure!.Category);
            Assert.Contains("turn deadline", result.Failure.SafeMessage, StringComparison.Ordinal);
            Assert.Equal(TransportOutcome.Failed, result.Transport);
            Assert.Single(client.Sends);
            Assert.Equal(0, client.CancelCalls);
        }
    }

    [Fact]
    public async Task RunTurn_RecoveryBudgetExhausted_IndeterminateTimeoutTaskStillAddressable()
    {
        var options = new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromSeconds(1), RecoveryBudget = TimeSpan.FromSeconds(3) };
        var (h, conversation) = Real(options);
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json").OnRpc("GetTask", "envelopes/gettask-working.json");

            var result = await PumpRecoveryAsync(conversation.RunTurnAsync(Turn()), h, Step);

            Assert.Equal(WorkIqTurnOutcome.Indeterminate, result.Outcome);
            Assert.Equal(TransportOutcome.Interrupted, result.Transport);
            Assert.Equal(WorkIqFailureCategory.Timeout, result.Failure!.Category);
            Assert.Equal("TASK_STATE_WORKING", result.Failure.RemoteTaskState);
            Assert.Equal(OutcomeCertainty.Indeterminate, result.Failure.Certainty);
            Assert.True(result.Timing.RecoveryRequests >= 1);
            Assert.Equal(result.Timing.RecoveryRequests, h.Handler.CountOf("GetTask"));
            Assert.Equal(1, h.Handler.CountOf("SendMessage"));
            Assert.Equal(0, h.Handler.CountOf("CancelTask"));
            Assert.Equal(SubmittedTaskId, conversation.ActiveTask?.TaskId);
            Assert.Contains(h.Issues.Issues, i => i.Code == "budget.recovery_exhausted" && i.Category == WorkIqIssueCategory.Budget);
            Assert.Single(h.Events("budget.exceeded"));
        }
    }

    [Fact]
    public async Task RunTurn_RecoveryBudgetExhausted_CancelRemoteOptIn_SendsCancelTaskAndMapsConfirmedState()
    {
        var options = new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromSeconds(1), RecoveryBudget = TimeSpan.FromSeconds(2), CancelRemoteTaskOnTimeout = true };
        var (h, conversation) = Real(options);
        using (h)
        await using (conversation)
        {
            h.Handler
                .OnRpc("SendMessage", "envelopes/send-task-submitted.json")
                .OnRpc("GetTask", "envelopes/gettask-working.json")
                .OnRpc("CancelTask", "envelopes/canceltask-completed-race.json");

            var result = await PumpRecoveryAsync(conversation.RunTurnAsync(Turn()), h, Step);

            Assert.Equal(1, h.Handler.CountOf("CancelTask"));
            Assert.Equal(WorkIqTurnOutcome.Completed, result.Outcome);
            Assert.Single(h.Events("cancel.not_confirmed"));
            Assert.Null(conversation.ConfirmedCanceledTaskId);

            // Review L5: the task finished on its own, so the register carries a Warning that says so — never the Error
            // "budget.recovery_exhausted" for a turn that completed.
            Assert.Single(h.Events("budget.recovered_after_cancel"));
            var recovered = Assert.Single(h.Issues.Issues, i => i.Code == "budget.recovered_after_cancel");
            Assert.Equal(WorkIqIssueCategory.Budget, recovered.Category);
            Assert.Equal("TASK_STATE_COMPLETED", recovered.A2AState);
            Assert.DoesNotContain(h.Issues.Issues, i => i.Code == "budget.recovery_exhausted");
            Assert.Contains(h.Events("issue.budget"), e => e.Level == WorkIqTraceLevel.Warning);
        }
    }

    [Fact]
    public async Task RunTurn_RecoveryBudgetExhausted_CancelRemoteConfirmed_CanceledAndConfirmed()
    {
        var options = new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromSeconds(1), RecoveryBudget = TimeSpan.FromSeconds(2), CancelRemoteTaskOnTimeout = true };
        var (h, conversation) = Real(options);
        using (h)
        await using (conversation)
        {
            var canceled = FixtureLoader.Text("envelopes/gettask-working.json").Replace("TASK_STATE_WORKING", "TASK_STATE_CANCELED", StringComparison.Ordinal);
            h.Handler
                .OnRpc("SendMessage", "envelopes/send-task-submitted.json")
                .OnRpc("GetTask", "envelopes/gettask-working.json")
                .OnRpc("CancelTask", canceled);

            var result = await PumpRecoveryAsync(conversation.RunTurnAsync(Turn()), h, Step);

            Assert.Equal(1, h.Handler.CountOf("CancelTask"));
            Assert.Equal(WorkIqTurnOutcome.Canceled, result.Outcome);
            Assert.Null(result.AnswerText);
            Assert.Single(h.Events("cancel.confirmed"));
            Assert.Equal(SubmittedTaskId, conversation.ConfirmedCanceledTaskId);
            // The budget ran out and the task was canceled because of it: that Error stays in the register.
            Assert.Contains(h.Issues.Issues, i => i.Code == "budget.recovery_exhausted");
            Assert.DoesNotContain(h.Issues.Issues, i => i.Code == "budget.recovered_after_cancel");
        }
    }

    [Fact]
    public async Task RunTurn_RecoveryBudgetExhausted_CancelRemoteNotConfirmed_TimeoutStandsAndBudgetIssueKept()
    {
        var options = new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromSeconds(1), RecoveryBudget = TimeSpan.FromSeconds(2), CancelRemoteTaskOnTimeout = true };
        var (h, conversation) = Real(options);
        using (h)
        await using (conversation)
        {
            h.Handler
                .OnRpc("SendMessage", "envelopes/send-task-submitted.json")
                .OnRpc("GetTask", "envelopes/gettask-working.json")
                .OnRpc("CancelTask", "envelopes/gettask-working.json");

            var result = await PumpRecoveryAsync(conversation.RunTurnAsync(Turn()), h, Step);

            Assert.Equal(1, h.Handler.CountOf("CancelTask"));
            Assert.Equal(WorkIqTurnOutcome.Indeterminate, result.Outcome);
            Assert.Equal(WorkIqFailureCategory.Timeout, result.Failure!.Category);
            Assert.Single(h.Events("cancel.not_confirmed"));
            Assert.Null(conversation.ConfirmedCanceledTaskId);
            Assert.Contains(h.Issues.Issues, i => i.Code == "budget.recovery_exhausted" && i.A2AState == "TASK_STATE_WORKING");
            Assert.DoesNotContain(h.Issues.Issues, i => i.Code == "budget.recovered_after_cancel");
        }
    }

    [Fact]
    public async Task RunTurn_RecoveryBudgetExhausted_CancelTaskFails_TimeoutStands()
    {
        var options = new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromSeconds(1), RecoveryBudget = TimeSpan.FromSeconds(2), CancelRemoteTaskOnTimeout = true };
        var (h, conversation) = Real(options);
        using (h)
        await using (conversation)
        {
            h.Handler
                .OnRpc("SendMessage", "envelopes/send-task-submitted.json")
                .OnRpc("GetTask", "envelopes/gettask-working.json")
                .FailWith("CancelTask", HttpStatusCode.InternalServerError);

            var result = await PumpRecoveryAsync(conversation.RunTurnAsync(Turn()), h, Step);

            Assert.Equal(WorkIqTurnOutcome.Indeterminate, result.Outcome);
            Assert.Equal(WorkIqFailureCategory.Timeout, result.Failure!.Category);
            Assert.Single(h.Events("cancel.failed"));
            Assert.Contains(h.Issues.Issues, i => i.Code == "budget.recovery_exhausted");
        }
    }

    [Fact]
    public async Task AbandonAndReset_ZeroRecoveryBudget_NotQuiescedIsReportedAndTheTurnStillEndsCanceled()
    {
        var (client, conversation, trace, issues, _) = Fake(new WorkIqOptions { RecoveryBudget = TimeSpan.Zero });
        await using (conversation)
        {
            client.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = conversation.RunTurnAsync(Turn());
            await Task.Yield();
            Assert.True(conversation.IsTurnActive);

            var receipt = await conversation.AbandonAndResetAsync("no time to wait");

            Assert.False(receipt.Quiesced);
            Assert.Contains(trace.Events, e => e.Code == "turn.abandon_not_quiesced" && e.Level == WorkIqTraceLevel.Error);
            Assert.Contains(issues.Issues, i => i.Code == "turn.abandon_not_quiesced");
            Assert.Null(conversation.ContextId);
            var ex = await Assert.ThrowsAsync<WorkIqTurnCanceledException>(() => pending);
            Assert.Contains("abandoned", ex.TurnResult.Failure!.SafeMessage, StringComparison.Ordinal);
            Assert.Equal(0, client.CancelCalls);
        }
    }

    [Fact]
    public async Task StreamTurnAsync_HappyPath_YieldsTheEventSequenceAndACompletedResult()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnSse("SendStreamingMessage", "sse/stream-happy-path.txt");

            var events = new List<WorkIqStreamEvent>();
            await foreach (var e in conversation.StreamTurnAsync(Turn(turnId: "turn-stream-1")))
            {
                events.Add(e);
            }

            Assert.IsType<WorkIqStreamEvent.TaskSnapshotObserved>(events[0]);
            Assert.Equal(2, events.OfType<WorkIqStreamEvent.ArtifactRevisionObserved>().Count());
            Assert.Contains(events, e => e is WorkIqStreamEvent.ProgressObserved);
            var statuses = events.OfType<WorkIqStreamEvent.TaskStatusObserved>().Select(s => s.RawState).ToArray();
            Assert.Equal(new[] { "TASK_STATE_WORKING", "TASK_STATE_COMPLETED" }, statuses);
            Assert.IsType<WorkIqStreamEvent.EvidenceObserved>(events[^2]);
            var finished = Assert.IsType<WorkIqStreamEvent.TurnFinished>(events[^1]);
            Assert.Equal(WorkIqTurnOutcome.Completed, finished.Result.Outcome);
            Assert.StartsWith("Project Atlas is on schedule", finished.Result.AnswerText, StringComparison.Ordinal);
            Assert.All(events, e => Assert.Equal("turn-stream-1", e.TurnId));
            Assert.Equal(events.Select(e => e.Sequence).OrderBy(s => s), events.Select(e => e.Sequence));
            Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());
            Assert.Equal(1, h.Handler.CountOf("SendStreamingMessage"));
            Assert.Equal(0, h.Handler.CountOf("SendMessage"));
            Assert.False(conversation.IsTurnActive);
        }
    }

    [Fact]
    public async Task SubscribeAsync_StreamEndsWithoutAFirstSnapshot_MalformedNeverCompleted()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");
            var first = await conversation.RunTurnAsync(Turn());
            h.Handler.OnSse("SubscribeToTask", ": keep-alive comment only, no frame\n\n");

            var events = new List<WorkIqStreamEvent>();
            await foreach (var e in conversation.SubscribeAsync(first.Task!))
            {
                events.Add(e);
            }

            var finished = Assert.IsType<WorkIqStreamEvent.TurnFinished>(events[^1]);
            Assert.NotEqual(WorkIqTurnOutcome.Completed, finished.Result.Outcome);
            Assert.Equal(WorkIqFailureCategory.Malformed, finished.Result.Failure!.Category);
            Assert.Contains("without a first task snapshot", finished.Result.Failure.SafeMessage, StringComparison.Ordinal);
            Assert.Null(finished.Result.AnswerText);
            Assert.Equal(1, h.Handler.CountOf("SubscribeToTask"));
            Assert.Equal(1, h.Handler.CountOf("SendMessage"));   // a subscription never resubmits the input
        }
    }

    [Fact]
    public async Task RunTurn_Completed_NormaliserTracesOnTheConversationsDiagnostics()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");

            await conversation.RunTurnAsync(Turn(caseId: "atlas-norm"));

            // The normaliser's own events reach the run's trace (they were dropped before: it ran without diagnostics).
            Assert.Single(h.Events("normalize.start"));
            Assert.Single(h.Events("normalize.end"));
            Assert.Contains(h.Events("normalize.answer"), e => e.CaseId == "atlas-norm");
            Assert.Single(h.Events("normalize.citations"));
            Assert.Single(h.Events("normalize.channels"));
        }
    }

    [Fact]
    public async Task RunTurn_GetTaskNotFoundDuringRecovery_IndeterminateTaskUnavailable()
    {
        var options = new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromSeconds(1), RecoveryBudget = TimeSpan.FromMinutes(1) };
        var (h, conversation) = Real(options);
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-submitted.json").OnRpc("GetTask", "envelopes/canceltask-not-found-error.json");

            var result = await PumpRecoveryAsync(conversation.RunTurnAsync(Turn()), h, Step);

            Assert.Equal(WorkIqTurnOutcome.Indeterminate, result.Outcome);
            Assert.Equal(WorkIqFailureCategory.NotFound, result.Failure!.Category);
            Assert.Equal(OutcomeCertainty.Indeterminate, result.Failure.Certainty);
            Assert.Contains("TaskUnavailable", result.Failure.SafeMessage, StringComparison.Ordinal);
            Assert.Equal(1, result.Timing.RecoveryRequests);
            Assert.Equal(1, h.Handler.CountOf("SendMessage"));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Request shaping
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-05T12:00:00Z", 60)]
    [InlineData("2026-07-05T12:00:00Z", 120)]
    public async Task RunTurn_TimeZoneConfigured_LocationMetadataIsDstAware(string instant, int expectedOffset)
    {
        Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Zurich", out _), "This machine must resolve IANA time zone ids for the Location metadata to work.");
        var options = new WorkIqOptions { EvaluationReferenceInstant = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture) };
        var (h, conversation) = Real(options, WorkIqClientHarness.DefaultConfig(timeZone: "Europe/Zurich"));
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json");

            await conversation.RunTurnAsync(Turn());

            var metadata = Message(Posts(h, "SendMessage")[0])["metadata"]!["Location"]!;
            Assert.Equal("Europe/Zurich", metadata["timeZone"]!.GetValue<string>());
            Assert.Equal(expectedOffset, metadata["timeZoneOffset"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task RunTurn_NoTimeZone_NoMetadata()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-direct-message.json");

            await conversation.RunTurnAsync(Turn());

            Assert.False(Message(Posts(h, "SendMessage")[0]).ContainsKey("metadata"));
        }
    }

    [Fact]
    public async Task RunTurn_Native118Profile_TelemetryPartialWhenDiagnosticsArriveAndModesRequested()
    {
        var (h, conversation) = Real(new WorkIqOptions { ProfileOverride = DiagnosticProfile.Native118Diagnostics });
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-developer-and-retrieval-info.json");

            var result = await conversation.RunTurnAsync(Turn());

            Assert.Equal(TelemetryCompleteness.Partial, result.Telemetry);
            var modes = JsonNode.Parse(Posts(h, "SendMessage")[0].BodyJson!)!["params"]!["configuration"]!["acceptedOutputModes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.Contains(WorkIqA2AClient.DeveloperInfoMediaType, modes);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Explicit reads, continuation, disposal
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetTaskAsync_ValidHandle_ReturnsSnapshot()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("GetTask", "envelopes/gettask-completed.json");
            var handle = new WorkIqTaskHandle(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, SubmittedTaskId, SharedContextId, null, conversation.PrincipalKey);

            var snapshot = await conversation.GetTaskAsync(handle);

            Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
            Assert.Equal(SubmittedTaskId, snapshot.Handle.TaskId);
            Assert.Equal(conversation.PrincipalKey, snapshot.Handle.PrincipalKey);
            Assert.Equal(WorkIqSnapshotSource.GetTask, snapshot.Source);
            Assert.NotNull(snapshot.Capture);
            Assert.Single(snapshot.Artifacts);
            Assert.Equal(1, h.Handler.CardRequestCount);
        }
    }

    [Fact]
    public async Task GetTaskAsync_ForeignHandle_Rejected()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("GetTask", "envelopes/gettask-completed.json");
            var otherEndpoint = new WorkIqTaskHandle(new Uri("https://workiq.svc.cloud.microsoft/a2a/U_x.y/"), "1.0", "U_x.y", SubmittedTaskId, null, null, conversation.PrincipalKey);
            var otherPrincipal = new WorkIqTaskHandle(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, SubmittedTaskId, null, null, "someone-else");

            await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.GetTaskAsync(otherEndpoint));
            await Assert.ThrowsAsync<InvalidOperationException>(() => conversation.CancelTaskAsync(otherPrincipal));

            Assert.Equal(0, h.Handler.CountOf("GetTask"));
            Assert.Equal(0, h.Handler.CountOf("CancelTask"));
        }
    }

    [Fact]
    public async Task CancelTaskAsync_CompletedRace_SnapshotNotConfirmed()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("CancelTask", "envelopes/canceltask-completed-race.json");
            var handle = new WorkIqTaskHandle(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, "task-7b3f9d1e-6a2c-4e5b-8f0a-1d4c7b9e3a62", null, null, conversation.PrincipalKey);

            var snapshot = await conversation.CancelTaskAsync(handle);

            Assert.Equal(WorkIqTaskState.Completed, snapshot.State);
            Assert.Null(conversation.ConfirmedCanceledTaskId);
            Assert.Single(h.Events("cancel.not_confirmed"));
        }
    }

    [Fact]
    public async Task CancelTaskAsync_NotFound_SurfacesProtocolException()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("CancelTask", "envelopes/canceltask-not-found-error.json");
            var handle = new WorkIqTaskHandle(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, "task-00000000-0000-4000-8000-000000000000", null, null, conversation.PrincipalKey);

            var ex = await Assert.ThrowsAsync<WorkIqProtocolException>(() => conversation.CancelTaskAsync(handle));

            Assert.Equal(WorkIqFailureCategory.NotFound, ex.Failure.Category);
        }
    }

    [Fact]
    public async Task ContinueAsync_NotSupportedYet()
    {
        var (_, conversation, _, _, _) = Fake();
        await using (conversation)
        {
            var handle = new WorkIqTaskHandle(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), "1.0", null, "task-1", null, null, conversation.PrincipalKey);
            var pending = new WorkIqPendingInteraction(handle, WorkIqInteractionKind.InputRequired, "req-1", null, [], [], WorkIqConversation.InteractionProfileId, null, []);
            var response = WorkIqInteractionResponse.ForInput(pending, "yes");

            var ex = await Assert.ThrowsAsync<NotSupportedException>(() => conversation.ContinueAsync(pending, response));

            Assert.Contains("TODO(M53)", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisposeAsync_IdempotentAndRejectsFurtherTurns()
    {
        var (_, conversation, _, _, _) = Fake();

        await conversation.DisposeAsync();
        await conversation.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => conversation.RunTurnAsync(Turn()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => conversation.ResetSessionAsync());
        Assert.False(conversation.IsTurnActive);
    }

    [Fact]
    public void PrincipalKey_DerivedFromTenantAppScopeOnly()
    {
        var (_, a, _, _, _) = Fake();
        var (_, b, _, _, _) = Fake();
        var other = new WorkIqConversation(new FakeWorkIqA2AClient(), WorkIqClientHarness.DefaultConfig() with { TenantId = "33333333-3333-3333-3333-333333333333" }, new WorkIqOptions(), TimeProvider.System);

        Assert.Equal(a.PrincipalKey, b.PrincipalKey);
        Assert.NotEqual(a.PrincipalKey, other.PrincipalKey);
        Assert.Equal(64, a.PrincipalKey.Length);
    }

    [Fact]
    public void Constructor_ValidatesInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkIqConversation(null!, WorkIqClientHarness.DefaultConfig(), new WorkIqOptions(), TimeProvider.System));
        Assert.Throws<InvalidOperationException>(() => new WorkIqConversation(new FakeWorkIqA2AClient(), new Microsoft365Config(), new WorkIqOptions(), TimeProvider.System));
        Assert.Throws<InvalidOperationException>(() => new WorkIqConversation(new FakeWorkIqA2AClient(), WorkIqClientHarness.DefaultConfig(), new WorkIqOptions { RecoveryBudget = TimeSpan.FromSeconds(-1) }, TimeProvider.System));
    }

    [Fact]
    public async Task RunTurn_NoSecretInDiagnostics()
    {
        var (h, conversation) = Real();
        using (h)
        await using (conversation)
        {
            h.Handler.OnRpc("SendMessage", "envelopes/send-task-completed-with-answer-and-attributions.json");

            await conversation.RunTurnAsync(Turn());

            h.AssertNoSecretInDiagnostics();
        }
    }
}
