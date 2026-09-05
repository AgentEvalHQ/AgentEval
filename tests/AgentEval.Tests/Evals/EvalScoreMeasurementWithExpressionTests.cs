// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// ADR-030 Slice 1.1. The invariant is one sentence — <i>a score that is not a measurement can never
/// be <see cref="EvalScore.Passed"/></i> — and ADR-030 §4.2 records it being placed wrongly twice
/// before landing here.
/// <list type="number">
///   <item>A throwing property <i>initializer</i> on <c>Measurement</c>: does not compile (CS0236),
///         and could never have fired anyway, because a non-positional init-only property is set by
///         an object initializer or a <c>with</c>, both of which run AFTER field initialisers.</item>
///   <item>A guard on <c>EvalResult</c>'s primary constructor: bypassed by
///         <c>result with { Score = badScore }</c>, and on the wrong type — both operands live on
///         <see cref="EvalScore"/>, which all five aggregation strategies read directly.</item>
/// </list>
/// These tests pin attempt 3, the AE-01 / AE-08 pattern: a private backing field plus a validating
/// <c>init</c> accessor on <b>each</b> operand, so the bad state is unreachable from either side.
/// The constructor-path tests in <see cref="EvalScoreTests"/> and the NaN tests in
/// <see cref="EvalScoreWithExpressionTests"/> are untouched.
/// </summary>
public class EvalScoreMeasurementWithExpressionTests
{
    private static EvalScore MeasuredPass()
        => new(Value: 0.9, Ordinal: null, Label: "pass", Passed: true, Threshold: 0.8, Severity: "none", Confidence: null);

    // The real read path: EvalResultPersistence's options, camelCase properties and camelCase enums.
    private static readonly JsonSerializerOptions s_persistenceLike = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ── the acceptance criterion, both directions ──────────────────────────────────────────────

    [Fact]
    public void NotApplicableScore_CannotBePassed()
    {
        // The exact bypass that defeated the EvalResult-constructor guard.
        var na = EvalScore.NotApplicable();

        var ex = Assert.Throws<ArgumentException>(() => na with { Passed = true });

        Assert.Equal("Passed", ex.ParamName);
        Assert.False(na.Passed);
        Assert.Equal(MeasurementState.NotApplicable, na.Measurement);
    }

    [Fact]
    public void MeasuredPassingScore_CannotBecomeNotApplicable()
    {
        // The other side of the pair: arriving at the bad state by demoting the measurement rather
        // than by promoting the pass. A guard on one operand only would let this through.
        var pass = MeasuredPass();

        var ex = Assert.Throws<ArgumentException>(() => pass with { Measurement = MeasurementState.NotApplicable });

        Assert.Equal("Measurement", ex.ParamName);
        Assert.True(pass.Passed);
        Assert.Equal(MeasurementState.Measured, pass.Measurement);
    }

    [Fact]
    public void MeasuredPassingScore_CannotBecomeNotMeasured()
    {
        // NotMeasured is the operational sibling — a timeout, a budget filter. Same invariant.
        var ex = Assert.Throws<ArgumentException>(
            () => MeasuredPass() with { Measurement = MeasurementState.NotMeasured });

        Assert.Equal("Measurement", ex.ParamName);
    }

    [Fact]
    public void BothWithOrderings_AreCaught()
    {
        // The record's clone copies every field first, THEN runs the with-block's init accessors in
        // source order — so each accessor sees the other operand's copied value and neither ordering
        // can sneak the pair past the guard by setting the "safe" member first.
        var measuredFail = MeasuredPass() with { Passed = false, Label = "fail" };

        Assert.Throws<ArgumentException>(() =>
            measuredFail with { Passed = true, Measurement = MeasurementState.NotApplicable });

        Assert.Throws<ArgumentException>(() =>
            measuredFail with { Measurement = MeasurementState.NotApplicable, Passed = true });
    }

