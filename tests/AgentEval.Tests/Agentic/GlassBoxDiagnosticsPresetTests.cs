// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// Glass Box Phase 3 (P3.3 preset + P3.3.0 wiring) — the <c>glass-box-diagnostics</c> preset surfaces the 7
/// deterministic trace evaluators, and a single <c>WithTrace</c> on the composite's <see cref="EvalInput"/>
/// reaches every leaf (they are inert / all-Skipped without it).
/// </summary>
public class GlassBoxDiagnosticsPresetTests
{
    [Fact]
    public void Factory_Has7Components()
        => Assert.Equal(7, AgenticBenchmark.GlassBoxDiagnostics().Components.Count);

    [Fact]
    public void Registry_Resolves_GlassBoxDiagnostics()
    {
        _ = typeof(AgenticBenchmark).Assembly;   // force module-init
        var family = BenchmarkFamilyRegistry.TryGet("agentic");

        var composite = family!.CompositeFactory!("glass-box-diagnostics", null);

        Assert.Equal(7, composite.Components.Count);
    }

    private static AgentTrace RichTrace()
    {
        var trace = new AgentTrace();
        // 2 chat requests (drift), 2 chat responses w/ tokens (safety/truncation/token-dist), 2 tool executions.
        trace.Entries.Add(TraceEntry.ForChatRequest(0, null, "you are a bot", "hi", null, null));
        trace.Entries.Add(TraceEntry.ForChatRequest(1, null, "you are a bot", "again", null, null));
        trace.Entries.Add(TraceEntry.ForChatResponse(0, null, "ok", 1,
            new TraceTokenUsage { PromptTokens = 10, CompletionTokens = 40 }, null, "stop", null));
        trace.Entries.Add(TraceEntry.ForChatResponse(1, null, "ok", 1,
            new TraceTokenUsage { PromptTokens = 10, CompletionTokens = 40 }, null, "stop", null));
        trace.Entries.Add(TraceEntry.ForToolExecution(0, null, "search", "{\"q\":\"x\"}", "ok", 5, true, null));
        trace.Entries.Add(TraceEntry.ForToolExecution(1, null, "search", "{\"q\":\"y\"}", "ok", 5, true, null));
        return trace;
    }

    private static int SkippedLeafCount(EvalResult result)
        => result.Details.SubResults?.Count(r => r.Score.Label == "skipped") ?? 0;

    [Fact]
    public async Task WithTrace_LeavesAreNotAllSkipped()
    {
        var composite = AgenticBenchmark.GlassBoxDiagnostics();
        var input = new EvalInput(Query: "q", Response: "r").WithTrace(RichTrace());

        var result = await composite.EvaluateAsync(input);

        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(7, result.Details.SubResults!.Count);
        Assert.True(SkippedLeafCount(result) < 7, "at least one leaf should score against the attached trace");
    }

    [Fact]
    public async Task NoTrace_AllLeavesSkip()
    {
        var composite = AgenticBenchmark.GlassBoxDiagnostics();
        var input = new EvalInput(Query: "q", Response: "r"); // no trace

        var result = await composite.EvaluateAsync(input);

        Assert.Equal(7, SkippedLeafCount(result));
    }
}
