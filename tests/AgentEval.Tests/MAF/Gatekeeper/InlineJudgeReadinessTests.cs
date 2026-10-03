// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>P1-1 / §9 — an inline LLM judge must be proven calibration-ready before it may block.</summary>
public class InlineJudgeReadinessTests
{
    private sealed class StubRubric : IJudgeRubric
    {
        public string Axis => "stub-axis";
        public bool Prefilter(string text) => false;   // never reaches the model; behavior is irrelevant to the guard
        public string BuildPrompt(string text) => text;
        public JudgeVerdict Parse(string modelReply) => JudgeVerdict.Inconclusive();
    }

    private static CompositeJudgeGate<StubRubric> Judge() => new(new StubRubric(), new ScriptedChatClient().AddText("x"));

    // Scores the gold set perfectly, so the calibration harness can produce a genuinely inline-ready report.
    private sealed class PerfectGate : IChatGate
    {
        public string PolicyName => "perfect";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(text.StartsWith("attack", StringComparison.Ordinal) ? GateVerdict.Block(PolicyName, "attack") : GateVerdict.Allow(PolicyName));
    }

    // Counts loads, so a passing check can prove it looked the judge up rather than skipping it.
    private sealed class InMemoryReportStore : ICalibrationReportStore
    {
        private readonly Dictionary<string, CalibrationReport> _reports = new(StringComparer.Ordinal);

        public int Loads { get; private set; }

        public Task SaveAsync(CalibrationReport report, CancellationToken ct = default)
        {
            _reports[report.Axis] = report;
            return Task.CompletedTask;
        }

        public Task<CalibrationReport?> LoadLatestAsync(string axis, CancellationToken ct = default)
        {
            Loads++;
            return Task.FromResult(_reports.TryGetValue(axis, out var report) ? report : null);
        }

        public Task<IReadOnlyDictionary<string, CalibrationReport>> LoadLatestAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, CalibrationReport>>(_reports);
    }

    private static async Task<InMemoryReportStore> StoreWithReportAsync(string axis, bool inlineReady)
    {
        var cases = Enumerable.Range(0, 20).Select(i => new JudgeGoldCase($"attack {i}", true))
            .Concat(Enumerable.Range(0, 20).Select(i => new JudgeGoldCase($"benign {i}", false)));
        // A zero-miss bar is a real promotion bar; all-default options set none, so that report is never inline-ready.
        var report = await GateCalibrationHarness.EvaluateAsync(
            new PerfectGate(),
            new JudgeGoldSet(axis, cases),
            inlineReady ? new CalibrationOptions { MaxDangerousErrors = 0 } : new CalibrationOptions());
        Assert.Equal(inlineReady, report.IsInlineReady);   // the fixture must be what the test says it is

        var store = new InMemoryReportStore();
        await store.SaveAsync(report);
        return store;
    }

    [Fact]
    public void CompositeJudgeGate_ExposesAxisName_ForTheCalibrationGuard()
    {
        IRequiresCalibration gate = Judge();
        Assert.Equal("stub-axis", gate.AxisName);
    }

    [Fact]
    public async Task ValidateInlineJudges_UncalibratedInlineJudge_NoStore_Throws()
    {
        var options = new GatekeeperOptions();
        options.PreGates.Add(Judge());
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Equal("stub-axis", ex.AxisName);
        Assert.DoesNotContain("found inside", ex.Message, StringComparison.Ordinal);   // an unwrapped judge keeps its message
    }

    [Fact]
    public async Task ValidateInlineJudges_EscapeHatch_Passes()
    {
        var options = new GatekeeperOptions { AllowUncalibratedInlineJudge = true };
        options.PostGates.Add(Judge());
        await options.ValidateInlineJudgesAsync();   // loud opt-out honored — does not throw
    }

    [Fact]
    public async Task ValidateInlineJudges_NoInlineJudges_Passes()
    {
        var options = new GatekeeperOptions();
        await options.ValidateInlineJudgesAsync();   // trivially passes when nothing needs calibration
    }

    // ── Wrappers: the check must look through every gate that hands its decision to a judge ──

