// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Tests.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// <see cref="ChatClientAgentAdapter.InvokeStreamingAsync"/> maps function calls and results to tool chunks the same way
/// MAFAgentAdapter does, so a call re-created for a rejected approval decision (MAF 1.25+ #9152) must not be reported
/// as started or completed when a chat client streams one.
/// </summary>
public class ChatClientAgentAdapterRejectedApprovalTests
{
    private static async Task<List<AgentResponseChunk>> CollectAsync(ChatClientAgentAdapter adapter)
    {
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in adapter.InvokeStreamingAsync(UnansweredApprovalRun.NewText))
            chunks.Add(chunk);
        return chunks;
    }

    // Fails on HEAD 143696a9: the re-created call was streamed as ToolCallStarted and its "rejected" result as
    // ToolCallCompleted. Scripted chat client streaming the contents of the MAF 1.27 shape.
    [Fact]
    public async Task Streaming_RejectedApprovalCall_IsNeitherStartedNorCompleted()
    {
        var adapter = new ChatClientAgentAdapter(new MockStreamingChatClient(UnansweredApprovalRun.Contents()), "TestAgent");

        var chunks = await CollectAsync(adapter);

        Assert.DoesNotContain(chunks, c => c.ToolCallStarted is not null);
        Assert.DoesNotContain(chunks, c => c.ToolCallCompleted is not null);
        Assert.Equal(UnansweredApprovalRun.Answer, string.Concat(chunks.Select(c => c.Text)));
    }

    // Guard (passes on HEAD too): an approved decision followed by the call and its real result streams as before.
    [Fact]
    public async Task Streaming_ApprovedApprovalCall_IsStartedAndCompleted()
    {
        var adapter = new ChatClientAgentAdapter(
            new MockStreamingChatClient(UnansweredApprovalRun.Contents(approved: true)), "TestAgent");

        var chunks = await CollectAsync(adapter);

        Assert.Equal(UnansweredApprovalRun.CallId, Assert.Single(chunks, c => c.ToolCallStarted is not null).ToolCallStarted!.CallId);
        Assert.Equal(UnansweredApprovalRun.ApprovedResult, Assert.Single(chunks, c => c.ToolCallCompleted is not null).ToolCallCompleted!.Result);
    }
}
