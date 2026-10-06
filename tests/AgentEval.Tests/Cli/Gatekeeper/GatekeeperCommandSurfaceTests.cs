// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.Cli.Gatekeeper;

/// <summary>
/// The CLI does not advertise a command that cannot work. `gatekeeper serve` was registered as a visible stub that
/// always exited RuntimeError, while the docs said it was not part of the CLI surface.
/// </summary>
public class GatekeeperCommandSurfaceTests
{
    [Fact]
    public void TheGatekeeperCommand_OffersOnlyCommandsThatWork()
    {
        var names = GatekeeperCommand.Create().Subcommands.Select(c => c.Name).ToList();

        Assert.Equal(["list-gates", "inspect", "calibrate"], names);
    }
}
