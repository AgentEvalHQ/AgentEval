// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using Microsoft.Extensions.AI;

namespace AgentEval.HealthcareSafetyPack.Tests;

/// <summary>
/// The runner end to end, offline: a scripted model plays the agent behind the real function-invoking client, and a
/// scripted judge answers the checks. Both modes of the sample run through the same code as Program.cs.
/// </summary>
public class EndToEndTests
{
    /// <summary>A judge that gives each canned reply's target check its gold verdict, and passes every other check.</summary>
    private static ScriptedJudge JudgeThatAgreesWithGold(params string[] flip) => new(call =>
    {
        var scenario = Fixtures.Scenarios.Single(s => s.AgentResponse == call.Output);
        if (call.Check != scenario.CheckId)
            return ScriptedJudge.Pass;
        var expected = Fixtures.Label(scenario.ScenarioId).ExpectedVerdict;
        var verdict = flip.Contains(scenario.ScenarioId) ? (expected == "pass" ? "fail" : "pass") : expected;
        return verdict == "pass" ? ScriptedJudge.Pass : ScriptedJudge.Fail;
    });

    private static async Task<IReadOnlyList<CaseOutcome>> CalibrateAll(ScriptedJudge judge)
    {
        var pack = HealthcareSafetyPackFactory.Build(judge, "scripted");
        var byId = HealthcareSafetyRunner.ValidateFixtures(Fixtures.Scenarios, Fixtures.Gold);
        var outcomes = new List<CaseOutcome>();
        foreach (var label in Fixtures.Gold)
            outcomes.Add(await HealthcareSafetyRunner.GradeCaseAsync(pack, byId[label.ScenarioId], label, agent: null));
        return outcomes;
    }

    [Fact]
    public async Task Calibration_with_a_judge_that_agrees_reports_full_agreement_on_every_check()
    {
        // The deterministic check agrees too, which proves the canned tool calls match the gold labels.
        var outcomes = await CalibrateAll(JudgeThatAgreesWithGold());

        Assert.All(outcomes, o => Assert.True(o.Graded, o.Error));
        foreach (var check in HealthcareSafetyRunner.Agreement(outcomes))
        {
            Assert.Equal(3, check.Pairs.Count);
            Assert.Equal(0, check.NotMeasured);
            Assert.Equal(1.0, AgreementMetrics.Accuracy(check.Pairs));
        }
    }

    [Fact]
    public async Task Calibration_counts_one_disagreement_against_that_check_only()
    {
        var outcomes = await CalibrateAll(JudgeThatAgreesWithGold(flip: "hc-src-003"));

        var agreement = HealthcareSafetyRunner.Agreement(outcomes).ToDictionary(a => a.CheckKey);
        Assert.Equal(2.0 / 3.0, AgreementMetrics.Accuracy(agreement[CheckKeys.SourceSupport].Pairs), 6);
        Assert.Equal(1.0, AgreementMetrics.Accuracy(agreement[CheckKeys.AuditTrail].Pairs));
    }

    [Fact]
    public async Task Calibration_leaves_a_verdict_the_judge_did_not_produce_out_of_agreement()
    {
        var judge = new ScriptedJudge(call =>
            call.Check == CheckKeys.MedicationSafety && call.Output == Fixtures.Scenario("hc-med-002").AgentResponse
                ? ScriptedJudge.NoVerdict
                : ScriptedJudge.Pass);

        var outcomes = await CalibrateAll(judge);

        var medication = HealthcareSafetyRunner.Agreement(outcomes).Single(a => a.CheckKey == CheckKeys.MedicationSafety);
        Assert.Equal(1, medication.NotMeasured);
        Assert.Equal(2, medication.Pairs.Count);
    }

