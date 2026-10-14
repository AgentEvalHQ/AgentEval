using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Interop;
using AgentEval.Results.Json;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Integrity;

namespace AgentEval.Results.Tests.Interop;

public class ArtifactCompanionServiceTests
{
    private static readonly string Artifact = new('a', 64);
    private readonly ArtifactCompanionService _service = new();

    [Fact]
    public void Create_SealedFailingRun_BindsBytesWithoutChangingEvidenceOrClaimingPass()
    {
        using var run = new TestRun().Write().Seal();
        var paths = Directory.GetFiles(run.Dir, "*", SearchOption.AllDirectories);
        var before = paths.ToDictionary(p => p, File.ReadAllBytes);
        var companion = _service.Create(run.Dir, "agent.tar", Artifact);
        var result = _service.Verify(companion, Artifact, run.Dir);
        Assert.True(result.IntactBinding);
        Assert.Equal(AefOutcome.Intact, result.Evidence.Outcome);
        Assert.Null(result.Signature);
        Assert.False(result.PolicySupplied);
        Assert.Equal(paths.Order(), Directory.GetFiles(run.Dir, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("seal")]
    [InlineData("evidence")]
    [InlineData("run-hash")]
    public void Verify_ChangedBinding_ReportsIndependentFailures(string change)
    {
        using var run = new TestRun().Write().Seal();
        var companion = _service.Create(run.Dir, "agent", Artifact);
        var selected = Artifact;
        if (change == "artifact") selected = new string('d', 64);
        if (change == "seal") File.AppendAllText(Path.Combine(run.Dir, "seal.json"), "\n");
        if (change == "evidence") File.AppendAllText(Path.Combine(run.Dir, "results.ndjson"), "\n");
        if (change == "run-hash") companion = ArtifactCompanion.Create(companion.Name, Artifact, companion.SealSha256, new string('d', 64));
        var result = _service.Verify(companion, selected, run.Dir);
        Assert.False(result.IntactBinding);
        Assert.Equal(change != "artifact", result.ArtifactMatches);
        Assert.Equal(change != "seal", result.SealMatches);
        if (change == "seal") Assert.Equal(AefOutcome.Intact, result.Evidence.Outcome);
        if (change == "evidence") Assert.Equal(AefOutcome.Invalid, result.Evidence.Outcome);
        if (change == "run-hash") Assert.False(result.RunHashMatches);
    }

    [Fact]
    public void Verify_SignedCompanion_SeparatesTrustedUntrustedAndWrongPayload()
    {
        using var run = new TestRun().Write().Seal();
        using var signer = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy([new TrustedKey("producer", signer.PublicKey)]);
        var companion = _service.Create(run.Dir, "agent", Artifact);
        var envelope = _service.Sign(companion, signer);
        var trusted = _service.Verify(companion, Artifact, run.Dir, envelope, policy);
        Assert.True(trusted.IntactBinding);
        Assert.True(trusted.PolicySupplied);
        Assert.Equal(new[] { "producer" }, trusted.Signature!.VerifiesFor);
        Assert.Empty(trusted.Evidence.SignedBy!); // The companion signature does not sign the run seal.
        var untrusted = _service.Verify(companion, Artifact, run.Dir, envelope);
        Assert.True(untrusted.IntactBinding);
        Assert.False(untrusted.PolicySupplied);
        Assert.Empty(untrusted.Signature!.VerifiesFor);
        Assert.Equal(DsseSignatureResult.UntrustedKey, Assert.Single(untrusted.Signature.Signatures).Result);
        var whitespace = ArtifactCompanion.Parse(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(companion.ToBytes())));
        var mismatch = _service.Verify(whitespace, Artifact, run.Dir, envelope, policy);
        Assert.True(mismatch.IntactBinding);
        Assert.Equal(DsseEnvelopeResult.PayloadMismatch, mismatch.Signature!.EnvelopeResult);
        Assert.Empty(mismatch.Signature.VerifiesFor);
        var malformed = _service.Verify(companion, Artifact, run.Dir, "{}"u8.ToArray(), policy);
        Assert.Equal(DsseEnvelopeResult.Malformed, malformed.Signature!.EnvelopeResult);
    }

    [Fact]
    public void Create_UnsealedOrTamperedRun_Refuses()
    {
        using var run = new TestRun().Write();
        Assert.Throws<AefInputException>(() => _service.Create(run.Dir, "agent", Artifact));
        run.Seal();
        File.AppendAllText(Path.Combine(run.Dir, "run.json"), " ");
        Assert.Throws<AefInputException>(() => _service.Create(run.Dir, "agent", Artifact));
    }

    [Fact]
    public void Verify_MissingSeal_ReturnsUnsealedWithoutBinding()
    {
        using var run = new TestRun().Write().Seal();
        var companion = _service.Create(run.Dir, "agent", Artifact);
        File.Delete(Path.Combine(run.Dir, "seal.json"));
        var result = _service.Verify(companion, Artifact, run.Dir);
        Assert.Equal(AefOutcome.Unsealed, result.Evidence.Outcome);
        Assert.False(result.IntactBinding);
        Assert.False(result.SealMatches);
        Assert.False(result.RunHashMatches);
    }

    [Fact]
    public void Verify_AuthorizedWithholding_PreservesUnavailableCountAndRequiresAuthority()
    {
        using var run = new TestRun().Write().Seal();
        using var signer = EcdsaP256Signer.Generate();
        var companion = _service.Create(run.Dir, "agent", Artifact);
        run.AppendBatch([TestRun.Event("ov_1", "redact", new JsonObject { ["blob"] = TestRun.ReasoningSha },
            edit: e => e["reason"] = "personal data")], signer);
        File.Delete(Path.Combine(run.Dir, TestRun.ReasoningPath));
        var policy = TestRun.Policy(signer, redact: true);
        var result = _service.Verify(companion, Artifact, run.Dir, policy: policy);
        Assert.True(result.IntactBinding);
        Assert.Equal(1, result.Evidence.Withheld);
        Assert.Equal(companion.ToBytes(), _service.Create(run.Dir, "agent", Artifact, policy).ToBytes());
        Assert.False(_service.Verify(companion, Artifact, run.Dir).IntactBinding);
        Assert.Throws<AefInputException>(() => _service.Create(run.Dir, "agent", Artifact));
    }

    [Fact]
    public void Verify_EnvelopeSizeBoundary_ParsesAtLimitAndRefusesNextByte()
    {
        using var run = new TestRun().Write().Seal();
        var companion = _service.Create(run.Dir, "agent", Artifact);
        var bytes = Enumerable.Repeat((byte)' ', ArtifactCompanion.MaxEnvelopeBytes).ToArray();
        bytes[0] = (byte)'{'; bytes[1] = (byte)'}';
        Assert.Equal(DsseEnvelopeResult.Malformed, _service.Verify(companion, Artifact, run.Dir, bytes).Signature!.EnvelopeResult);
        Assert.Throws<AefLimitException>(() => _service.Verify(companion, Artifact, run.Dir, new byte[bytes.Length + 1]));
    }

    [Fact]
    public void Create_CaptureBudgetOrCancellation_RefusesBeforeReturningBinding()
    {
        using var run = new TestRun().Write().Seal();
        var bounded = new ArtifactCompanionService(new() { MaxBytes = 1, MaxFileBytes = 1 });
        Assert.Throws<AefStagingException>(() => bounded.Create(run.Dir, "agent", Artifact));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => _service.Create("missing", "agent", Artifact, cancellationToken: cancellation.Token));
        var companion = _service.Create(run.Dir, "agent", Artifact);
        Assert.Throws<OperationCanceledException>(() => _service.Verify(companion, Artifact, "missing", cancellationToken: cancellation.Token));
    }
}
