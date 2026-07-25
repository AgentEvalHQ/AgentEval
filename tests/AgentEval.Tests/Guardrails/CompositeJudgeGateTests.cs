// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>The Tribunal primitive: a single-axis rubric + fast model → runtime IChatGate, fail-closed on abstention.</summary>
public class CompositeJudgeGateTests
{
    // A trivial single-axis rubric: prefilter on "scan"; the model answers BLOCK / ALLOW; anything else is unparseable.
    private sealed class KeywordRubric : IJudgeRubric
    {
        public string Axis => "test-axis";
        public bool Prefilter(string text) => text.Contains("scan", StringComparison.OrdinalIgnoreCase);
        public string BuildPrompt(string text) => $"Is this bad? {text}";
        public JudgeVerdict Parse(string reply)
            => reply.Contains("BLOCK", StringComparison.OrdinalIgnoreCase) ? JudgeVerdict.Blocked("axis present", ["the-evidence"], 0.9)
             : reply.Contains("ALLOW", StringComparison.OrdinalIgnoreCase) ? JudgeVerdict.Allowed()
             : JudgeVerdict.Inconclusive("unparseable reply");
    }

    // A rubric that decides Blocked but supplies a non-finite confidence (e.g. a hits/total with total==0).
    private sealed class NanConfidenceRubric : IJudgeRubric
    {
        public string Axis => "nan-axis";
        public bool Prefilter(string text) => true;
        public string BuildPrompt(string text) => "?";
        public JudgeVerdict Parse(string reply) => JudgeVerdict.Blocked("detected", ["span"], double.NaN);
    }

    // A rubric whose prefilter throws — the gate must fail toward inspecting (consult the model), not skip it.
    private sealed class ThrowingPrefilterRubric : IJudgeRubric
    {
        public string Axis => "throwing-prefilter";
        public bool Prefilter(string text) => throw new InvalidOperationException("prefilter boom");
        public string BuildPrompt(string text) => "?";
        public JudgeVerdict Parse(string reply) => reply.Contains("BLOCK") ? JudgeVerdict.Blocked("x", null, 0.9) : JudgeVerdict.Allowed();
    }

    // Captures exactly what text Prefilter and BuildPrompt each receive — used to prove P5-3 bounds only the
    // model prompt, never the prefilter.
    private sealed class CapturingRubric : IJudgeRubric
    {
        public string? PrefilterSaw { get; private set; }
        public string? PromptSaw { get; private set; }
        public string Axis => "capture-axis";
        public bool Prefilter(string text) { PrefilterSaw = text; return true; }
        public string BuildPrompt(string text) { PromptSaw = text; return text; }
        public JudgeVerdict Parse(string reply) => JudgeVerdict.Allowed();
    }

    private static CompositeJudgeGate<KeywordRubric> Gate(IChatClient model, JudgeGateOptions? opts = null)
        => new(new KeywordRubric(), model, opts);

    [Fact]
    public async Task BlockedVerdict_Blocks_WithEvidenceSpans()
    {
        var v = await Gate(new ScriptedChatClient().AddText("BLOCK")).InspectAsync("please scan this input");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Equal("judge:test-axis", v.PolicyName);
        Assert.Contains("the-evidence", v.Matches!);
    }

    [Fact]
    public async Task AllowedVerdict_Allows()
    {
        var v = await Gate(new ScriptedChatClient().AddText("ALLOW")).InspectAsync("please scan this input");
        Assert.Equal(GateAction.Allow, v.Action);
    }

    [Fact]
    public async Task BlockedVerdict_HasProvenance_WithRuleNameEvidenceThresholdAndActual()
    {
        var opts = new JudgeGateOptions { BlockThreshold = 0.5 };
        var v = await Gate(new ScriptedChatClient().AddText("BLOCK"), opts).InspectAsync("please scan this input");

        Assert.NotNull(v.Provenance);
        Assert.Equal("judge:test-axis", v.Provenance!.RuleName);
        Assert.Contains("the-evidence", v.Provenance.Evidence);
        Assert.Equal(0.5, v.Provenance.Threshold);
        Assert.Equal(0.9, v.Provenance.ActualValue);
    }

