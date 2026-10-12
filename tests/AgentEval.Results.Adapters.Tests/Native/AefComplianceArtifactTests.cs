// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefComplianceArtifactTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("compliance-artifact");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("{ \"report\": \"original bytes\" }\r\n");
    public void Dispose() => AefTestRuns.Delete(_out);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Write_ArtifactCapture_PreservesExactBytesAndLinksOnlyMatchingRoot(bool composite, bool capture, bool containsContent)
    {
        var directory = Write(composite, capture, containsContent, "matched");
        var included = capture || !containsContent;
        var results = AefTestRuns.Results(directory);
        Assert.Equal(included, results.Single(r => (string?)r["caseId"] == "matched")["evidence"] is not null);
        Assert.Null(results.Single(r => (string?)r["caseId"] == "other")["evidence"]);
        var evidencePath = Path.Combine(directory, "evidence.ndjson");
        if (included)
        {
            var evidence = JsonNode.Parse(Assert.Single(File.ReadAllLines(evidencePath)))!;
            Assert.Equal("compliance_artifact", (string?)evidence["kind"]);
            var blobs = Directory.GetFiles(Path.Combine(directory, "blobs"), "*", SearchOption.AllDirectories);
            Assert.Equal(Bytes, File.ReadAllBytes(Assert.Single(blobs)));
        }
        else Assert.True(!File.Exists(evidencePath) || new FileInfo(evidencePath).Length == 0);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        if (AefTestRuns.ReferenceVerify(directory) is { } reference)
        {
            Assert.Equal("intact", (string?)reference["outcome"]);
            Assert.Empty(reference["problems"]!.AsArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_UnknownArtifactCase_RejectsOrphanEvidence(bool composite) =>
        Assert.Throws<AefInputException>(() => Write(composite, true, true, "missing"));

    private string Write(bool composite, bool capture, bool containsContent, string caseId)
    {
        AefComplianceArtifact[] artifacts = [new() { Bytes = Bytes, CaseIds = [caseId], ContainsContent = containsContent }];
        var mode = capture ? AefContentCapture.On : AefContentCapture.Off;
        if (!composite) return AefProbeRunWriter.Write(_out,
            [new() { CaseId = "matched", State = AefState.Passed }, new() { CaseId = "other", State = AefState.Passed }],
            new AefProbeRunOptions { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, SuiteRef = "redteam:test",
                ContentCapture = mode, ComplianceArtifacts = artifacts }, Start, Start.AddSeconds(1)).Directory;
        EvalResult Leaf(string key) => new(new(key, key, "test", "1.0.0"),
            new(1, null, "pass", true, null, "none", null), new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false), Start);
        var root = Leaf("root") with { Details = new(null, null, null, [Leaf("matched"), Leaf("other")], "Own") };
        return AefCompositeRunWriter.Write(_out, root, AefCompositeRunWriter.Undescribed,
            new AefCompositeRunOptions { SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:test",
                ContentCapture = mode, ComplianceArtifacts = artifacts }, Start, Start.AddSeconds(1)).Directory;
    }
}