    [Fact]
    public void ObjectInitializer_IsCaught()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new EvalScore(1.0, null, "pass", true, null, "none", null)
            { Measurement = MeasurementState.NotApplicable });

        Assert.Equal("Measurement", ex.ParamName);
    }

    // ── the factory ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NotApplicableFactory_ProducesTheCanonicalShape()
    {
        var na = EvalScore.NotApplicable();

        Assert.Equal(MeasurementState.NotApplicable, na.Measurement);
        Assert.Equal("inapplicable", na.Label);
        Assert.False(na.Passed);          // always false, per ADR-030 §4.2's canonical mapping table
        Assert.Equal(0.0, na.Value);      // 0.0, and never read
        Assert.Equal("none", na.Severity);
        Assert.Null(na.Threshold);
        Assert.Null(na.Confidence);
        Assert.Equal("high", EvalScore.NotApplicable("high").Severity);
    }

    [Fact]
    public void NotApplicable_IsNotEqualTo_AFabricatedLookAlike()
    {
        // Measurement participates in record equality because it is a field. That is CORRECT — an
        // inapplicable score and a hand-built look-alike are different facts — and it is declared here
        // rather than discovered by whoever keys a cache on EvalScore.
        var real = EvalScore.NotApplicable();
        var lookAlike = new EvalScore(0.0, null, "inapplicable", false, null, "none", null);

        Assert.NotEqual(real, lookAlike);
        Assert.Equal(real, EvalScore.NotApplicable());
        Assert.Equal(real.GetHashCode(), EvalScore.NotApplicable().GetHashCode());
    }

    // ── the escape hatch, declared rather than discovered later ────────────────────────────────

    [Fact]
    public void ReDeclaringBothOperands_Succeeds_AndIsTheOnlyWayBack()
    {
        // ADR-030 §4.2 declares this deliberately: the author must assert BOTH "this is a real
        // measurement" AND "it passed" in one expression. There is no way to forbid it without making
        // Measurement immutable after construction, which would break the legal NotMeasured-on-a-timeout
        // path. It fails in the FLATTERING direction, which is why the architecture test in
        // MetaLaneArchitectureTests greps for `Measurement = MeasurementState.Measured` in with-blocks.
        var revived = EvalScore.NotApplicable() with { Measurement = MeasurementState.Measured, Passed = true };

        Assert.True(revived.Passed);
        Assert.Equal(MeasurementState.Measured, revived.Measurement);
    }

    // ── deserialisation fails closed ───────────────────────────────────────────────────────────

    [Fact]
    public void HostileArtifact_ClaimingAPassOnANonMeasurement_FailsClosed()
    {
        // ADR-025's direction: refuse the artifact rather than load a pass that was never earned.
        // System.Text.Json fills positional members through the constructor and init-only members
        // afterwards, so Measurement's accessor sees Passed == true and throws.
        const string hostile = """
            {"value":0.0,"label":"inapplicable","passed":true,"severity":"none","measurement":"notApplicable"}
            """;

        var ex = Assert.ThrowsAny<Exception>(
            () => JsonSerializer.Deserialize<EvalScore>(hostile, s_persistenceLike));

        Assert.True(
            ex is ArgumentException || ex.InnerException is ArgumentException,
            $"Expected the pair guard to refuse the document; got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void HostileArtifact_WithNumericEnum_AlsoFailsClosed()
    {
        // Default options (no string-enum converter) accept the numeric form. Same guard, same answer.
        const string hostile = """
            {"Value":0.0,"Label":"inapplicable","Passed":true,"Severity":"none","Measurement":1}
            """;

        var ex = Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<EvalScore>(hostile));

        Assert.True(
            ex is ArgumentException || ex.InnerException is ArgumentException,
            $"Expected the pair guard to refuse the document; got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void HonestInapplicableArtifact_RoundTrips()
    {
        const string honest = """
            {"value":0.0,"label":"inapplicable","passed":false,"severity":"none","measurement":"notApplicable"}
            """;

        var score = JsonSerializer.Deserialize<EvalScore>(honest, s_persistenceLike)!;

        Assert.Equal(MeasurementState.NotApplicable, score.Measurement);
        Assert.False(score.Passed);
        Assert.Equal(EvalScore.NotApplicable(), score);
    }

    // ── the write path is byte-identical while the state is Measured ───────────────────────────

    [Fact]
    public void MeasuredScore_DoesNotEmitAMeasurementField()
    {
        // Schema v1 declares `additionalProperties: false` on `score`. Emitting `measurement`
        // unconditionally would invalidate every document the library writes and change every
        // historical ScenarioResult content hash. Persisting it is Slice 1.4, which ADR-030 §9 Q4
        // still gates — so until then the field is written only when it is NOT the default, which
        // no shipped producer can currently make it.
        var persisted = JsonSerializer.Serialize(MeasuredPass(), s_persistenceLike);
        Assert.DoesNotContain("measurement", persisted, StringComparison.OrdinalIgnoreCase);

        // And with plain options, where nothing is dropped for being null: still exactly the seven
        // positional members schema v1 knows about, and no eighth.
        var plain = JsonSerializer.Serialize(MeasuredPass());
        using var doc = JsonDocument.Parse(plain);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(7, names.Count);
        Assert.DoesNotContain("Measurement", names);
        Assert.All(new[] { "Value", "Ordinal", "Label", "Passed", "Threshold", "Severity", "Confidence" },
            expected => Assert.Contains(expected, names));
    }

    [Fact]
    public void SkippedResult_StillWritesNoMeasurementField()
    {
        // The canonical mapping table says skipped/error SHOULD carry NotMeasured. It cannot until
        // Slice 1.4 lands, for the schema reason above; the census reads the label meanwhile, so
        // nothing downstream misclassifies it. Pinned so the day 1.4 lands, this test is the one
        // that has to change on purpose.
        var skipped = EvalResult.Skipped(new StubEval(), "no tool definitions were supplied");

        var json = JsonSerializer.Serialize(skipped.Score, s_persistenceLike);

        Assert.DoesNotContain("measurement", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MeasurementState.Measured, skipped.Score.Measurement);
        Assert.Equal(MeasurementState.NotMeasured, skipped.Score.CensusBucket());
    }

    private sealed class StubEval : IEval
    {
        public string Key => "stub";
        public string Name => "Stub";
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