    [Fact]
    public async Task ValidateInlineJudges_CachedUncalibratedJudge_Throws()
    {
        // A JudgeVerdictCache is not itself IRequiresCalibration; the check used to stop at it and pass.
        var options = new GatekeeperOptions();
        options.PreGates.Add(new JudgeVerdictCache(Judge()));
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Equal("stub-axis", ex.AxisName);
        Assert.Contains("found inside pre gate 'judge:stub-axis'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateInlineJudges_StockJudgeBuiltWithDefaults_IsChecked()
    {
        // Every stock judge factory defaults to cache: true, so this is what a caller gets with no extra arguments.
        var options = new GatekeeperOptions();
        options.PostGates.Add(OverRefusalJudge.Create(new ScriptedChatClient()));
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Equal(OverRefusalJudge.Axis, ex.AxisName);
    }

    [Fact]
    public async Task ValidateInlineJudges_UncalibratedJudgeOnAPanel_Throws()
    {
        // The panel blocks when ANY judge blocks, so one uncalibrated judge among deterministic gates is enough.
        var options = new GatekeeperOptions();
        options.PostGates.Add(new ParallelJudgeFanOut([new KeywordOracleGate(policyName: "keyword-oracle:stub"), Judge()]));
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Equal("stub-axis", ex.AxisName);
        Assert.Contains("found inside post gate 'judge-panel'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateInlineJudges_NestedWrappers_AreSeenThrough()
    {
        var options = new GatekeeperOptions();
        options.PreGates.Add(new ParallelJudgeFanOut([new JudgeVerdictCache(new JudgeVerdictCache(Judge()))]));
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Equal("stub-axis", ex.AxisName);
    }

    [Fact]
    public async Task ValidateInlineJudges_OutboundBoundaryGate_JudgeBehindFormatterAndCache_IsChecked()
    {
        // CreateOutbound returns a goal-binding wrapper around a cached judge: two layers, both stock.
        var options = new GatekeeperOptions();
        options.PreGates.Add(InterAgentBoundaryInjectionGate.CreateOutbound(new ScriptedChatClient(), "Summarise the quarterly report."));
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Equal(InterAgentBoundaryInjectionGate.OutboundAxis, ex.AxisName);
    }

    [Fact]
    public async Task ValidateInlineJudges_CachedJudgeWithNonReadyReport_Throws()
    {
        var store = await StoreWithReportAsync("stub-axis", inlineReady: false);
        var options = new GatekeeperOptions { CalibrationReportStore = store };
        options.PreGates.Add(new JudgeVerdictCache(Judge()));
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Contains("not inline-ready", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateInlineJudges_CertifiedJudgesBehindWrappers_Pass_AndAreActuallyLookedUp()
    {
        var store = await StoreWithReportAsync("stub-axis", inlineReady: true);
        var options = new GatekeeperOptions { CalibrationReportStore = store };
        options.PreGates.Add(new JudgeVerdictCache(Judge()));
        options.PostGates.Add(new ParallelJudgeFanOut([new JudgeVerdictCache(Judge())]));

        await options.ValidateInlineJudgesAsync();

        Assert.Equal(2, store.Loads);   // passed because both were checked and certified, not because both were skipped
    }

    [Fact]
    public async Task ValidateInlineJudges_EscapeHatch_AlsoCoversWrappedJudges()
    {
        var options = new GatekeeperOptions { AllowUncalibratedInlineJudge = true };
        options.PreGates.Add(new JudgeVerdictCache(Judge()));
        options.ApprovalGates.Add(new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "Refund order 42."));
        await options.ValidateInlineJudgesAsync();
    }

    // ── Approval gates: a judge whose allow verdict auto-approves a tool call must be calibrated too ──

    [Fact]
    public async Task ValidateInlineJudges_ApprovalGateJudge_Uncalibrated_Throws()
    {
        var options = new GatekeeperOptions();
        options.ApprovalGates.Add(new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "Refund order 42."));
        var ex = await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Equal(ToolArgumentGoalCoherenceJudge.Axis, ex.AxisName);
        Assert.Contains("approval gate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateInlineJudges_ApprovalGateJudge_Certified_Passes()
    {
        var store = await StoreWithReportAsync(ToolArgumentGoalCoherenceJudge.Axis, inlineReady: true);
        var options = new GatekeeperOptions { CalibrationReportStore = store };
        options.ApprovalGates.Add(new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "Refund order 42."));

        await options.ValidateInlineJudgesAsync();

        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public async Task ValidateInlineJudges_DeterministicGatesAndWrappers_NeedNoCalibration()
    {
        // No store is configured, so any judge found would throw: nothing here holds one.
        var options = new GatekeeperOptions();
        options.PreGates.Add(new ParallelJudgeFanOut([new KeywordOracleGate(policyName: "keyword-oracle:stub")]));
        options.PostGates.Add(new JudgeVerdictCache(new KeywordOracleGate(policyName: "keyword-oracle:cached")));
        options.ApprovalGates.Add(new ToolNameApprovalGate(["delete_account"]));
        await options.ValidateInlineJudgesAsync();
    }
}