    [Fact]
    public async Task AllowedVerdict_CarriesNoProvenance_MirrorsNoConfidence()
    {
        // Same discipline as AllowedVerdict_CarriesNoConfidence_NotAFleetCorrelationSignal: a genuine Allowed
        // decision has nothing to explain — fabricating a provenance chain for "nothing happened" would be
        // noise, not signal.
        var v = await Gate(new ScriptedChatClient().AddText("ALLOW")).InspectAsync("please scan this input");
        Assert.Null(v.Provenance);
    }

    [Fact]
    public async Task AllowedVerdict_CarriesNoConfidence_NotAFleetCorrelationSignal()
    {
        // Regression test for a real bug found in review: this test originally asserted Confidence == 1.0 here,
        // which was WRONG — JudgeVerdict.Allowed() defaults to confidence 1.0 ("very sure this is fine"), the
        // opposite of a near-miss signal. Attaching it made FleetCorrelator treat every confidently-clean turn
        // from 2+ judges as a "soft signal," false-positive-blocking almost all benign multi-judge traffic. A
        // genuine Allowed decision must carry NO confidence — only a low-confidence Block or fail-open
        // Inconclusive is a real near-miss worth correlating (see LowConfidenceBlock_BelowThreshold_Allows and
        // ModelError_FailOpenOption_Allows below).
        var v = await Gate(new ScriptedChatClient().AddText("ALLOW")).InspectAsync("please scan this input");
        Assert.Null(v.Confidence);
    }

    [Fact]
    public async Task PrefilterFalse_ShortCircuits_ModelNeverCalled()
    {
        var model = new ScriptedChatClient().AddText("BLOCK");   // would block IF called
        var v = await Gate(model).InspectAsync("nothing interesting here");   // no "scan" ⇒ prefilter false

        Assert.Equal(GateAction.Allow, v.Action);
        Assert.Empty(model.ReceivedMessages);   // proves the fast model was never invoked (0 tokens)
        Assert.Null(v.Confidence);   // no judge ran — there is no soft signal to report, not even a fabricated one
    }

    [Fact]
    public async Task ModelError_IsInconclusive_FailClosed_Blocks()
    {
        var v = await Gate(new ScriptedChatClient().AddThrow()).InspectAsync("please scan this");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("error", v.Reason!, StringComparison.OrdinalIgnoreCase);   // "…judge error: InvalidOperationException"
    }

    [Fact]
    public async Task ModelError_FailOpenOption_Allows()
    {
        var opts = new JudgeGateOptions { FailClosedOnInconclusive = false };
        var v = await Gate(new ScriptedChatClient().AddThrow(), opts).InspectAsync("please scan this");
        Assert.Equal(GateAction.Allow, v.Action);
        Assert.Equal(0.0, v.Confidence);   // JudgeVerdict.Inconclusive's own confidence (0.0) carries through honestly
    }

    [Fact]
    public async Task UnparseableReply_IsInconclusive_FailClosed_Blocks()
    {
        var v = await Gate(new ScriptedChatClient().AddText("I'm not sure, maybe?")).InspectAsync("please scan this");
        Assert.Equal(GateAction.Block, v.Action);
    }

    [Fact]
    public async Task Timeout_IsInconclusive_FailClosed_Blocks()
    {
        var slow = new DelayingChatClient(TimeSpan.FromSeconds(30));   // far longer than the gate timeout
        var opts = new JudgeGateOptions { Timeout = TimeSpan.FromMilliseconds(50) };

        var v = await Gate(slow, opts).InspectAsync("please scan this");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("timed out", v.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LowConfidenceBlock_BelowThreshold_Allows()
    {
        // Threshold 0.95 > the rubric's 0.9 confidence ⇒ not decisive enough to act.
        var opts = new JudgeGateOptions { BlockThreshold = 0.95 };
        var v = await Gate(new ScriptedChatClient().AddText("BLOCK"), opts).InspectAsync("please scan this");
        Assert.Equal(GateAction.Allow, v.Action);

        // This is the exact near-miss case Fleet Correlation depends on: a sub-threshold Blocked decision that
        // becomes an Allow must still surface its real confidence (0.9), not null or a fabricated 1.0 — a fleet
        // correlator combining several of these across gates needs the true value, not a laundered "clean allow".
        Assert.Equal(0.9, v.Confidence);

        // The near-miss's provenance makes it reconstructable, not just a bare number: which threshold it
        // almost crossed, and the evidence the judge actually saw.
        Assert.NotNull(v.Provenance);
        Assert.Equal(0.95, v.Provenance!.Threshold);
        Assert.Equal(0.9, v.Provenance.ActualValue);
    }

    [Fact]
    public async Task CallerCancellation_IsHonored_NotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Gate(new DelayingChatClient(TimeSpan.FromSeconds(30))).InspectAsync("please scan this", cts.Token));
    }

