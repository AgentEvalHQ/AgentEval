// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Tests.TestHelpers;
using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// <see cref="TraceRecordingAgent"/> records the tool calls the agent returned, each paired with its result. On MAF
/// 1.25+ (#9152) the run after an approval request that nobody answered returns the call MAF re-created for the rejected
/// request, with a "rejected" result. The trace leaves that call out on both paths: the tool never ran and the model did
/// not request it in that run.
/// </summary>
public class TraceRecordingAgentRejectedApprovalTests
{
    // Fails on HEAD 143696a9: the re-created call was paired with its "rejected" result and recorded with
    // Succeeded = true. Synthetic response in the MAF 1.27 shape.
    [Fact]
    public async Task NonStreaming_RejectedApprovalCall_IsNotRecorded()
    {
        await using var recorder = new TraceRecordingAgent(new FixedAgent(UnansweredApprovalRun.Response()), "rejected_approval");

        await recorder.InvokeAsync(UnansweredApprovalRun.NewText);

        var entry = recorder.Trace.Entries.Single(e => e.Type == TraceEntryType.Response);
        Assert.Null(entry.ToolCalls);
    }

    // Guard (passes on HEAD too): an approved decision followed by the call and its real result is a succeeded call,
    // recorded as before (no execution flag).
    [Fact]
    public async Task NonStreaming_ApprovedApprovalCall_IsRecordedAsSucceeded()
    {
        await using var recorder = new TraceRecordingAgent(
            new FixedAgent(UnansweredApprovalRun.Response(approved: true)), "approved_approval");

        await recorder.InvokeAsync(UnansweredApprovalRun.NewText);

        var call = Assert.Single(recorder.Trace.Entries.Single(e => e.Type == TraceEntryType.Response).ToolCalls!);
        Assert.Equal(UnansweredApprovalRun.ApprovedResult, call.Result);
        Assert.True(call.Succeeded);
        Assert.Null(call.WasExecuted);
    }

    // Fails on HEAD 143696a9. Pins MAF #9152 (1.25+): a real MAF pipeline (scripted model, offline) behind
    // MAFAgentAdapter, which reuses one session. The second run's trace has no call for the rejected request.
    [Fact]
    public async Task RealMafPipeline_UnansweredApprovalThenNewText_RecordsNoCallForIt()
    {
        var (agent, executed) = UnansweredApprovalRun.BuildApprovalGatedAgent();
        await using var recorder = new TraceRecordingAgent(new MAFAgentAdapter(agent), "unanswered_approval");

        await recorder.InvokeAsync(UnansweredApprovalRun.Pause);
        var second = await recorder.InvokeAsync(UnansweredApprovalRun.NewText);

        UnansweredApprovalRun.AssertMafAutoRejected(UnansweredApprovalRun.ContentsOf(second));
        Assert.Equal(0, executed());
        var entry = recorder.Trace.Entries.Last(e => e.Type == TraceEntryType.Response);
        Assert.DoesNotContain(entry.ToolCalls ?? [], c => c.Name == UnansweredApprovalRun.ToolName);
    }

    // Fails on HEAD 143696a9: both traces held the re-created call (non-streamed with Succeeded = true, streamed as a
    // completed call). Pins MAF #9152 (1.25+): two identical real MAF pipelines (scripted model, offline) behind
    // MAFAgentAdapter, one streamed and one not, record the same tool calls for the second run: none.
    [Fact]
    public async Task RealMafPipeline_StreamedAndNonStreamedTraces_RecordTheSameToolCalls()
    {
        var (plainAgent, plainExecuted) = UnansweredApprovalRun.BuildApprovalGatedAgent();
        var (streamAgent, streamExecuted) = UnansweredApprovalRun.BuildApprovalGatedAgent();
        await using var plain = new TraceRecordingAgent(new MAFAgentAdapter(plainAgent), "plain");
        await using var streamed = new TraceRecordingAgent(new MAFAgentAdapter(streamAgent), "streamed");

        await plain.InvokeAsync(UnansweredApprovalRun.Pause);
        await plain.InvokeAsync(UnansweredApprovalRun.NewText);
        await foreach (var _ in streamed.InvokeStreamingAsync(UnansweredApprovalRun.Pause)) { }
        await foreach (var _ in streamed.InvokeStreamingAsync(UnansweredApprovalRun.NewText)) { }

        Assert.Equal(0, plainExecuted());
        Assert.Equal(0, streamExecuted());
        var plainCalls = plain.Trace.Entries.Last(e => e.Type == TraceEntryType.Response).ToolCalls ?? [];
        var streamedCalls = streamed.Trace.Entries.Last(e => e.Type == TraceEntryType.Response).ToolCalls ?? [];
        Assert.Empty(plainCalls);
        Assert.Empty(streamedCalls);
    }

    private sealed class FixedAgent(AgentResponse response) : IEvaluableAgent
    {
        public string Name => "FixedAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(response);
    }
}
