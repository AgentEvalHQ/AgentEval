// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.Cli.Aef;

// Assertions for the actual memory producers' legacy folders, not fabricated generic DTO fixtures.
internal static class LegacyMemoryReportAssertions
{
    internal static JsonObject Import(string workspace, string source, string harness, bool reportOnly)
    {
        var manifest = Read(source, "manifest.json");
        Assert.Equal(harness, (string?)manifest["run"]!["harness"]);
        var bytes = File.ReadAllBytes(Path.Combine(source, "report-native.json"));
        var summary = Read(source, "summary.json");
        var scenarioDirectory = Path.Combine(source, "scenarios");
        var scenarios = (Directory.Exists(scenarioDirectory) ? Directory.GetFiles(scenarioDirectory, "*.json") : [])
            .Select(p => JsonNode.Parse(File.ReadAllBytes(p))!).OrderBy(s => (string)s["id"]!, StringComparer.Ordinal).ToArray();
        if (reportOnly) Assert.Empty(scenarios);
        else Assert.NotEmpty(scenarios);

        foreach (var capture in new[] { AefContentCapture.On, AefContentCapture.Off })
        {
            var destination = Path.Combine(Path.GetDirectoryName(workspace)!, "memory-import-proof", Path.GetFileName(source), capture.ToString());
            StoreV1Exporter.ExportWorkspaceRun(workspace, source, destination, new() { ContentCapture = capture });
            var evidence = new AefRunEvidenceReader().Read(destination);
            Assert.True(evidence.Info.Imported);
            Assert.Equal((string?)manifest["run"]!["runId"], evidence.Info.RunId);
            Assert.Equal(scenarios.Length, evidence.Results.Count);
            if (capture == AefContentCapture.On)
                Assert.Equal(bytes, evidence.CapturedContent("E-report-native-json")!.Value.ToArray());
            else Assert.Null(evidence.CapturedContent("E-report-native-json"));
#if NET10_0_OR_GREATER
            var artifacts = AgentEval.MissionControl.Services.WorkspaceArtifactProjector.Artifacts(
                new("memory-import-proof", evidence, []));
            if (capture == AefContentCapture.On)
            {
                var artifact = Assert.Single(artifacts, a => a.EvidenceId == "E-report-native-json");
                Assert.Equal("document", artifact.Kind);
                Assert.True(artifact.Captured);
            }
            else Assert.DoesNotContain(artifacts, a => a.EvidenceId == "E-report-native-json");
#endif
            var preserved = JsonNode.Parse(evidence.Summary!.Value.GetRawText())!["ext"]!["agenteval.store-v1"]!;
            Assert.Equal((string?)summary["verdict"], (string?)preserved["verdict"]);
            foreach (var key in new[] { "total", "passed", "failed", "warnings", "skipped" })
                Assert.Equal((int?)summary["stats"]![key] ?? 0, (int)preserved["stats"]![key]!);
            Assert.Equal(summary["metrics"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, (double)p.Value!)),
                preserved["metrics"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, (double)p.Value!)));

            var reader = new AefComparisonReader();
            if (reportOnly)
            {
                var error = Assert.Throws<InvalidDataException>(() => reader.Read(destination));
                Assert.Contains("no final comparison results", error.Message);
            }
            else
            {
                var comparison = reader.Read(destination);
                Assert.Equal(scenarios.Select(s => ((string)s["id"]!, (string)s["name"]!, (double?)s["score"], (bool)s["passed"]!)),
                    comparison.Scenarios.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.Id, s.Name, s.Score, s.Passed)));
                Assert.All(scenarios, s => Assert.Null(s["comparability"]));
                Assert.All(comparison.Scenarios, s => Assert.Null(s.Comparability));
                Assert.Equal(ComparisonVerdict.Incomparable, comparison.CompareTo(comparison).Verdict);
                foreach (var scenario in scenarios)
                {
                    var projected = Assert.Single(evidence.Results, r => r.GetProperty("caseId").GetString() == (string?)scenario["id"]);
                    var duration = TimeSpan.Parse((string)scenario["duration"]!, CultureInfo.InvariantCulture).TotalMilliseconds;
                    Assert.Equal(duration, projected.GetProperty("durationMs").GetDouble(), precision: 6);
                }
            }
        }
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(source, "report-native.json")));
        return JsonNode.Parse(bytes)!.AsObject();
    }

    internal static void NativeQuestions(string directory, JsonObject report)
    {
        var native = new AefRunEvidenceReader().Read(directory);
        Assert.Equal(report["QuestionResults"]!.AsArray().Select(q => (string)q!["QuestionId"]!).Order(StringComparer.Ordinal),
            native.Results.Select(r => r.GetProperty("caseId").GetString()!).Order(StringComparer.Ordinal));
    }

    private static JsonObject Read(string source, string name) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(source, name)))!.AsObject();
}
