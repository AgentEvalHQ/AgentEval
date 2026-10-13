// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Results.Writing;
using Xunit;

namespace AgentEval.Tests.Cli;

public sealed class BenchAefGateTests
{
    [Theory]
    [InlineData("pass", ExitCodes.Success, AefGateOutcome.Ship)]
    [InlineData("fail", ExitCodes.GateFailed, AefGateOutcome.NoShip)]
    [InlineData("warn", ExitCodes.GateWarning, AefGateOutcome.NoShip)]
    [InlineData("warn", ExitCodes.GateIndeterminate, AefGateOutcome.Inconclusive)]
    [InlineData("skipped", ExitCodes.GateIndeterminate, AefGateOutcome.Inconclusive)]
    public void Gate_CommandExit_RecordsTheActualDecision(string label, int exit, AefGateOutcome outcome)
    {
        var gate = BenchAef.Gate(label, exit);
        Assert.Equal(outcome, gate.Outcome);
        Assert.Equal(exit, gate.ExitCode);
        Assert.Equal(AefComparability.NotApplicable, gate.Comparability);
    }
}
