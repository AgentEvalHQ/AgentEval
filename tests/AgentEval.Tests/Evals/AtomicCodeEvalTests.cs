// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public class AtomicCodeEvalTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>Always returns a fixed value of 1.0 using the Build() helper.</summary>
    private sealed class AlwaysPassEval : AtomicCodeEval
    {
        public AlwaysPassEval() : base("always-pass", "Always Pass", "test", "1.0.0") { }

        protected override EvalResult Evaluate(EvalInput input) =>
            Build(value: 1.0, passed: true, severity: "none");
    }

    /// <summary>Returns a configurable result using Build() with dimensions and evidence.</summary>
    private sealed class ConfigurableEval(double value, bool passed, string severity) : AtomicCodeEval("cfg", "Configurable", "test", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input) =>
            Build(
                value: value,
                passed: passed,
                severity: severity,
                dimensions: new Dictionary<string, double> { ["score"] = value },
                evidence: [new EvalEvidence("code", "cfg-ref", "Computed deterministically")]);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_AlwaysPass_ProvenanceIsAtomicCode_ValueIsOne()
    {
        var sut = new AlwaysPassEval();
        var input = new EvalInput(Query: "test");

        var result = await sut.EvaluateAsync(input);

        Assert.Equal("atomic-code", result.Provenance.Type);
        Assert.Equal(1.0, result.Score.Value, precision: 10);
        Assert.True(result.Score.Passed);
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal("none", result.Score.Severity);
    }

    [Fact]
    public async Task EvaluateAsync_BuildHelper_DimensionsAndEvidencePopulated()
    {
        var sut = new ConfigurableEval(value: 0.75, passed: true, severity: "none");
        var input = new EvalInput(Query: "test");

        var result = await sut.EvaluateAsync(input);

        Assert.Equal(0.75, result.Score.Value, precision: 10);
        Assert.Equal("atomic-code", result.Provenance.Type);

        Assert.NotNull(result.Details.Dimensions);
        Assert.Equal(0.75, result.Details.Dimensions!["score"], precision: 10);

        Assert.NotNull(result.Details.Evidence);
        var ev = Assert.Single(result.Details.Evidence!);
        Assert.Equal("code", ev.Source);
        Assert.Equal("cfg-ref", ev.Reference);
    }

    [Fact]
    public async Task EvaluateAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var sut = new AlwaysPassEval();
        var input = new EvalInput(Query: "test");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.EvaluateAsync(input, cts.Token));
    }
}
