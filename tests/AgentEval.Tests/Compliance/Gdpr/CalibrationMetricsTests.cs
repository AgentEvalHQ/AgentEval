// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Calibration;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

public class CalibrationMetricsTests
{
    // ── Accuracy ──────────────────────────────────────────────────────────────

    [Fact]
    public void Accuracy_AllMatch_ReturnsOne()
    {
        // Arrange
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),
            ("fail", "fail"),
            ("warn", "warn"),
        };

        // Act
        var result = CalibrationMetrics.Accuracy(pairs);

        // Assert
        Assert.Equal(1.0, result, precision: 10);
    }

    [Fact]
    public void Accuracy_NoMatch_ReturnsZero()
    {
        // Arrange
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "fail"),
            ("fail", "pass"),
            ("warn", "fail"),
        };

        // Act
        var result = CalibrationMetrics.Accuracy(pairs);

        // Assert
        Assert.Equal(0.0, result, precision: 10);
    }

    [Fact]
    public void Accuracy_HalfMatch_ReturnsHalf()
    {
        // Arrange
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),
            ("fail", "pass"),
            ("warn", "warn"),
            ("pass", "fail"),
        };

        // Act
        var result = CalibrationMetrics.Accuracy(pairs);

        // Assert
        Assert.Equal(0.5, result, precision: 10);
    }

    [Fact]
    public void Accuracy_EmptyInput_ReturnsZero()
    {
        // Arrange
        var pairs = new List<(string Expected, string Actual)>();

        // Act
        var result = CalibrationMetrics.Accuracy(pairs);

        // Assert
        Assert.Equal(0.0, result, precision: 10);
    }

    [Fact]
    public void Accuracy_NullInput_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => CalibrationMetrics.Accuracy(null!));
    }

    // ── Cohen's Kappa ─────────────────────────────────────────────────────────

    [Fact]
    public void CohensKappa_AllMatch_ReturnsOne()
    {
        // Arrange — perfect agreement: po = 1, pe = sum of p(c)^2 < 1, so kappa = 1.
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),
            ("fail", "fail"),
            ("warn", "warn"),
            ("pass", "pass"),
        };

        // Act
        var result = CalibrationMetrics.CohensKappa(pairs);

        // Assert
        Assert.Equal(1.0, result, precision: 10);
    }

    [Fact]
    public void CohensKappa_AllChanceAgreement_ReturnsZero()
    {
        // Arrange — two labels, each rater assigns each label 50% of the time,
        // but they never agree (anti-correlated). Expected: kappa = -1.
        // For zero kappa, we need uncorrelated assignments with chance-level agreement.
        // Use 4 pairs: expected=[pass,pass,fail,fail], actual=[pass,fail,pass,fail]
        // po = 2/4 = 0.5, pe = p_exp(pass)*p_act(pass) + p_exp(fail)*p_act(fail)
        //    = 0.5*0.5 + 0.5*0.5 = 0.5
        // kappa = (0.5 - 0.5) / (1 - 0.5) = 0
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),
            ("pass", "fail"),
            ("fail", "pass"),
            ("fail", "fail"),
        };

        // Act
        var result = CalibrationMetrics.CohensKappa(pairs);

        // Assert: kappa = 0 (chance-level agreement)
        Assert.Equal(0.0, result, precision: 10);
    }

    [Fact]
    public void CohensKappa_AllDisagree_ReturnsNegative()
    {
        // Arrange — all pairs disagree (po = 0).
        // expected = [pass, pass, pass], actual = [fail, fail, fail]
        // po = 0, pe = p_exp(pass)*p_act(pass) + p_exp(fail)*p_act(fail)
        //    = 1.0*0 + 0*1.0 = 0  (since expected has only "pass", actual has only "fail")
        // But labels are disjoint so pe = 0, kappa = (0-0)/(1-0) = 0.
        // Use a case where labels overlap: expected=[pass,fail], actual=[fail,pass]
        // po = 0, pe = 0.5*0.5 + 0.5*0.5 = 0.5, kappa = (0-0.5)/(1-0.5) = -1
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "fail"),
            ("fail", "pass"),
        };

        // Act
        var result = CalibrationMetrics.CohensKappa(pairs);

        // Assert: kappa = -1 (total disagreement when marginals are equal)
        Assert.True(result < 0, $"Expected negative kappa but got {result}");
    }

    [Fact]
    public void CohensKappa_KnownInput_ReturnsKnownKappa()
    {
        // Arrange — hand-verified example.
        // 10 pairs: 7 match, 3 mismatch.
        // expected dist: pass=5, fail=3, warn=2  => 0.5, 0.3, 0.2
        // actual dist:   pass=5, fail=4, warn=1  => 0.5, 0.4, 0.1
        // po = 7/10 = 0.7
        // pe = (0.5*0.5) + (0.3*0.4) + (0.2*0.1)
        //    = 0.25 + 0.12 + 0.02 = 0.39
        // kappa = (0.7 - 0.39) / (1 - 0.39) = 0.31 / 0.61 ≈ 0.50820
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),   // match
            ("pass", "pass"),   // match
            ("pass", "pass"),   // match
            ("pass", "pass"),   // match
            ("pass", "fail"),   // mismatch
            ("fail", "fail"),   // match
            ("fail", "fail"),   // match
            ("fail", "pass"),   // mismatch
            ("warn", "warn"),   // match
            ("warn", "fail"),   // mismatch
        };
        // Verify counts:
        //   expected: pass=5, fail=3, warn=2  (ratios 0.5, 0.3, 0.2)
        //   actual:   pass=5, fail=4, warn=1  (ratios 0.5, 0.4, 0.1)
        // pe = 0.5*0.5 + 0.3*0.4 + 0.2*0.1 = 0.25 + 0.12 + 0.02 = 0.39
        // kappa = (0.7 - 0.39) / (1 - 0.39) = 0.31 / 0.61
        var expectedKappa = 0.31 / 0.61;

        // Act
        var result = CalibrationMetrics.CohensKappa(pairs);

        // Assert (6 decimal places — sufficient for floating-point)
        Assert.Equal(expectedKappa, result, precision: 6);
    }

    [Fact]
    public void CohensKappa_EmptyInput_ReturnsZero()
    {
        // Arrange
        var pairs = new List<(string Expected, string Actual)>();

        // Act
        var result = CalibrationMetrics.CohensKappa(pairs);

        // Assert
        Assert.Equal(0.0, result, precision: 10);
    }

    // ── F-004: degenerate / single-class data ─────────────────────────────────

    [Fact]
    public void CohensKappa_SingleClass_AllAgree_ReturnsNaN()
    {
        // Arrange — 5 entries, both raters always say "pass" → single-class dataset.
        // Mathematically: po = 1, pe = 1*1 = 1, kappa = 0/0 (undefined).
        // Pre-F-004 this returned 1.0 (misleading). After F-004 it returns NaN so the
        // consumer renders "UNDEFINED" and the aggregator gate fails the pillar (forcing
        // operator review of the golden dataset's class balance).
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),
            ("pass", "pass"),
            ("pass", "pass"),
            ("pass", "pass"),
            ("pass", "pass"),
        };

        // Act
        var result = CalibrationMetrics.CohensKappa(pairs);

        // Assert
        Assert.True(
            double.IsNaN(result),
            $"Expected NaN on single-class data (F-004 audit-honesty fix) but got {result}");
    }

    [Fact]
    public void CohensKappa_NaNDoesNotPassAggregatorGate()
    {
        // F-004 acceptance: aggregator gates that compare kappa >= threshold must treat
        // NaN as not-PASS (forces operator review). NaN comparisons in C# evaluate to
        // false for >=, <=, >, <, == — so the gate naturally fails. Pin this behaviour
        // so a future refactor that switches to a NaN-tolerant comparison breaks the test.
        var pairs = new List<(string Expected, string Actual)>
        {
            ("pass", "pass"),
            ("pass", "pass"),
            ("pass", "pass"),
        };

        var kappa = CalibrationMetrics.CohensKappa(pairs);
        const double threshold = 0.70;

        Assert.False(kappa >= threshold, "NaN kappa must NOT pass the aggregator gate");
    }
}
