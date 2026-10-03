// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Xunit;
using AgenticLoader = AgentEval.Evals.Agentic.Calibration.CalibrationDatasetLoader;
using EuAiActLoader = AgentEval.Compliance.EuAiAct.Calibration.CalibrationDatasetLoader;
using GdprLoader = AgentEval.Compliance.Gdpr.Calibration.CalibrationDatasetLoader;

namespace AgentEval.Tests.Cli;

/// <summary>
/// The calibration golden sets ship inside the CLI assembly, so <c>bench … calibrate</c> works from the installed
/// tool. Before, they lived only in <c>AgentEval.Tests.dll</c>, and calibrate failed outside a source checkout. The
/// CLI embeds the same files the test project embeds; these tests prove every loader reads identical datasets from
/// both.
/// </summary>
public sealed class CalibrationGoldenAssemblyTests
{
    private static readonly System.Reflection.Assembly Cli = typeof(CalibrationGoldenAssembly).Assembly;
    private static readonly System.Reflection.Assembly Tests = typeof(CalibrationGoldenAssemblyTests).Assembly;

    [Fact]
    public void TheCliAssemblyItselfCarriesTheGoldens()
        => Assert.Same(Cli, CalibrationGoldenAssembly.TryLocate());

    [Fact]
    public async Task Gdpr_TheCliCarriesTheSameDatasetsAsTheTestProject()
    {
        var fromCli = await new GdprLoader().LoadAllFromAssemblyAsync(Cli);
        var fromTests = await new GdprLoader().LoadAllFromAssemblyAsync(Tests);
        Assert.NotEmpty(fromCli);
        Assert.Equal(Shape(fromTests.Select(d => (d.PillarKey, d.Entries.Count))), Shape(fromCli.Select(d => (d.PillarKey, d.Entries.Count))));
    }

    [Fact]
    public async Task EuAiAct_TheCliCarriesTheSameDatasetsAsTheTestProject()
    {
        var fromCli = await new EuAiActLoader().LoadAllFromAssemblyAsync(Cli);
        var fromTests = await new EuAiActLoader().LoadAllFromAssemblyAsync(Tests);
        Assert.NotEmpty(fromCli);
        Assert.Equal(Shape(fromTests.Select(d => (d.PillarKey, d.Entries.Count))), Shape(fromCli.Select(d => (d.PillarKey, d.Entries.Count))));
    }

    [Fact]
    public async Task Agentic_TheCliCarriesTheSameDatasetsAsTheTestProject()
    {
        var fromCli = await new AgenticLoader().LoadAllFromAssemblyAsync(Cli);
        var fromTests = await new AgenticLoader().LoadAllFromAssemblyAsync(Tests);
        Assert.NotEmpty(fromCli);
        Assert.Equal(Shape(fromTests.Select(d => (d.CategoryKey, d.Entries.Count))), Shape(fromCli.Select(d => (d.CategoryKey, d.Entries.Count))));
    }

    private static string[] Shape(IEnumerable<(string Key, int Count)> datasets) =>
        datasets.Select(d => $"{d.Key}:{d.Count}").OrderBy(s => s, StringComparer.Ordinal).ToArray();
}
