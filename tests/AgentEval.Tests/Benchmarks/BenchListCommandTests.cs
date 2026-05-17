// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Cli.Commands;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Tests for <see cref="BenchListCommand"/> (Phase 8 / v0.10.0-beta).
/// </summary>
/// <remarks>
/// The critical test is <see cref="OutputComesFromRegistry"/>: it registers a synthetic
/// extra family at runtime and asserts the family appears in <c>bench --list</c>'s
/// output. This proves the listing is NOT a hardcoded constant.
/// </remarks>
public class BenchListCommandTests
{
    [Fact]
    public void Run_PrintsAllEightDefaultFamilies()
    {
        // Anchor assemblies and capture output.
        BenchListCommand.AnchorAssemblies();
        var output = CaptureOutput(BenchListCommand.Run);

        // Each of the 8 default families should be present.
        Assert.Contains("agentic", output);
        Assert.Contains("gdpr", output);
        Assert.Contains("eu-ai-act", output);
        Assert.Contains("owasp", output);
        Assert.Contains("mitre", output);
        Assert.Contains("longmemeval", output);
        Assert.Contains("memory", output);
        Assert.Contains("perf", output);
    }

    [Fact]
    public void Run_IncludesPresetDescriptions()
    {
        BenchListCommand.AnchorAssemblies();
        var output = CaptureOutput(BenchListCommand.Run);

        // At least one preset per family should appear.
        Assert.Contains("agentic-execution", output);
        Assert.Contains("standard", output);
        Assert.Contains("top10", output);
        Assert.Contains("atlas-baseline", output);
        Assert.Contains("subset", output);
        Assert.Contains("quick", output);
        Assert.Contains("latency", output);
    }

    /// <summary>
    /// The critical test: a synthetic family registered at runtime appears in the
    /// <c>bench --list</c> output. This proves the listing is sourced from the
    /// registry, NOT a hardcoded constant.
    /// </summary>
    [Fact]
    public void OutputComesFromRegistry()
    {
        var syntheticName = $"test-synthetic-{Guid.NewGuid():N}";
        var synthetic = new BenchmarkFamily(
            name: syntheticName,
            description: "Synthetic family registered at runtime for the extensibility test",
            defaultCostTier: CostTier.Free,
            presets: [new BenchmarkPreset("default", "Default preset", null)],
            compositeFactory: (_, _) => throw new NotSupportedException("Test-only synthetic family."),
            owningAssemblyName: "AgentEval.Tests");

        BenchmarkFamilyRegistry.Register(synthetic);

        var output = CaptureOutput(BenchListCommand.Run);

        Assert.Contains(syntheticName, output);
        Assert.Contains("Synthetic family registered at runtime for the extensibility test", output);
    }

    [Fact]
    public void Run_ReturnsZero_WhenFamiliesPresent()
    {
        BenchListCommand.AnchorAssemblies();
        var writer = new StringWriter();
        var exitCode = BenchListCommand.Run(writer);
        Assert.Equal(0, exitCode);
    }

    private static string CaptureOutput(Func<TextWriter?, int> action)
    {
        var writer = new StringWriter();
        action(writer);
        return writer.ToString();
    }
}
