// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.AgentHooks;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AgentHooks;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.AgentHooks;

/// <summary>
/// Gatekeeper enforcing inside Microsoft Agent Framework's OWN AgentHooks host (<c>Microsoft.Agents.AI.AgentHooks</c>,
/// MAF 1.19+), not inside an AgentEval harness. The host consumes <c>AgentHooks.IInterceptor</c>, which
/// <see cref="GatekeeperInterceptor"/> implements, so the same gates that guard an AgentEval-built agent guard an agent
/// built with <c>AsAIAgentWithAgentHooks</c> — and a denied tool's body never runs.
/// </summary>
public class GatekeeperInsideMafAgentHooksHostTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static (AIAgent Agent, List<string> Executed) Build(ScriptedChatClient model, params IToolGate[] gates)
    {
        var executed = new List<string>();
        var deleteFile = AIFunctionFactory.Create(
            (string path) => { executed.Add($"delete_file:{path}"); return $"Deleted {path}."; },
            "delete_file", "Delete a file.");
        var readFile = AIFunctionFactory.Create(
            (string path) => { executed.Add($"read_file:{path}"); return $"Contents of {path}."; },
            "read_file", "Read a file.");

        var agent = model.AsAIAgentWithAgentHooks(
            new AgentHooksOptions([new GatekeeperInterceptor(gates)]),
            new ChatClientAgentOptions
            {
                Name = "FileAgent",
                ChatOptions = new ChatOptions { Tools = [deleteFile, readFile] },
            });
        return (agent, executed);
    }

    [Fact]
    public async Task ADeniedToolCall_NeverRunsItsBody_InsideMafsHost()
    {
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "delete_file", Args(("path", "/etc/passwd")))
            .AddText("I could not delete that file.");
        var (agent, executed) = Build(model, new ForbiddenToolGate("delete_file"));

        await agent.RunAsync("Delete /etc/passwd.");

        Assert.DoesNotContain(executed, e => e.StartsWith("delete_file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAllowedToolCall_StillRuns_SoTheDenyAboveIsTheGateNotTheHost()
    {
        // The control: the same host, the same interceptor, a tool the gate does not forbid. Without it, the test
        // above would also pass if the host simply never executed tools.
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "read_file", Args(("path", "/tmp/notes.txt")))
            .AddText("Here are your notes.");
        var (agent, executed) = Build(model, new ForbiddenToolGate("delete_file"));

        await agent.RunAsync("Read my notes.");

        Assert.Contains("read_file:/tmp/notes.txt", executed);
    }
}
