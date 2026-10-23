// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

// Paths refer only to the owned input snapshot, whose lifetime encloses Export.
internal sealed record StoreV1ReportArtifact(string Format, string SnapshotPath);

public static partial class StoreV1Exporter
{
    private static IReadOnlyList<StoreV1ReportArtifact> Reports(string snapshot, AefContentCapture capture) =>
        capture == AefContentCapture.Off ? [] : new[] { "markdown", "md", "html", "junit", "sarif", "xml" }
            .Select(format => new StoreV1ReportArtifact(format, Path.Combine(snapshot, "reports", "report." + format)))
            .Append(new StoreV1ReportArtifact("native-json", Path.Combine(snapshot, "report-native.json")))
            .Where(report => File.Exists(report.SnapshotPath)).ToArray();

    private static void AddReports(AefRunWriter writer, IReadOnlyList<StoreV1ReportArtifact> reports, AefContentCapture capture)
    {
        if (capture == AefContentCapture.Off) return;
        foreach (var report in reports)
        {
            using var input = File.OpenRead(report.SnapshotPath);
            writer.AddEvidence(new()
            {
                EvidenceId = "E-report-" + report.Format,
                Kind = AefEvidenceKind.Document,
                Link = AefEvidenceLink.ToBlob(writer.PutBlob(input)),
                Description = "Captured legacy report; ingest custody begins at conversion.",
                Ext = new JsonObject { [ExtName] = new JsonObject { ["reportFormat"] = report.Format } },
            });
        }
    }
}
