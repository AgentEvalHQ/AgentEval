// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core.Benchmarks;
using AgentEval.Tracing;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Glass Box Phase 3 (T3.1/T3.3/T3.4/T3.7): the Trace Fidelity runner — the six discrepancy classes, the
/// pinned scoring rubric, the EvalResult tree projection, and the Shape-B registry registration.
/// </summary>
public class TraceFidelityTests
{
    // ── fixtures: build the two traces by hand (most deterministic) ──
    private static AgentTrace Chat(params TraceEntry[] entries) => new() { Version = "1.1", Entries = entries.ToList() };

    private static AgentTrace Agent(TracePerformance? performance, params TraceEntry[] entries)
        => new() { Version = "1.0", Entries = entries.ToList(), Performance = performance };

    private static TraceEntry ChatResp(int i, string? finish = "stop",
        IEnumerable<(string Name, string Args)>? calls = null, int prompt = 0, int completion = 0)
        => TraceEntry.ForChatResponse(
            i, correlationId: "inv-1", text: "…", durationMs: 10,
            usage: new TraceTokenUsage { PromptTokens = prompt, CompletionTokens = completion },
            toolCalls: calls?.Select(c => new TraceToolCall { Name = c.Name, Arguments = c.Args }).ToList(),
            finishReason: finish, providerMetadata: null);

    private static TraceEntry AgentResp(int i, IEnumerable<(string Name, string Args)>? calls = null, string? finish = null)
        => new()
        {
            Type = TraceEntryType.Response,
            Index = i,
            ToolCalls = calls?.Select(c => new TraceToolCall { Name = c.Name, Arguments = c.Args }).ToList(),
            FinishReason = finish,
        };

    private static TraceFidelityReport Reconcile(AgentTrace agent, AgentTrace chat)
        => new TraceFidelityRunner().Reconcile(agent, chat);

    private static TraceFidelityDiscrepancy Class(TraceFidelityReport report, string key)
        => report.Discrepancies.Single(d => d.ClassKey == key);

    // ── T3.1: rubric pinned by a divergence test ──
    [Fact]
    public void Rubric_WeightsSumToOne()
        => Assert.Equal(1.00, TraceFidelityRubric.Classes.Sum(TraceFidelityRubric.Weight), 6);

    [Fact]
    public void Rubric_WorkedExample_OneHiddenRetry_RootIs090_ChildIs050()
    {
        // High severity penalty = 0.50; child = 1 - 0.50 = 0.50; root = 1 - 0.20*(1-0.50) = 0.90
        Assert.Equal(0.50, TraceFidelityRubric.ChildValue(TraceFidelityRubric.HiddenRetries, 1), 6);
        var chat = Chat(
            ChatResp(0, "tool_calls", new[] { ("SearchFlights", "{}") }),
            ChatResp(1, "tool_calls", new[] { ("SearchFlights", "{}") }),
            ChatResp(2));
        var agent = Agent(null, AgentResp(0, new[] { ("SearchFlights", "{}") }));

        var report = Reconcile(agent, chat);

        Assert.Equal(1, Class(report, TraceFidelityRubric.HiddenRetries).Count);
        Assert.Equal(0.50, Class(report, TraceFidelityRubric.HiddenRetries).Score, 6);
        Assert.Equal(0.90, report.OverallScore, 6);
    }

    // ── T3.7: one probe per discrepancy class ──
    [Fact]
    public void MissingToolCalls_DetectedWhenAgentOmitsAModelCall()
    {
        var report = Reconcile(
            agent: Agent(null, AgentResp(0)),
            chat: Chat(ChatResp(0, "tool_calls", new[] { ("SearchHotels", "{}") })));
        Assert.Equal(1, Class(report, TraceFidelityRubric.MissingToolCalls).Count);
        Assert.Equal("High", Class(report, TraceFidelityRubric.MissingToolCalls).Severity);
        Assert.True(report.OverallScore < 1.0);
    }

    [Fact]
    public void PhantomToolCalls_DetectedWhenAgentInventsACall()
    {
        var report = Reconcile(
            agent: Agent(null, AgentResp(0, new[] { ("DeleteAll", "{}") })),
            chat: Chat(ChatResp(0)));
        Assert.Equal(1, Class(report, TraceFidelityRubric.PhantomToolCalls).Count);
        Assert.Equal("High", Class(report, TraceFidelityRubric.PhantomToolCalls).Severity);
    }

    [Fact]
    public void ArgumentDrift_DetectedWhenSameToolHasDifferentArgs_ButNotForRetries()
    {
        var report = Reconcile(
            agent: Agent(null, AgentResp(0, new[] { ("Book", "{\"city\":\"Tokyo\"}") })),
            chat: Chat(ChatResp(0, "tool_calls", new[] { ("Book", "{\"city\":\"NRT\"}") })));
        Assert.Equal(1, Class(report, TraceFidelityRubric.ArgumentDrift).Count);
        Assert.Equal("Medium", Class(report, TraceFidelityRubric.ArgumentDrift).Severity);
        // A retry (same args twice) must NOT count as drift (it's hidden_retries):
        var retry = Reconcile(
            agent: Agent(null, AgentResp(0, new[] { ("Book", "{}") })),
            chat: Chat(ChatResp(0, "tool_calls", new[] { ("Book", "{}") }), ChatResp(1, "tool_calls", new[] { ("Book", "{}") })));
        Assert.Equal(0, Class(retry, TraceFidelityRubric.ArgumentDrift).Count);
    }

