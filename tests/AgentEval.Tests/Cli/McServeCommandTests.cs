// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using AgentEval.Cli.Commands;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Locks down defense-in-depth canonicalisation on the <c>--workspace</c>
/// parameter of <c>agenteval mc serve</c>: a non-existent directory must
/// surface as exit 1 before any server boot, matching the validator gate
/// used by every other workspace-aware command.
/// </summary>
public class McServeCommandTests
{
    [Fact]
    public async Task McServe_WorkspaceRoot_NonExistent_ReturnsExitOne()
    {
        // The validator must reject before the port-probe and subprocess-spawn
        // logic ever runs, so this test is safe to execute without booting the
        // MC web app.
        var bogus = Path.Combine(Path.GetTempPath(),
            "definitely-not-a-real-dir-" + Guid.NewGuid().ToString("N"));
        var result = await McServeCommand.RunAsync(port: 65432, workspaceRoot: bogus);
        Assert.Equal(1, result);
    }
}

#endif
