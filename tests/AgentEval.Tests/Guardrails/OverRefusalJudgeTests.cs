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
/// The utility-valve axis, <see cref="OverRefusalJudge"/>: the canonical gold set, the naive refusal-marker oracle it
/// must beat, and the promotion logic. Confirms the harness serves an <i>advisory</i> axis too — a justified refusal
/// that cites a real reason is a benign the oracle false-alarms on and the judge correctly allows. Model part is a
/// deterministic stand-in; the real-model proof belongs in a sample.
/// </summary>
public class OverRefusalJudgeTests
{
    private readonly OverRefusalRubric _rubric = new();

    [Fact]
    public void CalibrationGoldSet_IsBothDirections_AndClearsDefaultFloor()
    {
        var gold = OverRefusalJudge.GoldSet();
        var floor = new CalibrationOptions().MinCasesPerDirection;   // 20

        Assert.Equal("over-refusal", gold.Axis);
        Assert.True(gold.AttackCount >= floor, $"over-refusals {gold.AttackCount} < floor {floor}");
        Assert.True(gold.BenignCount >= floor, $"non-over-refusals {gold.BenignCount} < floor {floor}");
    }

    [Fact]
    public void CalibrationGoldSet_EveryOverRefusal_TripsRubricPrefilter()
    {
        // The judge only calls the model when the prefilter fires. Every over-refusal in the gold set MUST trip the
        // prefilter — including the marker-less soft declines — so the model actually adjudicates it.
        var overRefusals = OverRefusalJudge.GoldSet().Cases.Where(c => c.ShouldBlock);

        foreach (var or in overRefusals)
        {
            Assert.True(_rubric.Prefilter(or.Text),
                $"over-refusal case does NOT trip the prefilter (judge would miss it): \"{or.Text}\"");
        }
    }

    [Fact]
    public async Task RefusalMarkerOracle_LosesOnGoldSet_MissesSoftRefusals_AND_FalseAlarmsOnJustified()
    {
        // The naive refusal oracle must fail in BOTH directions — miss ≥1 marker-less soft over-refusal AND
        // false-alarm on ≥1 JUSTIFIED refusal (or a non-refusal use of "can't"/"sorry"). Reading surface markers
        // can't separate a wrongful refusal from a justified one — which is exactly why the judge is needed.
        var oracle = OverRefusalJudge.KeywordBaseline();

        var report = await GateCalibrationHarness.EvaluateAsync(
            oracle, OverRefusalJudge.GoldSet(),
            new CalibrationOptions { MinCasesPerDirection = 20, MaxDangerousErrors = 0 });

        Assert.True(report.DangerousErrorCount > 0, "the oracle should MISS marker-less soft over-refusals");
        Assert.True(report.FalsePositives > 0, "the oracle should FALSE-ALARM on justified refusals / non-refusal markers");
        Assert.False(report.MeetsThresholds, "it fails the zero-miss bar — not vacuously, it truly loses");
        Assert.False(report.IsInlineReady, "a losing keyword oracle must never be inline-ready");
    }

    [Fact]
    public void JustifiedRefusal_IsTreatedAsBenign_InGoldSet()
    {
        // The key discrimination: a refusal that cites a genuine reason (harmful content) is NOT over-refusal and must
        // be labelled benign — this is where the marker oracle false-alarms and the judge must not.
        var gold = OverRefusalJudge.GoldSet();
        var justified = gold.Cases.FirstOrDefault(c => c.Text.Contains("create malware", StringComparison.Ordinal));

        Assert.NotNull(justified);
        Assert.False(justified!.ShouldBlock, "a justified refusal is not over-refusal");
    }

    [Fact]
    public async Task Judge_BeatsRefusalMarkerOracle_WithZeroMisses_IsInlineReady()
    {
        var judge = new CompositeJudgeGate<OverRefusalRubric>(_rubric, new GoldLabelModel(OverRefusalJudge.GoldSet()));

        var report = await GateCalibrationHarness.EvaluateAsync(
            judge, OverRefusalJudge.GoldSet(),
            new CalibrationOptions
            {
                DeterministicBaseline = OverRefusalJudge.KeywordBaseline(),
                MaxDangerousErrors = 0,
            });

        Assert.Equal(0, report.DangerousErrorCount);
        Assert.True(report.BeatsBaseline, "the judge must beat the naive refusal-marker oracle");
        Assert.NotNull(report.BaselineAccuracy);
        Assert.True(report.DecisiveAccuracy > report.BaselineAccuracy!.Value,
            $"judge {report.DecisiveAccuracy:P0} should exceed oracle {report.BaselineAccuracy:P0}");
        Assert.True(report.IsInlineReady, "it beat the baseline with no misses on a sufficient gold set");
        report.AssertInlineReady();   // must not throw
    }

    [Fact]
    public async Task CalibrateAsync_WiresGoldSetAndBaseline_Promotes()
    {
        var report = await OverRefusalJudge.CalibrateAsync(new GoldLabelModel(OverRefusalJudge.GoldSet()));

        Assert.True(report.IsInlineReady);
        Assert.Equal(0, report.DangerousErrorCount);
        Assert.True(report.BeatsBaseline);
    }

    [Fact]
    public void Create_ReturnsCachedGate_WithAxisPolicyName()
    {
        var gate = OverRefusalJudge.Create(new GoldLabelModel(OverRefusalJudge.GoldSet()));
        Assert.Equal("judge:over-refusal", gate.PolicyName);
    }

    // Deterministic stand-in: returns the gold label for whichever gold case's text is embedded in the judge prompt.
    private sealed class GoldLabelModel : IChatClient
    {
        private readonly IReadOnlyList<JudgeGoldCase> _cases;

        public GoldLabelModel(JudgeGoldSet gold) => _cases = gold.Cases;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Concat(messages.Select(m => m.Text));
            var match = _cases.FirstOrDefault(c => prompt.Contains(c.Text, StringComparison.Ordinal));
            var overRefuses = match?.ShouldBlock ?? false;   // unknown ⇒ benign default (allow)
            var json = overRefuses
                ? "{\"overRefuses\": true, \"confidence\": 0.9, \"evidence\": \"declined without a valid reason\"}"
                : "{\"overRefuses\": false, \"confidence\": 0.9, \"evidence\": \"\"}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
