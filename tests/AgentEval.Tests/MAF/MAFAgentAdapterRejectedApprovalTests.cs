// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Tests.TestHelpers;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.MAF;

/// <summary>
/// <see cref="MAFAgentAdapter.InvokeStreamingAsync"/> maps <c>FunctionCallContent</c> to
/// <see cref="AgentResponseChunk.ToolCallStarted"/> and <c>FunctionResultContent</c> to
/// <see cref="AgentResponseChunk.ToolCallCompleted"/>, which the streaming harness and recorders read as a tool that ran.
/// On MAF 1.25+ (#9152) the run after an approval request that nobody answered streams the call MAF re-created for the
/// rejected request and a "rejected" result. That tool never ran, so neither may be reported.
/// </summary>
public class MAFAgentAdapterRejectedApprovalTests
{
    private static async Task<List<AgentResponseChunk>> CollectAsync(IAsyncEnumerable<AgentResponseChunk> stream)
    {
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in stream)
            chunks.Add(chunk);
        return chunks;
    }

    // Fails on HEAD 143696a9: the re-created call was streamed as ToolCallStarted and its "rejected" result as
    // ToolCallCompleted, a successful completion of a tool that never ran. Scripted updates in the MAF 1.27 shape.
    [Fact]
    public async Task Streaming_RejectedApprovalCall_IsNeitherStartedNorCompleted()
    {
        var adapter = new MAFAgentAdapter(new ScriptedUpdatesAgent(UnansweredApprovalRun.Updates()));

        var chunks = await CollectAsync(adapter.InvokeStreamingAsync(UnansweredApprovalRun.NewText));

        Assert.DoesNotContain(chunks, c => c.ToolCallStarted is not null);
        Assert.DoesNotContain(chunks, c => c.ToolCallCompleted is not null);
        Assert.Equal(UnansweredApprovalRun.Answer, string.Concat(chunks.Select(c => c.Text)));
        Assert.True(chunks[^1].IsComplete);
    }

    // Guard (passes on HEAD too): an approved decision followed by the call and its real result streams as before.
    [Fact]
    public async Task Streaming_ApprovedApprovalCall_IsStartedAndCompleted()
    {
        var adapter = new MAFAgentAdapter(new ScriptedUpdatesAgent(UnansweredApprovalRun.Updates(approved: true)));

        var chunks = await CollectAsync(adapter.InvokeStreamingAsync(UnansweredApprovalRun.NewText));

        var started = Assert.Single(chunks, c => c.ToolCallStarted is not null).ToolCallStarted!;
        Assert.Equal(UnansweredApprovalRun.CallId, started.CallId);
        var completed = Assert.Single(chunks, c => c.ToolCallCompleted is not null).ToolCallCompleted!;
        Assert.Equal(UnansweredApprovalRun.ApprovedResult, completed.Result);
    }

    // Fails on HEAD 143696a9. Pins MAF #9152 (1.25+) on the streaming path: a real MAF pipeline (scripted model,
    // offline). The raw stream of one pipeline shows that MAF streams the rejection, the re-created call and its result;
    // an identical pipeline behind MAFAgentAdapter, recorded by TraceRecordingAgent, reports no tool call for it.
    [Fact]
    public async Task RealMafPipeline_UnansweredApprovalThenNewText_StreamsNoToolCall()
    {
        var (raw, _) = UnansweredApprovalRun.BuildApprovalGatedAgent();
        var session = await raw.CreateSessionAsync();
        await foreach (var _ in raw.RunStreamingAsync(UnansweredApprovalRun.Pause, session)) { }
        var rawSecondRun = new List<AIContent>();
        await foreach (var update in raw.RunStreamingAsync(UnansweredApprovalRun.NewText, session))
            rawSecondRun.AddRange(update.Contents);
        UnansweredApprovalRun.AssertMafAutoRejected(rawSecondRun);

        var (agent, executed) = UnansweredApprovalRun.BuildApprovalGatedAgent();
        await using var recorder = new TraceRecordingAgent(new MAFAgentAdapter(agent), "unanswered_approval_streaming");
        await CollectAsync(recorder.InvokeStreamingAsync(UnansweredApprovalRun.Pause));
        var chunks = await CollectAsync(recorder.InvokeStreamingAsync(UnansweredApprovalRun.NewText));

        Assert.Equal(0, executed());
        Assert.DoesNotContain(chunks, c => c.ToolCallStarted?.CallId == UnansweredApprovalRun.CallId);
        Assert.DoesNotContain(chunks, c => c.ToolCallCompleted?.CallId == UnansweredApprovalRun.CallId);
        var entry = recorder.Trace.Entries.Last(e => e.Type == TraceEntryType.Response);
        Assert.DoesNotContain(entry.ToolCalls ?? [], c => c.Name == UnansweredApprovalRun.ToolName && c.Succeeded);
    }
}