    [Fact]
    public void TokenUnderReporting_DetectedBeyondTolerance()
    {
        var report = Reconcile(
            agent: Agent(new TracePerformance { TotalPromptTokens = 100, TotalCompletionTokens = 50 }, AgentResp(0)),
            chat: Chat(ChatResp(0, prompt: 200, completion: 100)));   // chat=300 vs agent=150
        Assert.Equal(1, Class(report, TraceFidelityRubric.TokenUnderReporting).Count);
        Assert.Equal("Low", Class(report, TraceFidelityRubric.TokenUnderReporting).Severity);
    }

    [Fact]
    public void SuppressedFinishReason_DetectedWhenChatSawContentFilter()
    {
        // The agent boundary reports NO finish reason: "reported none" does not reflect the content_filter turn.
        var report = Reconcile(
            agent: Agent(null, AgentResp(0)),
            chat: Chat(ChatResp(0, finish: "content_filter")));
        Assert.Equal(1, Class(report, TraceFidelityRubric.SuppressedFinishReason).Count);
        Assert.Equal("Critical", Class(report, TraceFidelityRubric.SuppressedFinishReason).Severity);
        Assert.Contains("agent reported: <null>", Assert.Single(Class(report, TraceFidelityRubric.SuppressedFinishReason).Examples));
    }

    [Theory]
    [InlineData("content_filter", "content_filter")]
    [InlineData("length", "length")]
    [InlineData("content_filter", "CONTENT_FILTER")]   // compared case-insensitively
    public void SuppressedFinishReason_NotFlaggedWhenAgentReportsTheSameReason(string chatFinish, string agentFinish)
    {
        // A faithful report: the framework passed the provider's intervention through, so nothing was suppressed.
        var report = Reconcile(
            agent: Agent(null, AgentResp(0, finish: agentFinish)),
            chat: Chat(ChatResp(0, finish: chatFinish)));
        var suppressed = Class(report, TraceFidelityRubric.SuppressedFinishReason);
        Assert.Equal(0, suppressed.Count);
        Assert.Empty(suppressed.Examples);
        Assert.Equal(1.0, report.OverallScore, 6);
    }

    [Theory]
    [InlineData("content_filter", "stop")]
    [InlineData("length", "stop")]
    [InlineData("content_filter", "length")]   // a different intervention is not the reason the turn ended with
    public void SuppressedFinishReason_DetectedWhenAgentReportsADifferentReason(string chatFinish, string agentFinish)
    {
        var report = Reconcile(
            agent: Agent(null, AgentResp(0, finish: agentFinish)),
            chat: Chat(ChatResp(0, finish: chatFinish)));
        var suppressed = Class(report, TraceFidelityRubric.SuppressedFinishReason);
        Assert.Equal(1, suppressed.Count);
        Assert.Equal("Critical", suppressed.Severity);
        var example = Assert.Single(suppressed.Examples);
        Assert.Contains($"'{chatFinish}'", example);
        Assert.Contains($"agent reported: '{agentFinish}'", example);
        Assert.Equal(0.85, report.OverallScore, 6);   // root = 1 - 0.15 x (1 - 0)
    }

    [Fact]
    public void SuppressedFinishReason_CountsOnlyTheTurnsTheAgentBoundaryDidNotReport()
    {
        // Two invocations each ended in content_filter at the chat boundary; the agent boundary reported it for one
        // and 'stop' for the other. Reconciled by count per reason, so exactly one turn is suppressed, not two.
        var report = Reconcile(
            agent: Agent(null, AgentResp(0, finish: "content_filter"), AgentResp(1, finish: "stop")),
            chat: Chat(ChatResp(0, finish: "content_filter"), ChatResp(1, finish: "content_filter")));
        var suppressed = Class(report, TraceFidelityRubric.SuppressedFinishReason);
        Assert.Equal(1, suppressed.Count);
        Assert.Contains("reported it 1×", Assert.Single(suppressed.Examples));
    }

    [Fact]
    public void CleanPair_ScoresPerfectFidelity()
    {
        var report = Reconcile(
            agent: Agent(new TracePerformance { TotalPromptTokens = 10, TotalCompletionTokens = 5 }, AgentResp(0, new[] { ("Book", "{\"x\":1}") })),
            chat: Chat(ChatResp(0, "stop", new[] { ("Book", "{\"x\":1}") }, prompt: 10, completion: 5)));
        Assert.Equal(1.0, report.OverallScore, 6);
        Assert.All(report.Discrepancies, d => Assert.Equal(1.0, d.Score, 6));
    }

