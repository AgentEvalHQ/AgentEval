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

    // D-17: a gate that only withholds its pass because a required check (a security check with no tool calls recorded)
    // was not measured has no conclusive verdict: 11, never the warning code 10 that CI may treat as non-blocking.
    [Theory]
    [InlineData("warn", AgentEval.Evals.Meta.MeasurementState.NotMeasured, 11)]
    [InlineData("warn", AgentEval.Evals.Meta.MeasurementState.Measured, 10)]
    [InlineData("pass", AgentEval.Evals.Meta.MeasurementState.Measured, 0)]
    [InlineData("fail", AgentEval.Evals.Meta.MeasurementState.NotMeasured, 9)]
    [InlineData("skipped", AgentEval.Evals.Meta.MeasurementState.NotApplicable, 11)]
    public void FromVerdict_AWithheldPassIsNoConclusiveVerdict(string label, AgentEval.Evals.Meta.MeasurementState measurement, int expected)
        => Assert.Equal(expected, BenchExitCodes.FromVerdict(label, measurement));
}
