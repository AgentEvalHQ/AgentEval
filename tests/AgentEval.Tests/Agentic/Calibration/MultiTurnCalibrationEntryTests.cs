// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Evals.Agentic.Conversation;
using AgentEval.Evals.Agentic.Memory;
using Xunit;

namespace AgentEval.Tests.Agentic.Calibration;

/// <summary>
/// A calibration entry carries the earlier turns, the context and the tool calls, and the runner hands them to the
/// evaluator where it reads them at run time. Before, the multi-turn goldens pasted the turns into <c>input</c> as text,
/// which the evaluators do not read: every entry skipped, and the five memory evaluators were carved out of calibration.
/// </summary>
public sealed class MultiTurnCalibrationEntryTests
{
    private sealed class CapturingJudge : IEvaluator
    {
        public List<string> Inputs { get; } = [];

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Inputs.Add(input);
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 90,
                Summary = "captured",
                CriteriaResults = criteria.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "ok" }).ToList(),
            });
        }
    }

    private const string Line = """
        {"scenarioId":"t-1","evaluatorKey":"memory_recall_accuracy","input":"What company do I work at?","conversationHistory":[{"role":"user","content":"I work at Contoso-7731."},{"role":"assistant","content":"Noted."}],"context":"Directory: Contoso-7731 is in Oslo.","toolCalls":[{"name":"lookup","arguments":{"id":"7731"},"result":"found"}],"agentResponse":"You work at Contoso-7731.","expectedVerdict":"pass","expectedScoreMin":0.8,"expectedScoreMax":1.0,"rationale":"recalled"}
        """;

    private static async Task<CalibrationEntry> LoadOneAsync(string jsonl)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonl));
        var dataset = await new CalibrationDatasetLoader().LoadAsync("memory", stream);
        return Assert.Single(dataset.Entries);
    }

    [Fact]
    public async Task AnEntry_CarriesItsHistoryContextAndToolCalls_IntoTheEvalInput()
    {
        var input = (await LoadOneAsync(Line)).ToEvalInput();

        var history = ConversationHistoryHelper.TryGetHistory(input);
        Assert.NotNull(history);
        Assert.Equal(["user", "assistant"], history!.Select(t => t.Role));
        Assert.Equal("I work at Contoso-7731.", history[0].Content);
        Assert.Equal("Directory: Contoso-7731 is in Oslo.", input.Context);
        var call = Assert.Single(input.ToolCalls!);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("found", call.Result);
        Assert.Equal("What company do I work at?", input.Query);
    }

    [Fact]
    public async Task AnEntryWithoutThem_LeavesTheInputAsBefore()
    {
        var entry = await LoadOneAsync(
            """{"scenarioId":"t-2","evaluatorKey":"relevance","input":"q","agentResponse":"r","expectedVerdict":"pass","expectedScoreMin":0.5,"expectedScoreMax":1.0,"rationale":"x"}""");

        var input = entry.ToEvalInput();

        Assert.Null(input.Metadata);
        Assert.Null(input.Context);
        Assert.Null(input.ToolCalls);
    }

    [Fact]
    public async Task TheRunner_GivesTheMemoryEvaluatorTheEarlierTurns_SoItsJudgeSeesThem()
    {
        var judge = new CapturingJudge();
        var runner = new CalibrationRunner(key => key == "memory_recall_accuracy" ? new MemoryRecallAccuracyEval(judge) : null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Line));
        var dataset = await new CalibrationDatasetLoader().LoadAsync("memory", stream);

        var report = await runner.RunAsync([dataset]);

        Assert.Equal(1, report.PerCategory["memory"].EntryCount);   // measured, not skipped for want of history
        Assert.Contains(judge.Inputs, i => i.Contains("I work at Contoso-7731.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("memory_recall_accuracy")]
    [InlineData("turn_coherence")]
    [InlineData("goal_tracking")]
    public async Task EveryShippedGoldenForAHistoryReadingKey_CarriesItsTurnsStructured(string key)
    {
        // A golden that pastes the turns into its input again would be skipped by the evaluator and silently drop out.
        var entries = (await new CalibrationDatasetLoader().LoadAllFromAssemblyAsync(typeof(MultiTurnCalibrationEntryTests).Assembly))
            .SelectMany(d => d.Entries)
            .Where(e => e.EvaluatorKey == key)
            .ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.True(e.ConversationHistory is { Count: > 0 }, $"{e.ScenarioId} has no conversationHistory");
            Assert.DoesNotContain("[turn ", e.Input, StringComparison.Ordinal);
            Assert.DoesNotContain("[Previous turn]", e.Input, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("refusal_quality", "ux")]
    [InlineData("goal_decomposition_quality", "reasoning")]
    [InlineData("plan_formulation_quality", "reasoning")]
    [InlineData("self_correction_quality", "reasoning")]
    [InlineData("response_completeness", "quality")]
    [InlineData("20-quality", "quality")]          // a golden filename suffix still routes by suffix
    [InlineData("memory-multiturn", "memory")]
    [InlineData("tool_selection", "process")]
    public void AKeyEndingInQuality_RoutesToItsOwnCategory(string keyOrSuffix, string category)
    {
        // The filename-suffix scan used to run before the exact keys, so every key ending in "quality" was calibrated,
        // and gated, as "quality".
        Assert.Equal(category, CalibrationDatasetLoader.DeriveCategory(keyOrSuffix));
    }
}
