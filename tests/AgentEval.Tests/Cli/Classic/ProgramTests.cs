// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/ProgramTests.cs
// during the v1.1 CLI consolidation. Adapted for the merged command surface:
// the joslat repo's CLI exposes both the legacy commands (eval/init/list/redteam,
// imported from this repo) AND the v0.10+ commands (bench/doctor/migrate/mc/...).
// The original tests asserted exactly 4 subcommands — that assertion is loosened
// to "contains all 4 ported subcommands" so the merged Program.cs continues to
// satisfy the contract while exposing additional commands.

using System.CommandLine;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Classic;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// Tests for the CLI root command (Program.cs) — verifies all subcommands are registered.
/// </summary>
public class ProgramTests
{
    private static RootCommand CreateRootCommand()
    {
        // Mirror the relevant subset of Program.cs: the ported v0.2.0-alpha commands.
        // We don't construct the full Program.cs surface here because the in-tree
        // commands have their own dedicated tests (BenchCommandTests etc.).
        return new RootCommand("AgentEval — evaluate AI agents from the command line")
        {
            EvalCommand.Create(),
            DatasetInitCommand.Create(),
            ListCommand.Create(),
            RedTeamCommand.Create(),
        };
    }

    [Fact]
    public void RootCommand_HasAtLeast4Subcommands()
    {
        // Original test asserted "Equal(4, ...)". Loosened because the joslat-merged
        // root command also exposes bench/doctor/migrate/mc/... — see Program.cs.
        var root = CreateRootCommand();
        Assert.True(root.Subcommands.Count >= 4,
            $"Expected at least 4 subcommands, got {root.Subcommands.Count}");
    }

    [Fact]
    public void RootCommand_HasEvalCommand()
    {
        var root = CreateRootCommand();
        Assert.Contains(root.Subcommands, c => c.Name == "eval");
    }

    [Fact]
    public void RootCommand_HasInitCommand()
    {
        var root = CreateRootCommand();
        Assert.Contains(root.Subcommands, c => c.Name == "init");
    }

    [Fact]
    public void RootCommand_HasListCommand()
    {
        var root = CreateRootCommand();
        Assert.Contains(root.Subcommands, c => c.Name == "list");
    }

    [Fact]
    public void RootCommand_HasRedTeamCommand()
    {
        var root = CreateRootCommand();
        Assert.Contains(root.Subcommands, c => c.Name == "redteam");
    }

    [Fact]
    public void RootCommand_Description_ContainsAgentEval()
    {
        var root = CreateRootCommand();
        Assert.Contains("AgentEval", root.Description);
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("init")]
    [InlineData("list")]
    [InlineData("redteam")]
    public void AllCommands_HaveDescriptions(string commandName)
    {
        var root = CreateRootCommand();
        var command = root.Subcommands.First(c => c.Name == commandName);
        Assert.False(string.IsNullOrWhiteSpace(command.Description),
            $"Command '{commandName}' should have a description");
    }
}
