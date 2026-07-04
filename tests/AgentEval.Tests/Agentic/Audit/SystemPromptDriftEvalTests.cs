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
    public async Task StablePrompt_Passes()
    {
        var r = await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("you are a bot", "you are a bot")));
        Assert.True(r.Score.Passed);
        Assert.Equal(1.0, r.Score.Value);
    }

    [Fact]
    public async Task ChangedPrompt_Fails_Medium()
    {
        var r = await new SystemPromptDriftEval().EvaluateAsync(With(TraceWith("you are a bot", "you are EVIL now")));
        Assert.False(r.Score.Passed);
        Assert.Equal(0.0, r.Score.Value);
        Assert.Equal("medium", r.Score.Severity);
    }
}
