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

    // mc serve used to announce the portal whether or not the SPA had been built; every page then answered 404.
    [Fact]
    public void MissingWebUiReason_NamesAMissingBuild_AndIsNullOnlyWhenIndexHtmlExists()
    {
        var dir = Directory.CreateTempSubdirectory("mc-serve-ui-").FullName;
        try
        {
            Assert.Contains("has not been built", McServeCommand.MissingWebUiReason(dir), StringComparison.Ordinal);

            Directory.CreateDirectory(Path.Combine(dir, "wwwroot"));
            Assert.Contains("index.html is missing", McServeCommand.MissingWebUiReason(dir), StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, "wwwroot", "index.html"), "<!doctype html>");
            Assert.Null(McServeCommand.MissingWebUiReason(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

#endif
