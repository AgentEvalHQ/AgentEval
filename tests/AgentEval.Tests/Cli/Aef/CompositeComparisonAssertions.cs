// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.Cli.Aef;

internal static class CompositeComparisonAssertions
{
    internal static async Task MatchesFinalAsync(string workspace, string nativeDirectory, bool capture, bool workspaceEnrichment = true)
    {
        var reader = new AefComparisonReader();
        var native = reader.Read(nativeDirectory);
        var header = JsonNode.Parse(File.ReadAllBytes(Path.Combine(nativeDirectory, "run.json")))!;
        var runId = (string)header["ext"]!["agenteval.store-v1"]!["runId"]!;
        var source = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(workspace, "manifest.json", SearchOption.AllDirectories),
            p => (string?)JsonNode.Parse(File.ReadAllBytes(p))!["run"]!["runId"] == runId))!;
        var original = new List<ScenarioResult>();
        await foreach (var scenario in new FileSystemOutputStore(workspace).GetScenarioResultsAsync(runId)) original.Add(scenario);
        Assert.NotEmpty(original);
        Assert.Equal(original.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.Id, s.Name, s.Score, s.Passed, s.Measurement)),
            native.Scenarios.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.Id, s.Name, s.Score, s.Passed, s.Measurement)));
        if (capture)
            Assert.Equal(original.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.StimulusHash, s.Comparability)),
                native.Scenarios.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.StimulusHash, s.Comparability)));
        else Assert.All(native.Scenarios, s => Assert.Null(s.StimulusHash));
        var destination = Path.Combine(Path.GetDirectoryName(workspace)!, "composite-import-proof", runId);
        var options = new StoreV1ExportOptions { ContentCapture = capture ? AefContentCapture.On : AefContentCapture.Off };
        if (workspaceEnrichment) StoreV1Exporter.ExportWorkspaceRun(workspace, source, destination, options);
        else
        {
            // The report-failure fixture deliberately blocks the workspace report directory with a file.
            // Workspace enrichment must refuse it; compare's exact-folder route still reads the completed run.
            Assert.Throws<IOException>(() => StoreV1Exporter.ExportWorkspaceRun(workspace, source, destination, options));
            StoreV1Exporter.ExportDirectory(source, destination, options);
        }
        var converted = reader.Read(destination);
        Assert.True(converted.Imported);
        // A later report failure can abort the command after the legacy evaluation completed.
        Assert.Equal("completed", converted.Status);
        Assert.Equal(native.Scenarios.OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => (s.Id, s.Name, s.Score, s.Passed, s.Measurement, s.StimulusHash, s.Comparability)),
            converted.Scenarios.OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => (s.Id, s.Name, s.Score, s.Passed, s.Measurement, s.StimulusHash, s.Comparability)));
        Assert.Equal(native.Metrics.OrderBy(p => p.Key, StringComparer.Ordinal), converted.Metrics.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(native.CompareTo(native).Verdict, native.CompareTo(converted).Verdict);
    }
}
