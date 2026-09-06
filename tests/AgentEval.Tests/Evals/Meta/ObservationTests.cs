// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// ADR-030 §4.1. The neutral tuple everything in the meta lane consumes.
/// </summary>
public class ObservationTests
{
    [Fact]
    public void AMeasuredObservation_CannotCarryANonFiniteValue()
    {
        // Same discipline as EvalScore.Value (AE-08): a NaN that survives construction is averaged,
        // compared and serialised by everything downstream, and the producer bug surfaces three
        // layers away from where it happened.
        Assert.Throws<ArgumentException>(() => Observation.Measured("c1", "live", double.NaN));
        Assert.Throws<ArgumentException>(() => Observation.Measured("c1", "live", double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new Observation("c1", "live", double.NaN, MeasurementState.Measured));
    }

    [Fact]
    public void AnUndecidableObservation_MayCarryAPlaceholder_BecauseNobodyReadsIt()
    {
        // The placeholder is 0.0 and it is never read: CountsTowardAggregate is false, and every
        // consumer in this namespace routes through that rather than through the value.
        var notApplicable = Observation.NotApplicable("c1", "live");
        var notMeasured = Observation.NotMeasured("c1", "live");

        Assert.False(notApplicable.CountsTowardAggregate);
        Assert.False(notMeasured.CountsTowardAggregate);
        Assert.True(Observation.Measured("c1", "live", 0.0).CountsTowardAggregate);

        // ⚠ And the two are DISTINCT. NotApplicable is a corpus finding, NotMeasured an operational
        // one; they have different owners and different fixes, and pooling them hides which you have.
        Assert.NotEqual(notApplicable, notMeasured);
    }

    [Fact]
    public void AnObservationNeedsAStableIdentity()
    {
        // Every comparison in the namespace joins on these two strings. A blank key silently pools
        // unrelated rows into one, which looks exactly like a comparison that ran.
        Assert.Throws<ArgumentException>(() => Observation.Measured("", "live", 1.0));
        Assert.Throws<ArgumentException>(() => Observation.Measured("   ", "live", 1.0));
        Assert.Throws<ArgumentException>(() => Observation.Measured("c1", "", 1.0));
        Assert.Throws<ArgumentException>(() => Observation.Measured("c1", null!, 1.0));
    }

    [Fact]
    public void TheGuardSurvivesAWithCopy()
    {
        // The AE-01/AE-08 lesson, applied before it can be forgotten: a record's copy path runs the
        // property accessors, so a guard that only fires in the constructor is not a guard.
        var ok = Observation.Measured("c1", "live", 0.5);

        Assert.Throws<ArgumentException>(() => ok with { Value = double.NaN });
        Assert.Throws<ArgumentException>(() => ok with { CaseId = "  " });

        // The legal transition is still legal: a measured observation can be re-declared as one the
        // instrument did not produce, and the placeholder value comes with it.
        var degraded = ok with { State = MeasurementState.NotMeasured };
        Assert.False(degraded.CountsTowardAggregate);
    }
}