    [Fact]
    public async Task BlockedVerdict_NanConfidence_FailsClosed_Blocks()
    {
        // NaN >= threshold is always false — a Blocked verdict with NaN confidence must NOT fall through to Allow.
        var gate = new CompositeJudgeGate<NanConfidenceRubric>(new NanConfidenceRubric(), new ScriptedChatClient().AddText("x"));
        var v = await gate.InspectAsync("please scan this");
        Assert.Equal(GateAction.Block, v.Action);
    }

    [Fact]
    public void NonPositiveTimeout_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Gate(new ScriptedChatClient(), new JudgeGateOptions { Timeout = TimeSpan.Zero }));

    [Fact]
    public void BlockThresholdAboveOne_Throws()   // a fat-fingered "95" would otherwise silently disable all blocking
        => Assert.Throws<ArgumentOutOfRangeException>(() => Gate(new ScriptedChatClient(), new JudgeGateOptions { BlockThreshold = 95 }));

    [Fact]
    public async Task BrokenPrefilter_StillConsultsModel_FailsTowardInspecting()
    {
        var model = new ScriptedChatClient().AddText("BLOCK");
        var gate = new CompositeJudgeGate<ThrowingPrefilterRubric>(new ThrowingPrefilterRubric(), model);

        var v = await gate.InspectAsync("anything");

        Assert.Equal(GateAction.Block, v.Action);   // a throwing prefilter must not silently disable the judge
        Assert.NotEmpty(model.ReceivedMessages);     // the model WAS consulted
    }

    [Fact]
    public async Task WiredRunPre_ThroughEvalGate_BlocksBeforeInnerModel()
    {
        // The judge as a run-pre gate through EvalGatingChatClient: a block refuses the run before the model runs.
        var agentModel = new ScriptedChatClient().AddText("should never run");
        var judge = new CompositeJudgeGate<KeywordRubric>(new KeywordRubric(), new ScriptedChatClient().AddText("BLOCK"));
        var client = agentModel.AsBuilder()
            .UseEvalGate(pre: new IChatGate[] { judge }, policy: EvalGatePolicy.ThrowOnFail)
            .Build();

        await Assert.ThrowsAsync<EvalGateRefusalException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "please scan this input")]));
        Assert.Equal(0, agentModel.CallCount);
    }

    [Fact]
    public void NullRubric_Throws()
        => Assert.Throws<ArgumentNullException>(() => new CompositeJudgeGate<KeywordRubric>(null!, new ScriptedChatClient()));

    [Fact]
    public void NullModel_Throws()
        => Assert.Throws<ArgumentNullException>(() => new CompositeJudgeGate<KeywordRubric>(new KeywordRubric(), null!));

    // ── P5-2: shared spend governor ──

    [Fact]
    public async Task SpendGovernor_BudgetExhausted_FailOpen_Allows_AndSkipsModel()
    {
        // maxTokens=1 is smaller than any real estimate (chars/4 + 256 output cap), so the first reservation is
        // already refused — the model must be skipped and the turn allowed (fail-open default), with provenance.
        var model = new ScriptedChatClient().AddText("BLOCK");   // would block IF called
        var gov = new JudgeSpendGovernor(maxCalls: 100, maxTokens: 1);
        var v = await Gate(model, new JudgeGateOptions { SpendGovernor = gov }).InspectAsync("please scan this");

        Assert.Equal(GateAction.Allow, v.Action);
        Assert.Empty(model.ReceivedMessages);   // proves the model was never called — spend was refused
        Assert.NotNull(v.Provenance);
        Assert.Contains("budget-exhausted", v.Provenance!.RuleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpendGovernor_BudgetExhausted_FailClosed_Blocks_AndSkipsModel()
    {
        var model = new ScriptedChatClient().AddText("ALLOW");   // would allow IF called
        var gov = new JudgeSpendGovernor(maxCalls: 100, maxTokens: 1);
        var opts = new JudgeGateOptions { SpendGovernor = gov, FailClosedOnBudgetExhausted = true };

        var v = await Gate(model, opts).InspectAsync("please scan this");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("budget", v.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(model.ReceivedMessages);   // fail-closed still skips the (unaffordable) model call
    }

    [Fact]
    public async Task SpendGovernor_WithinBudget_JudgeRunsNormally()
    {
        var model = new ScriptedChatClient().AddText("BLOCK");
        var gov = new JudgeSpendGovernor(maxCalls: 100, maxTokens: 1_000_000);

        var v = await Gate(model, new JudgeGateOptions { SpendGovernor = gov }).InspectAsync("please scan this");

        Assert.Equal(GateAction.Block, v.Action);      // budget ok ⇒ the judge actually ran
        Assert.NotEmpty(model.ReceivedMessages);
    }

    [Fact]
    public async Task SpendGovernor_PrefilterSkip_DoesNotConsumeBudget()
    {
        // A turn that never reaches the model (prefilter false) must not spend the wallet — otherwise benign
        // traffic would starve the budget before any judge-worthy turn arrives.
        var gov = new JudgeSpendGovernor(maxCalls: 1, maxTokens: 1_000_000);
        var opts = new JudgeGateOptions { SpendGovernor = gov };

        _ = await Gate(new ScriptedChatClient().AddText("BLOCK"), opts).InspectAsync("nothing interesting");   // no "scan"

        // The single call in the budget is still available — a real (scanned) turn now runs the model.
        var model = new ScriptedChatClient().AddText("BLOCK");
        var v = await Gate(model, opts).InspectAsync("please scan this");
        Assert.Equal(GateAction.Block, v.Action);
        Assert.NotEmpty(model.ReceivedMessages);
    }

    // ── P5-3: bound judge input ──

    [Fact]
    public async Task InputBound_UnderCap_PassesFullTextToModel()
    {
        var rubric = new CapturingRubric();
        var text = "please scan this " + new string('x', 100);
        var gate = new CompositeJudgeGate<CapturingRubric>(rubric, new ScriptedChatClient().AddText("ALLOW"),
            new JudgeGateOptions { MaxInputChars = 16_000 });

        await gate.InspectAsync(text);

        Assert.Equal(text, rubric.PromptSaw);   // well under the cap ⇒ untouched
    }

    [Fact]
    public async Task InputBound_OverCap_TruncatesToHeadTailSandwich_ButPrefilterSeesFullText()
    {
        var rubric = new CapturingRubric();
        var text = "HEADSTART " + new string('x', 5000) + " TAILEND";
        var gate = new CompositeJudgeGate<CapturingRubric>(rubric, new ScriptedChatClient().AddText("ALLOW"),
            new JudgeGateOptions { MaxInputChars = 200 });

        await gate.InspectAsync(text);

        // Prefilter saw the FULL text (it decides whether to invoke the judge at all).
        Assert.Equal(text, rubric.PrefilterSaw);

        // The MODEL saw a bounded head+tail sandwich: both boundaries survive, the middle is dropped + marked.
        Assert.NotNull(rubric.PromptSaw);
        Assert.StartsWith("HEADSTART", rubric.PromptSaw);
        Assert.EndsWith("TAILEND", rubric.PromptSaw);
        Assert.Contains("truncated", rubric.PromptSaw, StringComparison.Ordinal);
        Assert.True(rubric.PromptSaw!.Length < text.Length);
        // Head + tail ≈ MaxInputChars (200) plus the short marker — nowhere near the 5000-char original.
        Assert.True(rubric.PromptSaw.Length < 300);
    }

    [Fact]
    public async Task InputBound_Zero_MeansUnbounded()
    {
        var rubric = new CapturingRubric();
        var text = "please scan this " + new string('y', 5000);
        var gate = new CompositeJudgeGate<CapturingRubric>(rubric, new ScriptedChatClient().AddText("ALLOW"),
            new JudgeGateOptions { MaxInputChars = 0 });

        await gate.InspectAsync(text);

        Assert.Equal(text, rubric.PromptSaw);   // 0 = unbounded ⇒ full text through
    }

    [Fact]
    public void NegativeMaxInputChars_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Gate(new ScriptedChatClient(), new JudgeGateOptions { MaxInputChars = -1 }));

    // A fast model that respects cancellation but otherwise never returns in time (for the timeout path).
    private sealed class DelayingChatClient : IChatClient
    {
        private readonly TimeSpan _delay;
        public DelayingChatClient(TimeSpan delay) => _delay = delay;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "BLOCK"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
