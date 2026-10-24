// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Adapters.Tests.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Reading;

public sealed class AefComplianceSnapshotTests : IDisposable
{
    private readonly string _root = AefTestRuns.TempPath("compliance-snapshot");
    public void Dispose() => AefTestRuns.Delete(_root);

    private async Task<string> Fixture()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        return StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _root).Directory;
    }

    [Fact]
    public async Task Read_DetachedSnapshotAfterSourceDeletion_PreservesVerifiedReport()
    {
        var directory = await Fixture();
        var reader = new AefComplianceArtifactReader();
        var original = reader.Read(directory);
        var snapshot = new AefRunEvidenceReader().Read(directory);
        Directory.Delete(directory, recursive: true);
        var detached = reader.Read(snapshot);
        Assert.Equal(original.Run.Asserted, detached.Run.Asserted);
        Assert.Equal(original.Run with { Asserted = detached.Run.Asserted }, detached.Run);
        var expected = Assert.Single(original.Documents);
        var actual = Assert.Single(detached.Documents);
        Assert.Equal(expected.EvidenceId, actual.EvidenceId);
        Assert.Equal(expected.Document.GetRawText(), actual.Document.GetRawText());
        Assert.Equal(0, Assert.Single(actual.Evidence.Controls).PassRate);
        Assert.True(detached.Run.Imported);
    }

    [Fact]
    public async Task Read_ArtifactExceedsReaderBudget_RefusesBeforeParsing()
    {
        var snapshot = new AefRunEvidenceReader().Read(await Fixture());
        var error = Assert.Throws<InvalidDataException>(() => new AefComplianceArtifactReader(1).Read(snapshot));
        Assert.Contains("artifact byte limit", error.Message);
    }

    [Fact]
    public async Task Read_UncapturedComplianceLink_RefusesWithoutFetching()
    {
        var directory = await Fixture();
        var path = Path.Combine(directory, "evidence.ndjson");
        var lines = File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!).ToArray();
        var artifact = Assert.Single(lines, e => (string?)e["kind"] == "compliance_artifact");
        artifact["link"] = new JsonObject { ["uri"] = "https://example.invalid/report.json" };
        artifact.AsObject().Remove("digest");
        File.WriteAllText(path, string.Join("\n", lines.Select(n => n.ToJsonString())) + "\n");
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory, new() { SealedBy = AefSealedBy.Ingest });
        var snapshot = new AefRunEvidenceReader().Read(directory);
        var error = Assert.Throws<InvalidDataException>(() => new AefComplianceArtifactReader().Read(snapshot));
        Assert.Contains("not a captured local blob", error.Message);
    }

    [Fact]
    public async Task Read_CancelledSnapshotRead_HonorsCancellation()
    {
        var snapshot = new AefRunEvidenceReader().Read(await Fixture());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new AefComplianceArtifactReader().Read(snapshot, cancellation.Token));
    }
    [Theory]
    [InlineData("subject.version")]
    [InlineData("subject.framework")]
    [InlineData("subject.modelId")]
    [InlineData("subject.sourceProject")]
    [InlineData("subject.sourcePath")]
    [InlineData("subject.tags")]
    [InlineData("configurationId")]
    [InlineData("notes")]
    [InlineData("regressedFromBaseline")]
    public async Task AddDocument_OptionalNullAndOmission_AreEquivalent(string field)
    {
        var reader = new AefComplianceArtifactReader();
        var captured = Assert.Single(reader.Read(await Fixture()).Documents);
        var json = JsonNode.Parse(captured.Document.GetRawText())!;
        var target = field.StartsWith("subject.", StringComparison.Ordinal) ? json["subject"]!.AsObject()
            : field == "configurationId" ? json["attestation"]!.AsObject() : json["controls"]![0]!.AsObject();
        var member = field.StartsWith("subject.", StringComparison.Ordinal) ? field[8..] : field;
        target.Remove(member);
        var original = reader.ReadDocument(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "original");
        target[member] = null;
        var candidate = reader.ReadDocument(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "other");
        var documents = new List<AefComplianceDocument> { original };
        AefComplianceArtifactReader.AddDocument(documents, candidate);
        Assert.Single(documents);
    }

    [Fact]
    public async Task AddDocument_UnknownNullMember_StillRefusesConflict()
    {
        var reader = new AefComplianceArtifactReader();
        var original = Assert.Single(reader.Read(await Fixture()).Documents);
        var json = JsonNode.Parse(original.Document.GetRawText())!;
        json["unfamiliarEvidence"] = null;
        var candidate = reader.ReadDocument(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "other");
        Assert.Throws<InvalidDataException>(() => AefComplianceArtifactReader.AddDocument([original], candidate));
    }

}
