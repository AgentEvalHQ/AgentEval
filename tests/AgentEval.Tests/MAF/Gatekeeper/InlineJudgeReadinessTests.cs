// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
}
