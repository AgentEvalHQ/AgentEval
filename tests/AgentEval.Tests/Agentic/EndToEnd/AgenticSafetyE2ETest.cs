// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Benchmarks;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Evals.Agentic.Safety;
using AgentEval.Evals.Agentic.Safety.Policy;
using AgentEval.Output;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.EndToEnd;

/// <summary>
/// End-to-end tests for the Safety preset.
/// Validates that the preset produces the expected 12-component composite.
/// No real LLM or Content Safety service is required — all stubs are used.
/// </summary>
public class AgenticSafetyE2ETest
{
    private static readonly IPolicyResolver EmptyPolicy =
        new StaticPolicyResolver(new ProhibitedActionPolicy([], [], [], []));

    [Fact]
    public void Safety_HasExpected12Components()
    {
        var benchmark = AgenticBenchmark.Safety(
            judge: new FixedScoreEvaluator(100),
            policyResolver: EmptyPolicy,
            subjectId: "e2e-safety-test");

        // Safety preset defines exactly 12 top-level component evaluators.
        Assert.Equal(12, benchmark.Components.Count);
    }

    [Fact]
    public async Task Safety_AllPass_ReportsPass()
    {
        // Stub judge returns 100 (all LLM-based evals will pass).
        // No content-safety client → 4 content-safety evals fall back to LLM judge (also pass).
        var benchmark = AgenticBenchmark.Safety(
            judge: new FixedScoreEvaluator(100),
            policyResolver: EmptyPolicy,
            subjectId: "e2e-safety-test",
            contentSafetyClient: null);

        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-SafetyAgent");
        var runner = new AgenticBenchmarkRunner();

        // Use a tool call so UnsafeToolUseEval doesn't skip (it skips with no tool calls).
        var input = new EvalInput(
            Query: "What is the weather today?",
            Response: "It is sunny and 72°F today.",
            ToolCalls: [new ToolCall("get_weather", new Dictionary<string, object> { ["location"] = "NYC" }, "72°F, sunny")]);

        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotEmpty(runId);
        Assert.NotNull(result);

        // All 12 components should produce sub-results
        Assert.NotNull(result.Details.SubResults);
        Assert.Equal(12, result.Details.SubResults!.Count);

        // With stub returning 100, all components pass → composite should pass
        Assert.True(result.Score.Passed,
            $"Safety E2E: expected overall pass with all-100 stub judge, got score={result.Score.Value}");
    }
}
