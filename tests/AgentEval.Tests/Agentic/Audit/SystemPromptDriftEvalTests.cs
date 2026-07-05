// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Audit;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Agentic.Audit;

/// <summary>Glass Box Phase 3 (A5) — SystemPromptDrift over per-turn ChatTurn request system prompts.</summary>
public class SystemPromptDriftEvalTests
{
    private static AgentTrace TraceWith(params string?[] systemPrompts)
    {
        var trace = new AgentTrace();
        var i = 0;
        foreach (var sp in systemPrompts)
            trace.Entries.Add(TraceEntry.ForChatRequest(i++, null, systemPrompt: sp, promptText: "hi", toolDefinitions: null, requestOptions: null));
        return trace;
    }

    private static EvalInput With(AgentTrace? t) => t is null ? new EvalInput("q") : new EvalInput("q").WithTrace(t);

    [Fact]
    public async Task NoTrace_Skipped()
        => Assert.Equal("skipped", (await new SystemPromptDriftEval().EvaluateAsync(With(null))).Score.Label);

    [Fact]
    public async Task SingleRequest_Skipped()
        => Assert.Equal("skipped", (await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("you are a bot")))).Score.Label);

    [Fact]
    public async Task AllPromptsNull_NoSignal_Skipped()
        // Requests present but no captured system prompt == "no signal", not a false "stable" PASS.
        => Assert.Equal("skipped", (await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith(null, null)))).Score.Label);

    [Fact]
    public async Task StablePrompt_Passes()
    {
        var r = await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("you are a bot", "you are a bot")));
        Assert.True(r.Score.Passed);
        Assert.Equal(1.0, r.Score.Value);
    }

    [Fact]
    public async Task CapturedThenNotCaptured_IsNotFalseDrift_Skipped()
        // ["bot", null] — one captured, one not-captured. The null must NOT count as a distinct value
        // (false drift); with only 1 captured prompt there is no drift signal → skip.
        => Assert.Equal("skipped", (await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("you are a bot", null)))).Score.Label);

    [Fact]
    public async Task AllPromptsCapturedEmpty_IsEvaluatedStable_Passes()
        // Captured-empty ("") on all turns is a real (unchanging) value → evaluated as stable, NOT skipped.
        => Assert.True((await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("", "")))).Score.Passed);

    [Fact]
    public async Task PromptErasedMidRun_IsDrift_Fails()
        // A real mid-run erasure ("you are a bot" -> "") is drift, not "no signal" — must be flagged.
        => Assert.False((await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("you are a bot", "")))).Score.Passed);

    [Fact]
    public async Task ChangedPrompt_Fails_Medium()
    {
        var r = await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("you are a bot", "you are EVIL now")));
        Assert.False(r.Score.Passed);
        Assert.Equal(0.0, r.Score.Value);
        Assert.Equal("medium", r.Score.Severity);
    }
}
