// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>
/// Fleet Correlation Layer P1/P2 — direct unit coverage for <see cref="FleetCorrelator"/>, focused on the
/// three false-positive guards its own design doc calls out: (1) 2+ DISTINCT families, not events, (2) a
/// bounded turn window, not whole-session, (3) abstain when fewer than 2 gates report confidence at all.
/// </summary>
public class FleetCorrelatorTests
{
    private static GateVerdict Verdict(string policy, double? confidence)
        => GateVerdict.Allow(policy) with
        {
            Confidence = confidence,
            Provenance = confidence is null
                ? null
                : new GateProvenance(
                    policy,
                    Array.Empty<string>(),
                    Threshold: 1.0,
                    ActualValue: confidence),
        };

    // ── Core escalation ──

    [Fact]
    public void TwoDistinctFamilies_BothAboveFloor_SameTurn_Escalates()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("judge:indirect-injection", 0.5), "pre");
        correlator.Observe(Verdict("judge:goal-hijack-drift", 0.6), "pre");

        var result = correlator.CheckCorrelation();

        Assert.NotNull(result);
        Assert.Equal(GateAction.Block, result!.Action);
        Assert.Equal("fleet-correlation", result.PolicyName);
        Assert.Contains("judge:indirect-injection", result.Reason);
        Assert.Contains("judge:goal-hijack-drift", result.Reason);
        Assert.Equal(0.55, result.Confidence);   // average of 0.5 and 0.6
    }

    [Fact]
    public void SameFamilyThreeTimes_NeverEscalates_ProvesNothingNewTwice()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("judge:indirect-injection", 0.5), "pre");
        correlator.Observe(Verdict("judge:indirect-injection", 0.6), "pre");
        correlator.Observe(Verdict("judge:indirect-injection", 0.7), "pre");

        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void OnlyOneFamilyReportsConfidence_Abstains_NotEnoughToCorrelateYet()
    {
        // A fleet with only deterministic/regex gates and zero judges can't correlate — must say so, not
        // starve toward a false trigger.
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("judge:indirect-injection", 0.9), "pre");
        correlator.Observe(GateVerdict.Allow("prompt-template-drift"), "pre");   // no Confidence at all

        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void NoObservationsAtAll_Abstains()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        Assert.Null(correlator.CheckCorrelation());
    }

    // ── SoftSignalFloor ──

    [Fact]
    public void TwoDistinctFamilies_OneBelowFloor_DoesNotEscalate()
    {
        var correlator = new FleetCorrelator(new FleetCorrelatorOptions { SoftSignalFloor = 0.4 });
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("judge:a", 0.5), "pre");
        correlator.Observe(Verdict("judge:b", 0.3), "pre");   // below the 0.4 floor

        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void ExactlyAtFloor_Counts_Inclusive()
    {
        var correlator = new FleetCorrelator(new FleetCorrelatorOptions { SoftSignalFloor = 0.4 });
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("judge:a", 0.4), "pre");
        correlator.Observe(Verdict("judge:b", 0.4), "pre");

        Assert.NotNull(correlator.CheckCorrelation());
    }

    // ── Bounded turn window ──

    [Fact]
    public void TwoDistinctFamilies_OutsideWindow_DoesNotEscalate()
    {
        var correlator = new FleetCorrelator(new FleetCorrelatorOptions { WindowTurns = 5 });
        correlator.AdvanceTurn();   // turn 1
        correlator.Observe(Verdict("judge:a", 0.9), "pre");

        for (var i = 0; i < 6; i++)
        {
            correlator.AdvanceTurn();   // now at turn 7 — turn 1 is outside a 5-turn trailing window
        }

        correlator.Observe(Verdict("judge:b", 0.9), "pre");

        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void TwoDistinctFamilies_WithinWindow_Escalates()
    {
        var correlator = new FleetCorrelator(new FleetCorrelatorOptions { WindowTurns = 5 });
        correlator.AdvanceTurn();   // turn 1
        correlator.Observe(Verdict("judge:a", 0.9), "pre");

        for (var i = 0; i < 3; i++)
        {
            correlator.AdvanceTurn();   // now at turn 4 — within the 5-turn window
        }

        correlator.Observe(Verdict("judge:b", 0.9), "pre");

        Assert.NotNull(correlator.CheckCorrelation());
    }

    // ── Family grouping ──

    [Fact]
    public void CustomFamilyOf_GroupsPolicyNamesTogether_TreatedAsOneFamily()
    {
        var correlator = new FleetCorrelator(new FleetCorrelatorOptions
        {
            FamilyOf = policy => policy.StartsWith("judge:", StringComparison.Ordinal) ? "judge" : policy,
        });
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("judge:indirect-injection", 0.9), "pre");
        correlator.Observe(Verdict("judge:goal-hijack-drift", 0.9), "pre");   // grouped into the SAME "judge" family

        // Both map to family "judge" — only 1 distinct family, must NOT escalate under the default MinDistinctFamilies=2.
        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void DefaultFamilyOf_IsIdentity_PolicyNameIsTheFamily()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("policy-a", 0.9), "pre");
        correlator.Observe(Verdict("policy-b", 0.9), "pre");

        Assert.NotNull(correlator.CheckCorrelation());
    }

    // ── MinDistinctFamilies (configurable, floor 2) ──

    [Fact]
    public void MinDistinctFamiliesThree_TwoFamiliesInsufficient()
    {
        var correlator = new FleetCorrelator(new FleetCorrelatorOptions { MinDistinctFamilies = 3 });
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("a", 0.9), "pre");
        correlator.Observe(Verdict("b", 0.9), "pre");

        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void MinDistinctFamiliesThree_ThreeFamiliesEscalates()
    {
        var correlator = new FleetCorrelator(new FleetCorrelatorOptions { MinDistinctFamilies = 3 });
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("a", 0.9), "pre");
        correlator.Observe(Verdict("b", 0.9), "pre");
        correlator.Observe(Verdict("c", 0.9), "pre");

        Assert.NotNull(correlator.CheckCorrelation());
    }

    // ── Validation ──

    [Fact]
    public void MinDistinctFamiliesOne_ThrowsAtConstruction()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new FleetCorrelator(new FleetCorrelatorOptions { MinDistinctFamilies = 1 }));

    [Fact]
    public void SoftSignalFloorNegative_ThrowsAtConstruction()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new FleetCorrelator(new FleetCorrelatorOptions { SoftSignalFloor = -0.1 }));

    [Fact]
    public void SoftSignalFloorAboveOne_ThrowsAtConstruction()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new FleetCorrelator(new FleetCorrelatorOptions { SoftSignalFloor = 1.1 }));

    [Fact]
    public void WindowTurnsZero_ThrowsAtConstruction()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new FleetCorrelator(new FleetCorrelatorOptions { WindowTurns = 0 }));

    [Fact]
    public void NullOptions_UsesDefaults_DoesNotThrow()
    {
        var correlator = new FleetCorrelator(options: null);
        correlator.AdvanceTurn();
        Assert.Null(correlator.CheckCorrelation());   // no observations — abstains, doesn't throw
    }

    [Fact]
    public void ObserveNullVerdict_Throws()
        => Assert.Throws<ArgumentNullException>(() => new FleetCorrelator().Observe(null!, "pre"));

    // ── Honesty: a correlation Block never fabricates a maskable span ──

    [Fact]
    public void CorrelationVerdict_NeverHasRedactedText_NamesAPatternNotASpan()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("a", 0.9), "pre");
        correlator.Observe(Verdict("b", 0.9), "pre");

        var result = correlator.CheckCorrelation();

        Assert.Null(result!.RedactedText);
    }

    [Fact]
    public void CorrelationVerdict_ReasonStatesItIsWeakerThanAnySingleGate()
    {
        // This repo's honesty motto applies to the correlator's own explanations, not just judges.
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(Verdict("a", 0.9), "pre");
        correlator.Observe(Verdict("b", 0.9), "pre");

        var result = correlator.CheckCorrelation();

        Assert.Contains("WEAKER", result!.Reason, StringComparison.Ordinal);
    }
}
