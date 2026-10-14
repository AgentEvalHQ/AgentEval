using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Indexing.Tests;

public sealed class BoundedCheckpointReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-checkpoint-query-" + Guid.NewGuid().ToString("N"));
    private static readonly AefTime At = AefTime.Parse("2026-10-14T00:00:00.123456789Z");
    private readonly BoundedCheckpointReader _reader = new();

    private CheckpointRunSelection Run(string name)
    {
        var directory = Path.Combine(_root, name);
        var started = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var writer = AefRunWriter.Create(directory, new AefRunHeader
        {
            RunId = name, Producer = new() { Name = "checkpoint-test", Version = "1" },
            Subject = new() { Ref = "agent:test", Kind = AefSubjectKind.Agent, Version = "1" },
            StartedAt = started, ContentCapture = AefContentCapture.Off,
            Execution = new() { TargetMode = AefTargetMode.Live }
        });
        writer.AddResult(new() { CaseId = "case-1", Path = "quality", Evaluator = new("code:quality"), State = AefState.Passed });
        writer.Close(AefRunStatus.Completed, started.AddSeconds(1));
        AefSealer.Seal(directory);
        return new(directory, AefRunVerifier.Verify(directory).RunHash!.Value.Value);
    }

    private static byte[] Manifest(params (string Id, string Hash)[] runs) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["schemaVersion"] = "1.0", ["checkpointId"] = "checkpoint-test", ["state"] = "evidence_complete", ["outcome"] = null,
        ["subject"] = new JsonObject { ["ref"] = "agent:test", ["version"] = "1" },
        ["lanes"] = new JsonArray(new JsonObject
        {
            ["lane"] = "quality", ["blocking"] = true,
            ["rule"] = new JsonObject { ["kind"] = "evidence-present", ["runs"] = 1 },
            ["runs"] = new JsonArray(runs.Select(run => (JsonNode)new JsonObject
                { ["runId"] = run.Id, ["runHash"] = run.Hash, ["origin"] = "launched" }).ToArray())
        })
    }.ToJsonString());

    [Fact]
    public void Read_ExplicitSelection_RecomputesEvidenceWithoutInventingApprovalOrTrust()
    {
        var run = Run("one"); var bytes = Manifest(("one", run.RunHash));
        var report = _reader.Read(bytes, null, [run], At, null);
        Assert.Empty(report.ManifestProblems); Assert.Empty(report.Problems);
        Assert.Equal("passed", Assert.Single(report.Lanes).EvidenceStatus);
        Assert.Equal(At.ToString(), report.EvaluatedAt); Assert.Equal(At.ToString(), report.RequestedAt);
        Assert.Empty(report.Anchors); Assert.Empty(report.SignedBy); Assert.False(report.SignatureChecked);
        Assert.Equal("evidence_complete", JsonNode.Parse(report.ManifestJson)!["state"]!.GetValue<string>());
        Assert.Equal(64, report.ManifestDigest.Length);
        Assert.DoesNotContain(_root, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void Read_UnselectedSiblingAndDuplicateSelections_DoNotDiscoverOrInventRuns()
    {
        var first = Run("one"); var other = Run("two");
        var report = _reader.Read(Manifest(("one", first.RunHash), ("two", other.RunHash)), null, [first, first], At, null);
        Assert.Contains(report.Problems, problem => problem.Code == "run-missing");
        Assert.Equal("not_measured", Assert.Single(report.Lanes).EvidenceStatus);
        Assert.Empty(report.Anchors);
    }

    [Fact]
    public void Read_SignedManifest_AnchorsOnlyWhenEvidenceAndTrustVerify()
    {
        var run = Run("one"); var bytes = Manifest(("one", run.RunHash));
        using var signer = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy([new TrustedKey("reviewer:test", signer.PublicKey)]);
        var envelope = DsseEnvelope.Create(Dsse.CheckpointPayloadType, bytes, signer).ToJson();
        var signed = _reader.Read(bytes, envelope, [run], At, policy);
        Assert.Equal(["reviewer:test"], signed.SignedBy); Assert.Equal([run.RunHash], signed.Anchors);
        Assert.True(signed.SignatureChecked); Assert.Null(signed.EnvelopeResult);
        var absent = _reader.Read(bytes, envelope, [], At, policy);
        Assert.NotEmpty(absent.Problems); Assert.Empty(absent.Anchors);
        var untrusted = _reader.Read(bytes, envelope, [run], At, TrustPolicy.Empty);
        Assert.Empty(untrusted.SignedBy); Assert.Empty(untrusted.Anchors);
        Assert.Equal(DsseSignatureResult.UntrustedKey, Assert.Single(untrusted.Signatures).Result);
        var malformed = _reader.Read(bytes, "{}"u8.ToArray(), [run], At, policy);
        Assert.Equal("Malformed", malformed.EnvelopeResult); Assert.Empty(malformed.Anchors);
    }

    [Fact]
    public void Read_ChangedHashTamperingAndBudgets_RefuseWithoutPartialEvidence()
    {
        var run = Run("one"); var bytes = Manifest(("one", run.RunHash));
        Assert.Equal("invalid-checkpoint-evidence", Assert.Throws<EvidenceIndexException>(() =>
            _reader.Read(bytes, null, [run with { RunHash = new string('a', 64) }], At, null)).Code);
        Assert.Throws<EvidenceIndexException>(() => _reader.Read(bytes, null, [run], At, null, 1));
        Assert.Equal("checkpoint-limit", Assert.Throws<EvidenceIndexException>(() =>
            _reader.Read(new byte[(1 << 20) + 1], null, [], At, null)).Code);
        Assert.Equal("checkpoint-limit", Assert.Throws<EvidenceIndexException>(() =>
            _reader.Read(bytes, null, Enumerable.Repeat(run, 9).ToArray(), At, null)).Code);
        Assert.Throws<OperationCanceledException>(() => _reader.Read(bytes, null, [run], At, null, cancellationToken: new(true)));
        File.AppendAllText(Path.Combine(run.Directory, "results.ndjson"), " ");
        Assert.Equal("invalid-checkpoint-evidence", Assert.Throws<EvidenceIndexException>(() => _reader.Read(bytes, null, [run], At, null)).Code);
    }

    [Fact]
    public void Read_RecordedDecision_UsesRecordedEvaluationTimeAndReportsMissingEvidence()
    {
        string? fixture = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "contracts/aef/1/conformance/checkpoints/valid-decided/document.json");
            if (File.Exists(candidate)) { fixture = candidate; break; }
        }
        Assert.NotNull(fixture);
        var report = _reader.Read(File.ReadAllBytes(fixture), null, [], At, null);
        Assert.Equal("2026-10-02T14:30:00Z", report.EvaluatedAt);
        Assert.Equal(At.ToString(), report.RequestedAt);
        Assert.NotEmpty(report.Problems); Assert.Empty(report.Anchors);
        Assert.Equal("inconclusive", JsonNode.Parse(report.ManifestJson)!["outcome"]!.GetValue<string>());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
