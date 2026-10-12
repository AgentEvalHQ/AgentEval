// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefNativeFinalizationTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("native-finalization");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_out);

    [Theory]
    [InlineData("eval", false)]
    [InlineData("eval", true)]
    [InlineData("probe", false)]
    [InlineData("probe", true)]
    [InlineData("composite", false)]
    [InlineData("composite", true)]
    public void Write_Unsealed_ClosesVerifiableEvidenceThatCanBeSignedLater(string family, bool aborted)
    {
        var (directory, hash) = Write(family, aborted, null);
        Assert.Null(hash);
        var verification = AefRunVerifier.Verify(directory);
        Assert.Equal(AefOutcome.Unsealed, verification.Outcome);
        Assert.Empty(verification.Problems);
        Assert.Equal(aborted ? "aborted" : "completed", (string?)AefTestRuns.Document(directory, "run.json")["status"]);
        if (AefTestRuns.ReferenceVerify(directory) is { } reference)
            Assert.Equal("unsealed", (string?)reference["outcome"]);

        using var signer = EcdsaP256Signer.Generate();
        var seal = AefSealer.Seal(directory, new AefSealOptions { SealedBy = AefSealedBy.Producer, Signer = signer });
        Assert.NotEmpty(seal.RunHash);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        if (AefTestRuns.ReferenceVerify(directory) is { } signed)
            Assert.Equal("intact", (string?)signed["outcome"]);
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("probe")]
    [InlineData("composite")]
    public void Write_UnsealedWithSigner_RefusesBeforeCreatingEvidence(string family)
    {
        using var signer = EcdsaP256Signer.Generate();
        Assert.Throws<AefInputException>(() => Write(family, false, signer));
        Assert.False(Directory.Exists(_out));
    }

    private (string Directory, string? RunHash) Write(string family, bool aborted, IAefSigner? signer)
    {
        var reason = aborted ? "cancelled" : null;
        if (family == "eval")
        {
            using var session = AefEvalWriter.Start(_out, new AefEvalRunOptions
            {
                SubjectRef = "model:test", Seal = false, Signer = signer, AbortReason = reason,
                DatasetName = "test.json", DatasetBytes = "[]"u8.ToArray(),
                TargetMode = AefTargetMode.Mocked,
            }, Start);
            session.Add(new TestResult { TestName = "case", Passed = true, Score = 100 }, "case", 70);
            return session.Complete(Start.AddSeconds(1));
        }
        if (family == "probe")
            return AefProbeRunWriter.Write(_out,
                [new AefProbeLine { CaseId = "case", State = AefState.Passed, Annotator = AefAnnotatorKind.Code }],
                new AefProbeRunOptions
                {
                    SubjectRef = "model:test", TargetMode = AefTargetMode.Mocked, SuiteRef = "redteam:test",
                    Seal = false, Signer = signer, AbortReason = reason,
                }, Start, Start.AddSeconds(1));

        var result = new EvalResult(new("case", "case", "test", "1.0.0"),
            new EvalScore(1, null, "pass", true, null, "none", null),
            new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false), Start);
        return AefCompositeRunWriter.Write(_out, result, new AefComponentShape(1, true, "Averaged", []),
            new AefCompositeRunOptions
            {
                SubjectRef = "model:test", TargetMode = AefTargetMode.Mocked, Lane = "gdpr", SuiteRef = "bench:test",
                Seal = false, Signer = signer, AbortReason = reason,
            }, Start, Start.AddSeconds(1));
    }
}
