// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class HallucinatedCitationJudgeTests
{
    // A model stand-in that THROWS if ever called — proves the deterministic existence check
    // short-circuits BEFORE any model call (the whole point of the hybrid design: cheap + fast for the
    // common "citation doesn't even exist" case).
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The model must not be called when the citation does not exist.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ScriptedSupportChatClient(bool supported) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var json = supported
                ? """{"supported": true, "confidence": 0.9}"""
                : """{"supported": false, "confidence": 0.9, "evidence": "not in source"}""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Records the exact prompt sent to the model, always answers "supported" — used to inspect what content actually reached the judge.</summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public string? LastPrompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = messages.Last().Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"supported": true, "confidence": 0.9}""")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task InspectAsync_CitationToNonexistentSource_BlocksWithoutCallingModel()
    {
        var gate = new HallucinatedCitationJudge(new ThrowingChatClient());
        var text = HallucinatedCitationJudge.FormatCase(
            new Dictionary<string, string> { ["S1"] = "Returns within 30 days." },
            citedSourceId: "S99", claim: "Returns within 30 days.");

        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Block, verdict.Action);
        Assert.Contains("does not exist", verdict.Reason);
    }

    [Fact]
    public async Task InspectAsync_CitationToRealSource_UnsupportedClaim_Blocks()
    {
        var gate = new HallucinatedCitationJudge(new ScriptedSupportChatClient(supported: false));
        var text = HallucinatedCitationJudge.FormatCase(
            new Dictionary<string, string> { ["S1"] = "Returns within 30 days." },
            citedSourceId: "S1", claim: "Returns within 90 days, no receipt needed.");

        var verdict = await gate.InspectAsync(text);
        Assert.Equal(GateAction.Block, verdict.Action);
    }

    [Fact]
    public async Task InspectAsync_CitationToRealSource_SupportedClaim_Allows()
    {
        var gate = new HallucinatedCitationJudge(new ScriptedSupportChatClient(supported: true));
        var text = HallucinatedCitationJudge.FormatCase(
            new Dictionary<string, string> { ["S1"] = "Returns within 30 days." },
            citedSourceId: "S1", claim: "You can return items within 30 days.");

        var verdict = await gate.InspectAsync(text);
        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Fact]
    public async Task InspectAsync_UnparseableText_Allows()
    {
        var gate = new HallucinatedCitationJudge(new ThrowingChatClient());
        var verdict = await gate.InspectAsync("not in the expected format at all");
        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Fact]
    public async Task InspectAsync_EmptyText_Allows()
    {
        var gate = new HallucinatedCitationJudge(new ThrowingChatClient());
        var verdict = await gate.InspectAsync("");
        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    // ── SourceLine regex: multi-line source content must not be silently truncated (review) ──

    [Fact]
    public async Task InspectAsync_MultiLineSourceContent_NotTruncated_FullContentReachesJudge()
    {
        // Regression guard: SourceLine's regex previously lacked RegexOptions.Singleline, so `.` never
        // matched `\n` — a source whose content spans multiple lines silently lost every line after the
        // first before the judge ever saw it, risking a false BLOCK for a claim genuinely supported by the
        // truncated-away part of the source.
        var capturing = new CapturingChatClient();
        var gate = new HallucinatedCitationJudge(capturing);
        var text = HallucinatedCitationJudge.FormatCase(
            new Dictionary<string, string>
            {
                ["S1"] = "Our return policy allows 30-day returns with a receipt.\n" +
                         "Exceptions apply for electronics, which have a 14-day window.\n" +
                         "All other terms follow standard commerce law.",
            },
            citedSourceId: "S1", claim: "Electronics have a 14-day return window.");

        await gate.InspectAsync(text);

        Assert.NotNull(capturing.LastPrompt);
        Assert.Contains("Exceptions apply for electronics, which have a 14-day window.", capturing.LastPrompt);
        Assert.Contains("All other terms follow standard commerce law.", capturing.LastPrompt);
    }

    [Fact]
    public async Task InspectAsync_MultiLineSourceFollowedByAnotherSource_DoesNotBleedAcrossSources()
    {
        // Guards the FIX itself, not just the original bug: a naive RegexOptions.Singleline-only change
        // (verified empirically before choosing this lookahead-bounded regex instead) makes source 1's
        // multi-line content greedily swallow source 2, CITED SOURCE, and CLAIM too — which would make "S2"
        // vanish from the parsed sources dictionary entirely and turn this into a false "does not exist"
        // Block instead of proceeding to the judge.
        var gate = new HallucinatedCitationJudge(new ScriptedSupportChatClient(supported: true));
        var text = HallucinatedCitationJudge.FormatCase(
            new Dictionary<string, string>
            {
                ["S1"] = "First line of source one.\nSecond line of source one.",
                ["S2"] = "Source two is a single line.",
            },
            citedSourceId: "S2", claim: "Source two is a single line.");

        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Fact]
    public void FormatCase_RoundTrips_MultipleSources()
    {
        var text = HallucinatedCitationJudge.FormatCase(
            new Dictionary<string, string> { ["S1"] = "content one", ["S2"] = "content two" },
            citedSourceId: "S2", claim: "a claim");

        Assert.Contains("[S1] content one", text);
        Assert.Contains("[S2] content two", text);
        Assert.Contains("CITED SOURCE: S2", text);
        Assert.Contains("CLAIM: a claim", text);
    }

    [Fact]
    public void GoldSet_IsBothDirections_AboveMinCasesPerDirection()
    {
        var goldSet = HallucinatedCitationJudge.CalibrationGoldSet();
        Assert.True(goldSet.AttackCount >= 20, $"expected >=20 hallucinated/unsupported cases, got {goldSet.AttackCount}");
        Assert.True(goldSet.BenignCount >= 20, $"expected >=20 valid cases, got {goldSet.BenignCount}");
    }

    [Fact]
    public void PolicyName_IsJudgeHallucinatedCitation()
    {
        var gate = new HallucinatedCitationJudge(new ThrowingChatClient());
        Assert.Equal("judge:hallucinated-citation", gate.PolicyName);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HallucinatedCitationJudge(null!));
    }
}
