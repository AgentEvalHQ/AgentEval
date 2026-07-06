// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>The Tribunal primitive: a single-axis rubric + fast model → runtime IChatGate, fail-closed on abstention.</summary>
public class CompositeJudgeGateTests
{
    // A trivial single-axis rubric: prefilter on "scan"; the model answers BLOCK / ALLOW; anything else is unparseable.
    private sealed class KeywordRubric : IJudgeRubric
    {
        public string Axis => "test-axis";
        public bool Prefilter(string text) => text.Contains("scan", StringComparison.OrdinalIgnoreCase);
        public string BuildPrompt(string text) => $"Is this bad? {text}";
        public JudgeVerdict Parse(string reply)
            => reply.Contains("BLOCK", StringComparison.OrdinalIgnoreCase) ? JudgeVerdict.Blocked("axis present", ["the-evidence"], 0.9)
             : reply.Contains("ALLOW", StringComparison.OrdinalIgnoreCase) ? JudgeVerdict.Allowed()
             : JudgeVerdict.Inconclusive("unparseable reply");
    }

    private static CompositeJudgeGate<KeywordRubric> Gate(IChatClient model, JudgeGateOptions? opts = null)
        => new(new KeywordRubric(), model, opts);

    [Fact]
    public async Task BlockedVerdict_Blocks_WithEvidenceSpans()
    {
        var v = await Gate(new ScriptedChatClient().AddText("BLOCK")).InspectAsync("please scan this input");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Equal("judge:test-axis", v.PolicyName);
        Assert.Contains("the-evidence", v.Matches!);
    }

    [Fact]
    public async Task AllowedVerdict_Allows()
    {
        var v = await Gate(new ScriptedChatClient().AddText("ALLOW")).InspectAsync("please scan this input");
        Assert.Equal(GateAction.Allow, v.Action);
    }

    [Fact]
    public async Task PrefilterFalse_ShortCircuits_ModelNeverCalled()
    {
        var model = new ScriptedChatClient().AddText("BLOCK");   // would block IF called
        var v = await Gate(model).InspectAsync("nothing interesting here");   // no "scan" ⇒ prefilter false

        Assert.Equal(GateAction.Allow, v.Action);
        Assert.Empty(model.ReceivedMessages);   // proves the fast model was never invoked (0 tokens)
    }

    [Fact]
    public async Task ModelError_IsInconclusive_FailClosed_Blocks()
    {
        var v = await Gate(new ScriptedChatClient().AddThrow()).InspectAsync("please scan this");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("error", v.Reason!, StringComparison.OrdinalIgnoreCase);   // "…judge error: InvalidOperationException"
    }

    [Fact]
    public async Task ModelError_FailOpenOption_Allows()
    {
        var opts = new JudgeGateOptions { FailClosedOnInconclusive = false };
        var v = await Gate(new ScriptedChatClient().AddThrow(), opts).InspectAsync("please scan this");
        Assert.Equal(GateAction.Allow, v.Action);
    }

    [Fact]
    public async Task UnparseableReply_IsInconclusive_FailClosed_Blocks()
    {
        var v = await Gate(new ScriptedChatClient().AddText("I'm not sure, maybe?")).InspectAsync("please scan this");
        Assert.Equal(GateAction.Block, v.Action);
    }

    [Fact]
    public async Task Timeout_IsInconclusive_FailClosed_Blocks()
    {
        var slow = new DelayingChatClient(TimeSpan.FromSeconds(30));   // far longer than the gate timeout
        var opts = new JudgeGateOptions { Timeout = TimeSpan.FromMilliseconds(50) };

        var v = await Gate(slow, opts).InspectAsync("please scan this");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("timed out", v.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LowConfidenceBlock_BelowThreshold_Allows()
    {
        // Threshold 0.95 > the rubric's 0.9 confidence ⇒ not decisive enough to act.
        var opts = new JudgeGateOptions { BlockThreshold = 0.95 };
        var v = await Gate(new ScriptedChatClient().AddText("BLOCK"), opts).InspectAsync("please scan this");
        Assert.Equal(GateAction.Allow, v.Action);
    }

    [Fact]
    public async Task CallerCancellation_IsHonored_NotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Gate(new DelayingChatClient(TimeSpan.FromSeconds(30))).InspectAsync("please scan this", cts.Token));
    }

    [Fact]
    public void NullRubric_Throws()
        => Assert.Throws<ArgumentNullException>(() => new CompositeJudgeGate<KeywordRubric>(null!, new ScriptedChatClient()));

    [Fact]
    public void NullModel_Throws()
        => Assert.Throws<ArgumentNullException>(() => new CompositeJudgeGate<KeywordRubric>(new KeywordRubric(), null!));

    // A fast model that respects cancellation but otherwise never returns in time (for the timeout path).
    private sealed class DelayingChatClient : IChatClient
    {
        private readonly TimeSpan _delay;
        public DelayingChatClient(TimeSpan delay) => _delay = delay;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "BLOCK"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