    // ── T3.3: EvalResult tree projection ──
    [Fact]
    public void AWarnIsASoftFail_NotAPass_AndAPassCarriesNoSeverity()
    {
        // Review round 5 M-2 (B10w): 0.80-0.99 was labelled warn with Passed = true, so the command printed PASS and
        // exited 0; the root's severity read "Low" even at 1.00.
        var chat = Chat(
            ChatResp(0, "tool_calls", new[] { ("SearchFlights", "{}") }),
            ChatResp(1, "tool_calls", new[] { ("SearchFlights", "{}") }),
            ChatResp(2));
        var agent = Agent(null, AgentResp(0, new[] { ("SearchFlights", "{}") }));

        var warn = new TraceFidelityRunner().ReconcileToEvalResult(agent, chat);

        Assert.Equal(0.90, warn.Score.Value, 6);
        Assert.Equal("warn", warn.Score.Label);
        Assert.False(warn.Score.Passed);
        Assert.Equal("low", warn.Score.Severity);
        Assert.All(warn.Details.SubResults!, s => Assert.Equal(s.Score.Label == "pass", s.Score.Passed));

        var clean = new TraceFidelityRunner().ReconcileToEvalResult(
            Agent(new TracePerformance { TotalPromptTokens = 10, TotalCompletionTokens = 5 }, AgentResp(0, new[] { ("Book", "{\"x\":1}") })),
            Chat(ChatResp(0, "stop", new[] { ("Book", "{\"x\":1}") }, prompt: 10, completion: 5)));   // CleanPair_ScoresPerfectFidelity's pair
        Assert.Equal("pass", clean.Score.Label);
        Assert.True(clean.Score.Passed);
        Assert.Equal("none", clean.Score.Severity);
    }

    [Fact]
    public void AChatTraceWithNoResponses_HasNothingToReconcile_NoVerdict()
    {
        // Review round 6 (B10y): an empty or failed chat capture scored every class 1.0 and read PASS.
        var result = new TraceFidelityRunner().ReconcileToEvalResult(new AgentTrace(), new AgentTrace());

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.All(result.Details.SubResults!, s => Assert.Equal("skipped", s.Score.Label));
    }

    [Fact]
    public void ReconcileToEvalResult_EmitsSixSubResultsWithExpectedKeysAndDimensions()
    {
        var chat = Chat(
            ChatResp(0, "tool_calls", new[] { ("SearchFlights", "{}") }),
            ChatResp(1, "tool_calls", new[] { ("SearchFlights", "{}") }));
        var agent = Agent(null, AgentResp(0, new[] { ("SearchFlights", "{}") }));

        var result = new TraceFidelityRunner().ReconcileToEvalResult(agent, chat);

        Assert.Equal("trace_fidelity", result.Metric.Key);
        Assert.InRange(result.Score.Value, 0.0, 1.0);
        Assert.Equal(result.Score.Value * 100, result.Details.Dimensions!["score100"], 6);
        Assert.Equal(6, result.Details.SubResults!.Count);
        var keys = result.Details.SubResults.Select(s => s.Metric.Key).ToHashSet();
        Assert.Contains("trace_fidelity.hidden_retries", keys);
        Assert.Contains("trace_fidelity.suppressed_finish_reason", keys);
        // The firing class carries concrete evidence.
        var hidden = result.Details.SubResults.Single(s => s.Metric.Key == "trace_fidelity.hidden_retries");
        Assert.NotEmpty(hidden.Details.Evidence!);
    }

    // ── T3.4: Shape-B registry registration ──
    [Fact]
    public void Family_IsRegisteredAsShapeB()
    {
        _ = typeof(TraceFidelityBenchmark).Assembly;   // force module-init
        var family = BenchmarkFamilyRegistry.TryGet("trace-fidelity");

        Assert.NotNull(family);
        Assert.Equal(typeof(TraceFidelityRunner), family!.RunnerType);
        Assert.Null(family.CompositeFactory);
        Assert.Null(family.EvaluateAsync);
        Assert.Equal(3, family.Presets.Count);
        Assert.IsType<TraceFidelityRunner>(family.RunnerFactory!("standard"));
    }

    // ── P3.2a: workflow-trace-fidelity Shape-B registry registration ──
    [Fact]
    public void WorkflowFamily_IsRegisteredAsShapeB()
    {
        _ = typeof(WorkflowTraceFidelityBenchmark).Assembly;   // force module-init
        var family = BenchmarkFamilyRegistry.TryGet("workflow-trace-fidelity");

        Assert.NotNull(family);
        Assert.Equal(typeof(WorkflowTraceFidelityReconciler), family!.RunnerType);
        Assert.Null(family.CompositeFactory);
        Assert.Null(family.EvaluateAsync);
        Assert.Equal(3, family.Presets.Count);
        Assert.IsType<WorkflowTraceFidelityReconciler>(family.RunnerFactory!("standard"));
    }
}
