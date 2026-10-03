// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.Models;
using Xunit;

namespace AgentEval.Memory.Tests.Reporting;

public class EmbeddedResourceTests
{
    [Fact]
    public void ReportHtml_IsAccessibleAsEmbeddedResource()
    {
        var assembly = typeof(MemoryBaseline).Assembly;
        var names = assembly.GetManifestResourceNames();
        var reportName = names.FirstOrDefault(n => n.EndsWith("report.html"));

        Assert.NotNull(reportName);
        using var stream = assembly.GetManifestResourceStream(reportName)!;
        Assert.True(stream.Length > 0);

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Assert.Contains("AgentEval", content);
        Assert.Contains("Memory Benchmark", content);
    }

    [Fact]
    public void ReportHtml_DoesNotReadArchetypesOrStochasticData()
    {
        // The docs, MemoryReportingOptions.IncludeArchetypes (default false) and
        // CategoryScoreEntry.Stochastic all state that the shipped report reads neither. If the
        // report starts using them, this fails: update those statements and reconsider the default.
        var assembly = typeof(MemoryBaseline).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("report.html"));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        Assert.DoesNotContain("archetype", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stochastic", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchetypesJson_IsAccessibleAsEmbeddedResource()
    {
        var assembly = typeof(MemoryBaseline).Assembly;
        var names = assembly.GetManifestResourceNames();
        var archetypeName = names.FirstOrDefault(n => n.EndsWith("archetypes.json"));

        Assert.NotNull(archetypeName);
        using var stream = assembly.GetManifestResourceStream(archetypeName)!;
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void ArchetypesJson_ContainsValidJson_With6Archetypes()
    {
        var assembly = typeof(MemoryBaseline).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("archetypes.json"));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var doc = JsonDocument.Parse(json);
        var archetypes = doc.RootElement.GetProperty("archetypes");
        Assert.Equal(6, archetypes.GetArrayLength());

        // Verify each archetype has expected_scores with 5 pentagon axes
        foreach (var arch in archetypes.EnumerateArray())
        {
            var scores = arch.GetProperty("expected_scores");
            Assert.True(scores.TryGetProperty("Recall", out _));
            Assert.True(scores.TryGetProperty("Resilience", out _));
            Assert.True(scores.TryGetProperty("Temporal", out _));
            Assert.True(scores.TryGetProperty("Persistence", out _));
            Assert.True(scores.TryGetProperty("Organization", out _));
        }
    }
}
