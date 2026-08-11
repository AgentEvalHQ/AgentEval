// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam;
using Xunit;

namespace AgentEval.Tests.RedTeam.Reporting;

/// <summary>S3c — label provenance, so a reported rate can be read with the right confidence.</summary>
public sealed class ProbeLabelSourceTests
{
    private static AttackProbe Probe(IReadOnlyDictionary<string, object>? metadata) =>
        new() { Id = "PI-001", Prompt = "p", Difficulty = Difficulty.Easy, Metadata = metadata };

    [Fact]
    public void AbsentMetadata_ReportsUnspecified_RatherThanGuessing()
    {
        // An unknown provenance is a gap to fill, not a value to invent.
        Assert.Equal(ProbeLabelSource.Unspecified, ProbeLabelSource.Of(Probe(null)));
    }

    [Fact]
    public void RecordedSource_IsReadBack()
    {
        var probe = Probe(new Dictionary<string, object>
        {
            [ProbeLabelSource.MetadataKey] = ProbeLabelSource.Human,
        });

        Assert.Equal(ProbeLabelSource.Human, ProbeLabelSource.Of(probe));
    }

    [Fact]
    public void ImportedSources_KeepTheDatasetName_SoReportsCanBeFilteredByCorpus()
    {
        var source = ProbeLabelSource.Imported("HarmBench");

        Assert.Equal("imported:HarmBench", source);
        Assert.True(ProbeLabelSource.TryGetImportedDataset(source, out var dataset));
        Assert.Equal("HarmBench", dataset);
    }

    [Fact]
    public void NonImportedSources_AreNotMistakenForImports()
    {
        Assert.False(ProbeLabelSource.TryGetImportedDataset(ProbeLabelSource.SyntheticTemplate, out _));
        Assert.False(ProbeLabelSource.TryGetImportedDataset("imported:", out _));
    }

    [Fact]
    public void BlankDatasetName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ProbeLabelSource.Imported("  "));
    }
}
