// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public class CodeToolIntentReuseTests
{
    [Fact]
    public void GoldSet_IsBalancedAndLargeEnoughForDefaultPromotionBar()
    {
        var gold = CodeToolIntentGoldSet.CalibrationGoldSet();

        Assert.Equal(ToolArgumentGoalCoherenceJudge.Axis, gold.Axis);
        Assert.Equal(20, gold.AttackCount);
        Assert.Equal(20, gold.BenignCount);
        Assert.All(gold.Cases, c =>
        {
            Assert.Contains(CodeToolIntentGoldSet.IntendedUse, c.Text, StringComparison.Ordinal);
            Assert.Contains("run_analysis_code", c.Text, StringComparison.Ordinal);
            Assert.Contains("\"code\"", c.Text, StringComparison.Ordinal);
        });
        Assert.Equal(gold.Cases.Count, gold.Cases.Select(c => c.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task KeywordBaseline_MissesCodeAuthorityViolations()
    {
        var gold = CodeToolIntentGoldSet.CalibrationGoldSet();
        var baseline = ToolArgumentGoalCoherenceJudge.KeywordBaseline();
        var correct = 0;
        var missedAttacks = 0;

        foreach (var item in gold.Cases)
        {
            var verdict = await baseline.InspectAsync(item.Text);
            var blocked = verdict.Action == GateAction.Block;
            if (blocked == item.ShouldBlock) correct++;
            if (item.ShouldBlock && !blocked) missedAttacks++;
        }

        Assert.Equal(20, correct);
        Assert.Equal(20, missedAttacks);
    }

    [Fact]
    public async Task ApprovalGate_ReusesDeclaredIntendedUseAsFixedGoal()
    {
        var model = new ScriptedChatClient().AddText("""{"incoherent": false, "confidence": 0.95}""");
        var gate = new ToolArgumentGoalCoherenceApprovalGate(model, CodeToolIntentGoldSet.IntendedUse);
        var call = new FunctionCallContent(
            "call_1",
            "run_analysis_code",
            new Dictionary<string, object?> { ["code"] = "df.groupby('region')['revenue'].sum()" });

        Assert.True(await gate.IsAutoApprovableAsync(call));
        var prompt = string.Concat(model.ReceivedMessages.SelectMany(batch => batch).Select(message => message.Text));
        Assert.Contains(CodeToolIntentGoldSet.IntendedUse, prompt, StringComparison.Ordinal);
        Assert.Contains("run_analysis_code", prompt, StringComparison.Ordinal);
        Assert.Contains("groupby", prompt, StringComparison.Ordinal);
    }
}
