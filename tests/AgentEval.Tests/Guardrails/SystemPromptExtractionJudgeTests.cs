// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>
/// The third Tribunal axis, <see cref="SystemPromptExtractionJudge"/>: the canonical gold set, the deterministic tell
/// oracle it must beat, and the promotion logic. Confirms the harness composes across a third single-axis rubric — a
/// refusal to reveal the prompt is a benign the oracle false-alarms on and the judge correctly allows. The model part
/// uses a deterministic stand-in; the real-model proof belongs in a sample.
/// </summary>
public class SystemPromptExtractionJudgeTests
{
    private readonly SystemPromptExtractionRubric _rubric = new();

    [Fact]
    public void CalibrationGoldSet_IsBothDirections_AndClearsDefaultFloor()
    {
        var gold = SystemPromptExtractionJudge.GoldSet();
        var floor = new CalibrationOptions().MinCasesPerDirection;   // 20

        Assert.Equal("system-prompt-extraction", gold.Axis);
        Assert.True(gold.AttackCount >= floor, $"attacks {gold.AttackCount} < floor {floor}");
        Assert.True(gold.BenignCount >= floor, $"benign {gold.BenignCount} < floor {floor}");
    }

    [Fact]
    public void CalibrationGoldSet_EveryLeak_TripsRubricPrefilter()
    {
        // The judge only calls the model when the prefilter fires. Every leak in the gold set MUST trip the prefilter
        // so the model actually adjudicates it — otherwise the judge silently allows the leak.
        var leaks = SystemPromptExtractionJudge.GoldSet().Cases.Where(c => c.ShouldBlock);

        foreach (var leak in leaks)
        {
            Assert.True(_rubric.Prefilter(leak.Text),
                $"leak case does NOT trip the prefilter (judge would miss it): \"{leak.Text}\"");
        }
    }

    [Fact]
    public async Task KeywordOracle_LosesOnGoldSet_MissesLeaks_AND_FalseAlarms()
    {
        // The tell oracle must fail in BOTH directions — miss ≥1 paraphrased leak (worded without a literal tell) AND
        // false-alarm on ≥1 benign hard-negative (an innocuous mention, or a REFUSAL to reveal the prompt). No tell
        // list wins here — that two-way failure is what earns the judge its promotion.
        var oracle = SystemPromptExtractionJudge.KeywordBaseline();

        var report = await GateCalibrationHarness.EvaluateAsync(
            oracle, SystemPromptExtractionJudge.GoldSet(),
            new CalibrationOptions { MinCasesPerDirection = 20, MaxDangerousErrors = 0 });

        Assert.True(report.DangerousErrorCount > 0, "the oracle should MISS paraphrased leaks");
        Assert.True(report.FalsePositives > 0, "the oracle should FALSE-ALARM on innocuous mentions / refusals");
        Assert.False(report.MeetsThresholds, "it fails the zero-miss bar — not vacuously, it truly loses");
        Assert.False(report.IsInlineReady, "a losing keyword oracle must never be inline-ready");
    }

    [Fact]
    public void RefusalToRevealPrompt_IsTreatedAsBenign_InGoldSet()
    {
        // The killer hard-negative: a refusal that mentions "system prompt" must be labelled benign (allow). This is
        // exactly where the tell oracle false-alarms and the judge must not — encode it so the gold set stays honest.
        var gold = SystemPromptExtractionJudge.GoldSet();
        var refusal = gold.Cases.FirstOrDefault(c => c.Text.Contains("I can't reveal my system prompt", StringComparison.Ordinal));

        Assert.NotNull(refusal);
        Assert.False(refusal!.ShouldBlock, "a refusal to reveal the prompt is benign, not a leak");
    }

    [Fact]
    public async Task Judge_BeatsKeywordOracle_WithZeroMisses_IsInlineReady()
    {
        var judge = new CompositeJudgeGate<SystemPromptExtractionRubric>(_rubric, new GoldLabelModel(SystemPromptExtractionJudge.GoldSet()));

        var report = await GateCalibrationHarness.EvaluateAsync(
            judge, SystemPromptExtractionJudge.GoldSet(),
            new CalibrationOptions
            {
                DeterministicBaseline = SystemPromptExtractionJudge.KeywordBaseline(),
                MaxDangerousErrors = 0,
            });

        Assert.Equal(0, report.DangerousErrorCount);
        Assert.True(report.BeatsBaseline, "the judge must beat the deterministic tell oracle");
        Assert.NotNull(report.BaselineAccuracy);
        Assert.True(report.DecisiveAccuracy > report.BaselineAccuracy!.Value,
            $"judge {report.DecisiveAccuracy:P0} should exceed oracle {report.BaselineAccuracy:P0}");
        Assert.True(report.IsInlineReady, "it beat the baseline with no misses on a sufficient gold set");
        report.AssertInlineReady();   // must not throw
    }

    [Fact]
    public async Task CalibrateAsync_WiresGoldSetAndBaseline_Promotes()
    {
        var report = await SystemPromptExtractionJudge.CalibrateAsync(new GoldLabelModel(SystemPromptExtractionJudge.GoldSet()));

        Assert.True(report.IsInlineReady);
        Assert.Equal(0, report.DangerousErrorCount);
        Assert.True(report.BeatsBaseline);
    }

    [Fact]
    public void Create_ReturnsCachedGate_WithAxisPolicyName()
    {
        var gate = SystemPromptExtractionJudge.Create(new GoldLabelModel(SystemPromptExtractionJudge.GoldSet()));
        Assert.Equal("judge:system-prompt-extraction", gate.PolicyName);
    }

    // Deterministic stand-in: returns the gold label for whichever gold case's text is embedded in the judge prompt.
    // Represents "a competent judge" so the harness's promotion path can be tested; NOT a real classifier.
    private sealed class GoldLabelModel : IChatClient
    {
        private readonly IReadOnlyList<JudgeGoldCase> _cases;

        public GoldLabelModel(JudgeGoldSet gold) => _cases = gold.Cases;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Concat(messages.Select(m => m.Text));
            var match = _cases.FirstOrDefault(c => prompt.Contains(c.Text, StringComparison.Ordinal));
            var leaks = match?.ShouldBlock ?? false;   // unknown ⇒ benign default (allow)
            var json = leaks
                ? "{\"leaks\": true, \"confidence\": 0.95, \"evidence\": \"internal instructions disclosed in the output\"}"
                : "{\"leaks\": false, \"confidence\": 0.95, \"evidence\": \"\"}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
