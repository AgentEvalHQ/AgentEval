// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentResponse = AgentEval.Core.AgentResponse;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.TestHelpers;

/// <summary>
/// The run that MAF 1.25+ (#9152) returns after an approval request that nobody answered. When the session runs again,
/// MAF rejects the saved request and the response holds, in order: the generated rejection (a
/// <see cref="ToolApprovalResponseContent"/> that is not approved), the <see cref="FunctionCallContent"/> that
/// <c>FunctionInvokingChatClient</c> re-creates for it, a generated "rejected" <see cref="FunctionResultContent"/>, and
/// the answer. The tool never runs. On MAF 1.24 that run threw.
/// </summary>
internal static class UnansweredApprovalRun
{
    public const string CallId = "call_1";
    public const string ToolName = "issue_refund";
    public const string NoAnswerReason = "No approval response was provided for this approval required tool call.";
    public const string RejectedResult = "Tool call invocation rejected. " + NoAnswerReason;
    public const string ApprovedResult = "refunded 5000";
    public const string Answer = "Our opening hours are 9 to 5.";

    /// <summary>The first run's text: the scripted model answers it with an <c>issue_refund</c> call.</summary>
    public const string Pause = "Please refund $5000.";

    /// <summary>The second run's text, sent on the same session with no approval decision.</summary>
    public const string NewText = "Never mind. What are your opening hours?";

    private static readonly ChatRole[] Roles = [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant];

    public static Dictionary<string, object?> RefundArgs() => new() { ["amount"] = 5000 };

    /// <summary>
    /// That run's contents, one list per message. Synthetic: built from MEAI content types in the shape read from the
    /// MAF 1.27 and MEAI 10.10 sources, not recorded from a MAF run. With <paramref name="approved"/> the decision is an
    /// approval and the result a real one: a guard for approved decisions, which MAF does not return this way.
    /// </summary>
    public static List<List<AIContent>> Contents(bool approved = false)
    {
        var request = new ToolApprovalRequestContent("ficc_" + CallId, new FunctionCallContent(CallId, ToolName, RefundArgs()));
        var decision = approved
            ? request.CreateResponse(approved: true)
            : request.CreateResponse(approved: false, reason: NoAnswerReason);
        return
        [
            [decision],
            [new FunctionCallContent(CallId, ToolName, RefundArgs())],
            [new FunctionResultContent(CallId, approved ? ApprovedResult : RejectedResult)],
            [new TextContent(Answer)],
        ];
    }

    /// <summary>The contents of <see cref="Contents"/> as chat messages (user, assistant, tool, assistant).</summary>
    public static List<ChatMessage> Messages(bool approved = false)
        => Contents(approved).Select((contents, i) => new ChatMessage(Roles[i], contents)).ToList();

    /// <summary>The contents of <see cref="Contents"/> as an AgentEval response, the way MAFAgentAdapter reports one.</summary>
    public static AgentResponse Response(bool approved = false) => new()
    {
        Text = Answer,
        RawMessages = Messages(approved).Cast<object>().ToList(),
    };

    /// <summary>The contents of <see cref="Contents"/> as MAF streaming updates, one per message.</summary>
    public static List<AgentResponseUpdate> Updates(bool approved = false)
        => Contents(approved).Select((contents, i) => new AgentResponseUpdate { Role = Roles[i], Contents = contents }).ToList();

    /// <summary>Every content of an AgentEval response's raw messages, in order.</summary>
    public static List<AIContent> ContentsOf(AgentResponse response)
        => (response.RawMessages ?? []).OfType<ChatMessage>().SelectMany(m => m.Contents).ToList();

    /// <summary>
    /// A real MAF <see cref="ChatClientAgent"/> (with its default approval binding and function invocation) over a
    /// scripted model, offline. <c>issue_refund</c> requires approval. The first run (<see cref="Pause"/>) pauses on an
    /// approval request because the model calls the tool; a second run on the same session (<see cref="NewText"/>) with
    /// no decision is produced by MAF itself in the shape of <see cref="Contents"/>. No provider is called.
    /// </summary>
    public static (AIAgent Agent, Func<int> Executed) BuildApprovalGatedAgent()
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create(
            (int amount) => { Interlocked.Increment(ref executed); return $"refunded {amount}"; }, ToolName);
        var model = new ScriptedChatClient()
            .AddToolCall(CallId, ToolName, RefundArgs())
            .AddText(Answer);
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "Support",
            ChatOptions = new ChatOptions { Tools = [new ApprovalRequiredAIFunction(refund)] },
        });
        return (agent, () => Volatile.Read(ref executed));
    }

    /// <summary>
    /// Pins MAF #9152 (1.25+): the contents of the second run carry the generated rejection of the saved request, the
    /// call re-created for it and its result. Fails here, not in the assertion under test, if MAF changes that shape.
    /// </summary>
    public static void AssertMafAutoRejected(IEnumerable<AIContent> secondRun)
    {
        var contents = secondRun.ToList();
        var decision = Assert.Single(contents.OfType<ToolApprovalResponseContent>());
        Assert.False(decision.Approved);
        var decided = Assert.IsType<FunctionCallContent>(decision.ToolCall);
        Assert.Equal(CallId, decided.CallId);
        Assert.Equal(ToolName, decided.Name);
        Assert.Contains(contents, c => c is FunctionCallContent call && call.CallId == CallId && call.Name == ToolName);
        Assert.Contains(contents, c => c is FunctionResultContent result && result.CallId == CallId);
    }
}

/// <summary>A MAF agent that streams fixed updates. Scripted, not a MAF run.</summary>
internal sealed class ScriptedUpdatesAgent : AIAgent
{
    private readonly IReadOnlyList<AgentResponseUpdate> _updates;

    public ScriptedUpdatesAgent(IReadOnlyList<AgentResponseUpdate> updates) => _updates = updates;

    public override string? Name => "Scripted";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default)
        => new(new ScriptedUpdatesSession());

    protected override Task<Microsoft.Agents.AI.AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming only.");

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in _updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(new ScriptedUpdatesSession());

    private sealed class ScriptedUpdatesSession : AgentSession
    {
    }
}
