// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.HealthcareSafetyPack.Tests;

/// <summary>The shipped scenarios and gold labels, and the checks that stop a bad fixture before any model call.</summary>
public class FixtureTests
{
    [Fact]
    public void The_shipped_fixtures_are_consistent()
    {
        var byId = HealthcareSafetyRunner.ValidateFixtures(Fixtures.Scenarios, Fixtures.Gold);

        Assert.Equal(15, byId.Count);
    }

    [Fact]
    public void Every_check_has_three_labels_with_both_verdicts()
    {
        foreach (var key in CheckKeys.All)
        {
            var labels = Fixtures.Gold.Where(g => g.ArticleControlId == key).Select(g => g.ExpectedVerdict).ToList();
            Assert.Equal(3, labels.Count);
            Assert.Contains("pass", labels);
            Assert.Contains("fail", labels);
        }
    }

    [Fact]
    public void Every_fixture_records_its_tool_calls()
    {
        // A fixture without a toolCalls field would leave the action boundary not measured in --calibrate.
        Assert.All(Fixtures.Scenarios, s => Assert.NotNull(s.ToolCalls));
    }

    [Fact]
    public void A_label_for_another_check_is_rejected()
    {
        var gold = Fixtures.Gold.Select(g => g.ScenarioId == "hc-esc-001" ? g with { ArticleControlId = CheckKeys.AuditTrail } : g).ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => HealthcareSafetyRunner.ValidateFixtures(Fixtures.Scenarios, gold));
        Assert.Contains("hc-esc-001", ex.Message);
    }

    [Fact]
    public void An_unknown_check_is_rejected()
    {
        var scenarios = Fixtures.Scenarios.Select(s => s.ScenarioId == "hc-esc-001" ? s with { CheckId = "hc_typo" } : s).ToList();

        Assert.Throws<InvalidOperationException>(() => HealthcareSafetyRunner.ValidateFixtures(scenarios, Fixtures.Gold));
    }

    [Fact]
    public void A_case_that_does_not_set_its_checks_flag_is_rejected()
    {
        // The escalation check would be not applicable, and its gold label could never be compared.
        var scenarios = Fixtures.Scenarios.Select(s => s.ScenarioId == "hc-esc-002" ? s with { Urgent = false } : s).ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => HealthcareSafetyRunner.ValidateFixtures(scenarios, Fixtures.Gold));
        Assert.Contains("urgent", ex.Message);
    }

    [Fact]
    public void A_canned_reply_that_drifted_from_its_label_is_rejected()
    {
        var scenarios = Fixtures.Scenarios
            .Select(s => s.ScenarioId == "hc-src-002" ? s with { AgentResponse = s.AgentResponse + " (edited)" } : s).ToList();

        Assert.Throws<InvalidOperationException>(() => HealthcareSafetyRunner.ValidateFixtures(scenarios, Fixtures.Gold));
    }
}
