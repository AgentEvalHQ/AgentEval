// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public class SkillScriptExecutionGateTests
{
    private static GatedToolCall Call(string name, IReadOnlyDictionary<string, object?>? args)
        => new(name, args, AgentName: "A", Iteration: 0, FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    [Fact]
    public async Task NonSkillScriptTool_AlwaysAllows()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        var v = await gate.InspectAsync(Call("send_email", new Dictionary<string, object?> { ["to"] = "x@y.com" }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task AllowlistedScript_ByBareScriptNameValue_Allows()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        var v = await gate.InspectAsync(Call("run_skill_script", new Dictionary<string, object?>
        {
            ["skillName"] = "expense-report",
            ["scriptName"] = "scripts/summarize.csx",
        }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task AllowlistedScript_ByCombinedSkillSlashScript_Allows()
    {
        var gate = new SkillScriptExecutionGate(["expense-report/scripts/summarize.csx"]);
        var v = await gate.InspectAsync(Call("run_skill_script", new Dictionary<string, object?>
        {
            ["skillName"] = "expense-report",
            ["scriptName"] = "scripts/summarize.csx",
        }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task NonAllowlistedScript_Blocks()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        var v = await gate.InspectAsync(Call("run_skill_script", new Dictionary<string, object?>
        {
            ["skillName"] = "expense-report",
            ["scriptName"] = "scripts/delete-all.csx",
        }));
        Assert.Equal(ToolGateAction.Block, v.Action);
        // Never echoes the attempted (unlisted) script value into the trace reason.
        Assert.DoesNotContain("delete-all", v.Reason);
    }

    [Fact]
    public async Task ArgKeyAgnostic_DifferentArgumentKey_StillMatchesByValue()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        // A hypothetical future MAF rename: the script identifier now sits under "script_id" instead of "scriptName".
        var v = await gate.InspectAsync(Call("run_skill_script", new Dictionary<string, object?>
        {
            ["skill_id"] = "expense-report",
            ["script_id"] = "scripts/summarize.csx",
        }));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task ArgKeyAgnostic_UnrecognizedValueUnderRenamedKey_Blocks()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        var v = await gate.InspectAsync(Call("run_skill_script", new Dictionary<string, object?>
        {
            ["skill_id"] = "expense-report",
            ["script_id"] = "scripts/rm-rf.csx",
        }));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task EmptyArguments_FailsClosed()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        var v = await gate.InspectAsync(Call("run_skill_script", new Dictionary<string, object?>()));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task NullArguments_FailsClosed()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        var v = await gate.InspectAsync(Call("run_skill_script", null));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public void EmptyAllowlist_ThrowsAtConstruction()
        => Assert.Throws<ArgumentException>(() => new SkillScriptExecutionGate([]));

    [Fact]
    public void NullAllowlist_Throws()
        => Assert.Throws<ArgumentNullException>(() => new SkillScriptExecutionGate(null!));

    [Fact]
    public void MinimumPolicy_IsReplaceResult_NotWarnOnly()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        Assert.Equal(ToolGatePolicy.ReplaceResult, gate.MinimumPolicy);
    }

    [Fact]
    public void RegisteringUnderWarnOnly_ThrowsAtUseAgentEvalToolGate()
    {
        var gate = new SkillScriptExecutionGate(["scripts/summarize.csx"]);
        var builder = new Microsoft.Agents.AI.ChatClientAgent(
            new AgentEval.Testing.ScriptedChatClient(),
            new Microsoft.Agents.AI.ChatClientAgentOptions { Name = "A" }).AsBuilder();
        Assert.Throws<ArgumentException>(() => builder.UseAgentEvalToolGate([gate], ToolGatePolicy.WarnOnly));
    }
}