    [Fact]
    public async Task Live_an_agent_that_prescribes_without_permission_is_recorded_and_fails_the_boundary()
    {
        // hc-ab-001 permits nothing. The scripted model calls the prescribing tool, then answers.
        var model = new ScriptedModel(ScriptedModel.Calls("prescribe_medication"), ScriptedModel.Says("I've sent the prescription."));
        var judge = ScriptedJudge.PassingAll();
        var pack = HealthcareSafetyPackFactory.Build(judge, "scripted");

        var outcome = await HealthcareSafetyRunner.GradeCaseAsync(
            pack, Fixtures.Scenario("hc-ab-001"), Fixtures.Label("hc-ab-001"), model.AsAgent());

        Assert.True(outcome.Graded, outcome.Error);
        Assert.Equal("I've sent the prescription.", outcome.Response);
        Assert.Equal(new[] { "prescribe_medication" }, outcome.ToolCalls!.Select(c => c.Name).ToArray());
        Assert.Equal("fail", outcome.Target!.Score.Label);
        Assert.Equal("critical", outcome.Target.Score.Severity);
        Assert.Equal("fail", outcome.Result!.Score.Label);

        // The agent was told what this deployment permits, and was offered the three recording tools.
        var system = model.Requests[0].First(m => m.Role == ChatRole.System).Text;
        Assert.Contains("permits you to take are: none", system);
        Assert.Equal(
            new[] { "order_lab", "prescribe_medication", "schedule_appointment" },
            model.LastOptions!.Tools!.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Live_an_agent_that_uses_only_a_permitted_tool_passes_the_boundary()
    {
        // hc-ab-002 permits scheduling.
        var model = new ScriptedModel(ScriptedModel.Calls("schedule_appointment"), ScriptedModel.Says("Booked, as you asked."));
        var pack = HealthcareSafetyPackFactory.Build(ScriptedJudge.PassingAll(), "scripted");

        var outcome = await HealthcareSafetyRunner.GradeCaseAsync(
            pack, Fixtures.Scenario("hc-ab-002"), Fixtures.Label("hc-ab-002"), model.AsAgent());

        Assert.Equal("pass", outcome.Target!.Score.Label);
        Assert.Contains("permits you to take are: schedule", model.Requests[0].First(m => m.Role == ChatRole.System).Text);
    }

    [Fact]
    public async Task Live_the_judges_grade_the_agents_reply_not_the_canned_one()
    {
        var model = new ScriptedModel(ScriptedModel.Says("Call emergency services now."));
        var judge = ScriptedJudge.PassingAll();
        var pack = HealthcareSafetyPackFactory.Build(judge, "scripted");

        await HealthcareSafetyRunner.GradeCaseAsync(pack, Fixtures.Scenario("hc-esc-001"), Fixtures.Label("hc-esc-001"), model.AsAgent());

        Assert.All(judge.Calls, c => Assert.Equal("Call emergency services now.", c.Output));
    }

    [Fact]
    public async Task An_agent_failure_is_a_case_not_graded_not_a_crash()
    {
        var model = new ScriptedModel(ScriptedModel.Throws(new HttpRequestException("503 Service Unavailable")));
        var judge = ScriptedJudge.PassingAll();
        var pack = HealthcareSafetyPackFactory.Build(judge, "scripted");

        var outcome = await HealthcareSafetyRunner.GradeCaseAsync(
            pack, Fixtures.Scenario("hc-esc-001"), Fixtures.Label("hc-esc-001"), model.AsAgent());

        Assert.False(outcome.Graded);
        Assert.Contains("503", outcome.Error);
        Assert.Empty(judge.Calls);
        Assert.All(HealthcareSafetyRunner.Agreement([outcome]), a => Assert.Empty(a.Pairs));
    }

    [Fact]
    public async Task A_timeout_is_a_case_not_graded_but_a_requested_cancellation_stops_the_run()
    {
        var pack = HealthcareSafetyPackFactory.Build(ScriptedJudge.PassingAll(), "scripted");
        var scenario = Fixtures.Scenario("hc-esc-001");
        var label = Fixtures.Label("hc-esc-001");

        // An HTTP timeout surfaces as a TaskCanceledException that nobody requested.
        var timedOut = new ScriptedModel(ScriptedModel.Throws(new TaskCanceledException("The request timed out.")));
        var outcome = await HealthcareSafetyRunner.GradeCaseAsync(pack, scenario, label, timedOut.AsAgent());
        Assert.False(outcome.Graded);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = new ScriptedModel(ScriptedModel.Says("unused"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HealthcareSafetyRunner.GradeCaseAsync(pack, scenario, label, cancelled.AsAgent(), cts.Token));
    }

    [Fact]
    public async Task A_judge_failure_is_a_case_not_graded_not_a_crash()
    {
        var judge = new ScriptedJudge(_ => throw new InvalidOperationException("content filter"));
        var pack = HealthcareSafetyPackFactory.Build(judge, "scripted");

        var outcome = await HealthcareSafetyRunner.GradeCaseAsync(pack, Fixtures.Scenario("hc-aud-001"), Fixtures.Label("hc-aud-001"), agent: null);

        Assert.False(outcome.Graded);
        Assert.Contains("content filter", outcome.Error);
    }
}
