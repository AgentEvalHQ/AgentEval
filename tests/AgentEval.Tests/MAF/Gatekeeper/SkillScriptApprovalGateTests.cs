// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public class SkillScriptApprovalGateTests
{
    [Fact]
    public async Task LoadSkill_AlwaysAutoApprovable()
    {
        var gate = new SkillScriptApprovalGate();
        var call = new FunctionCallContent("c1", "load_skill", new Dictionary<string, object?> { ["skillName"] = "expense-report" });
        Assert.True(await gate.IsAutoApprovableAsync(call));
    }

    [Fact]
    public async Task ReadSkillResource_AlwaysAutoApprovable()
    {
        var gate = new SkillScriptApprovalGate();
        var call = new FunctionCallContent("c1", "read_skill_resource", new Dictionary<string, object?>
        {
            ["skillName"] = "expense-report",
            ["resourceName"] = "resources/policy.md",
        });
        Assert.True(await gate.IsAutoApprovableAsync(call));
    }

    [Fact]
    public async Task RunSkillScript_NoTrustList_Escalates()
    {
        var gate = new SkillScriptApprovalGate();
        var call = new FunctionCallContent("c1", "run_skill_script", new Dictionary<string, object?>
        {
            ["skillName"] = "expense-report",
            ["scriptName"] = "scripts/summarize.csx",
        });
        Assert.False(await gate.IsAutoApprovableAsync(call));
    }

    [Fact]
    public async Task RunSkillScript_UntrustedScript_Escalates()
    {
        var gate = new SkillScriptApprovalGate(trustedScripts: ["scripts/summarize.csx"]);
        var call = new FunctionCallContent("c1", "run_skill_script", new Dictionary<string, object?>
        {
            ["skillName"] = "expense-report",
            ["scriptName"] = "scripts/delete-all.csx",
        });
        Assert.False(await gate.IsAutoApprovableAsync(call));
    }

    [Fact]
    public async Task RunSkillScript_TrustedScript_AutoApproves()
    {
        var gate = new SkillScriptApprovalGate(trustedScripts: ["scripts/summarize.csx"]);
        var call = new FunctionCallContent("c1", "run_skill_script", new Dictionary<string, object?>
        {
            ["skillName"] = "expense-report",
            ["scriptName"] = "scripts/summarize.csx",
        });
        Assert.True(await gate.IsAutoApprovableAsync(call));
    }

    [Fact]
    public async Task UnrelatedTool_Escalates_NotSilentlyApproved()
    {
        var gate = new SkillScriptApprovalGate(trustedScripts: ["scripts/summarize.csx"]);
        var call = new FunctionCallContent("c1", "send_email", new Dictionary<string, object?> { ["to"] = "x@y.com" });
        Assert.False(await gate.IsAutoApprovableAsync(call));
    }

    [Fact]
    public async Task ComposesWithUseAgentEvalToolApproval_AllGatesMustAgree()
    {
#pragma warning disable AEGK001, MAAI001
        var gate = new SkillScriptApprovalGate(trustedScripts: ["scripts/summarize.csx"]);
        var builder = new Microsoft.Agents.AI.ChatClientAgent(
                new AgentEval.Testing.ScriptedChatClient(),
                new Microsoft.Agents.AI.ChatClientAgentOptions { Name = "A" })
            .AsBuilder()
            .UseAgentEvalToolApproval([gate]);
        var agent = builder.Build();
        Assert.NotNull(agent);   // composition itself must not throw
#pragma warning restore AEGK001, MAAI001
    }

    [Fact]
    public async Task NullCall_Throws()
    {
        var gate = new SkillScriptApprovalGate();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await gate.IsAutoApprovableAsync(null!));
    }
}
