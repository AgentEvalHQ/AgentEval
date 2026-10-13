// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;
using AgentEval.Results;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

public sealed class ComplianceAefTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "compliance-aef-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Scan_SavedReport_RequiresCurrentSourceAndLinksOnlyNamedControls(int mutation)
    {
        var evidence = new ComplianceEvidence("1.0", OWASPComplianceReporter.Regulation,
            new SubjectIdentity(SubjectKind.Agent, "subject"), DateTimeOffset.UtcNow, new SourceRunRef("run-one", "sha256:123"),
            [new EvidenceControl("LLM01", "static title", "pass", 1, [], null)],
            new EvidenceSummary(1, 1, 0, 0, "PASS"), new Attestation("1", null, "test", "internal"));
        var file = new FileSystemLayout(_root).ComplianceEvidenceFile(evidence.Regulation, evidence.Subject,
            evidence.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.SerializeToNode(evidence, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        })!;
        if (mutation == 1) json["sourceRun"]!["runId"] = "other-run";
        if (mutation == 2) json["controls"]![0]!["notes"] = "injected private response";
        File.WriteAllText(file, json.ToJsonString() + "\r\n");
        ProbeResult Probe(string id) => new() { ProbeId = id, Prompt = "private prompt", Response = "private reply", Reason = "private reason", Outcome = EvaluationOutcome.Resisted };
        var scan = new RedTeamResult
        {
            AgentName = "subject", AttackResults =
            [new AttackResult { AttackName = "included", OwaspId = "LLM01", ProbeResults = [Probe("a")] },
             new AttackResult { AttackName = "excluded", OwaspId = "LLM02", ProbeResults = [Probe("b")] }],
        };
        if (mutation != 0) Assert.Throws<AefInputException>(() => ComplianceAef.Scan(_root, evidence, scan));
        else
        {
            var artifact = Assert.Single(ComplianceAef.Scan(_root, evidence, scan));
            Assert.Equal(File.ReadAllBytes(file), artifact.Bytes.ToArray());
            Assert.Equal(["included/a"], artifact.CaseIds);
            Assert.False(artifact.ContainsContent);
        }
    }
}
