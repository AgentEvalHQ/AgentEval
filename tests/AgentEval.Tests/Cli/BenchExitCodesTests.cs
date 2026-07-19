// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/Cli/BenchExitCodesTests.cs
//
// L23: the bench owasp|mitre|nist commands share one composite-label → CI exit-code mapping.
using AgentEval.Cli.Commands;

namespace AgentEval.Tests.Cli;

public class BenchExitCodesTests
{
    [Theory]
    [InlineData("pass", 0)]
    [InlineData("PASS", 0)]
    [InlineData("fail", 9)]
    [InlineData("warn", 10)]
    [InlineData("skipped", 11)]
    public void FromLabel_MapsToCiExitCode(string label, int expected)
        => Assert.Equal(expected, BenchExitCodes.FromLabel(label));
}
