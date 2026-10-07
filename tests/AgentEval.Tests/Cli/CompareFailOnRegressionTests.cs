// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Tests.Output;
using Xunit;
using static AgentEval.Tests.Cli.CompareCommandTests;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>agenteval compare --fail-on-regression</c>, over runs the real store wrote. Without the flag a comparable
/// result exits 0 whatever it shows, so a CI step running <c>compare</c> never failed on a regression.
/// </summary>
[Collection("ConsoleTests")]
public class CompareFailOnRegressionTests
{
    private static async Task<(string Baseline, string Candidate)> RunsAsync(
        TempWorkspace temp, bool baselinePassed, bool candidatePassed)
    {
        string a = await WriteRunAsync(temp, "A",
        [
            Scenario("steady", 0.9, true, "sha256:aaa", Facts()),
            Scenario("flips", 0.8, baselinePassed, "sha256:bbb", Facts()),
        ]);
        string b = await WriteRunAsync(temp, "B",
        [
            Scenario("steady", 0.9, true, "sha256:aaa", Facts()),
            Scenario("flips", 0.2, candidatePassed, "sha256:bbb", Facts()),
        ]);
        return (a, b);
    }

    private static (int Exit, string StdOut) Capture(Func<int> run)
    {
        var original = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            return (run(), sw.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task ARegression_ExitsOne_WithTheFlag_AndZeroWithout()
    {
        using var temp = TempWorkspace.Create("CompareRegressed");
        var (a, b) = await RunsAsync(temp, baselinePassed: true, candidatePassed: false);

        var (without, _) = Capture(() => CompareCommand.Run(a, b));
        var (with, report) = Capture(() => CompareCommand.Run(a, b, failOnRegression: true));
        var (withJson, _) = Capture(() => CompareCommand.Run(a, b, asJson: true, failOnRegression: true));

        Assert.Equal(0, without);
        Assert.Equal(ExitCodes.TestFailure, with);
        Assert.Equal(ExitCodes.TestFailure, withJson);
        Assert.Contains("REGRESSED", report, StringComparison.Ordinal);
        Assert.Contains("• flips", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecovery_IsNotARegression()
    {
        using var temp = TempWorkspace.Create("CompareRecovered");
        var (a, b) = await RunsAsync(temp, baselinePassed: false, candidatePassed: true);

        Assert.Equal(0, Capture(() => CompareCommand.Run(a, b, failOnRegression: true)).Exit);
    }

    [Fact]
    public async Task IncomparableRuns_StillExitThirteen_WithTheFlag()
    {
        using var temp = TempWorkspace.Create("CompareRefusedRegressed");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.9, true, "sha256:aaa", Facts())]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.1, false, "sha256:zzz", Facts())]);   // other stimulus

        Assert.Equal(ExitCodes.Incomparable, Capture(() => CompareCommand.Run(a, b, failOnRegression: true)).Exit);
    }

    [Fact]
    public async Task Json_NamesTheRegressedScenarios()
    {
        using var temp = TempWorkspace.Create("CompareRegressedJson");
        var (a, b) = await RunsAsync(temp, baselinePassed: true, candidatePassed: false);

        var (_, json) = Capture(() => CompareCommand.Run(a, b, asJson: true));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("regressed").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("recovered").GetInt32());
        Assert.Equal(["flips"], doc.RootElement.GetProperty("regressedScenarios").EnumerateArray().Select(e => e.GetString()));
    }
}
