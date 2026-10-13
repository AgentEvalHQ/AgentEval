// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;
using AgentEval.Results;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>Snapshots current-invocation reports and maps their controls to observed AEF roots.</summary>
internal static class ComplianceAef
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static IReadOnlyList<AefComplianceArtifact> Composite(string path, ComplianceEvidence evidence, EvalResult result, object report)
    {
        // Rich composite reports include judge text; never emit them under content-off.
        if (AefCliOptions.ContentCapture != AefContentCapture.On) return [];
        var controls = evidence.Controls.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> Keys(EvalResult node) => new[] { node.Metric.Key }
            .Concat((node.Details.SubResults ?? []).SelectMany(Keys));
        var roots = result.Details.SubResults is { Count: > 0 } children ? children : [result];
        var cases = roots.Where(r => Keys(r).Any(controls.Contains)).Select(r => r.Metric.Key).ToArray();
        return Snapshot(path, evidence, cases, containsContent: true, rich: true, report);
    }

    internal static IReadOnlyList<AefComplianceArtifact> Scan(string agentEvalDir, ComplianceEvidence evidence, RedTeamResult scan)
    {
        // These reporters' evidence.json contains taxonomy/control metadata, not the rich findings or raw scan.
        var controls = evidence.Controls.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attacks = scan.AttackResults.Where(a => evidence.Regulation switch
        {
            OWASPComplianceReporter.Regulation => a.OwaspId is { } id && controls.Contains(id),
            MITREATLASReporter.Regulation => a.MitreAtlasIds.Any(controls.Contains),
            _ => false,
        });
        var cases = attacks.SelectMany(a => a.ProbeResults.Select(p => $"{a.AttackName}/{p.ProbeId}")).ToArray();
        var path = new FileSystemLayout(agentEvalDir).ComplianceEvidenceFile(evidence.Regulation, evidence.Subject,
            evidence.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss"));
        return Snapshot(path, evidence, cases, containsContent: false, rich: false, evidence);
    }

    private static IReadOnlyList<AefComplianceArtifact> Snapshot(string path, ComplianceEvidence expected, string[] cases,
        bool containsContent, bool rich, object expectedDocument)
    {
        if (cases.Length == 0) return [];
        var bytes = File.ReadAllBytes(path);
        try
        {
            var json = JsonNode.Parse(bytes)!;
            if (!JsonNode.DeepEquals(json, JsonSerializer.SerializeToNode(expectedDocument, ReportJson)))
                throw new AefInputException("The saved compliance artifact differs from the report produced by this invocation.");
            var basis = rich ? json["base"] : json;
            if ((string?)basis?["sourceRun"]?["runId"] != expected.SourceRun.RunId ||
                (string?)basis?["sourceRun"]?["manifestHash"] != expected.SourceRun.ManifestHash ||
                (string?)basis?["regulation"] != expected.Regulation ||
                (string?)basis?["subject"]?["name"] != expected.Subject.Name)
                throw new AefInputException("The saved compliance artifact no longer belongs to this invocation.");
        }
        catch (JsonException) { throw new AefInputException("The saved compliance artifact is not valid JSON."); }
        return [new AefComplianceArtifact { Bytes = bytes, CaseIds = cases, ContainsContent = containsContent }];
    }
}
