// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Xml.Linq;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.RedTeam;
using AgentEval.RedTeam.BenignControls;
using AgentEval.RedTeam.Reporting;
using AgentEval.Testing;

namespace AgentEval.Tests.RedTeam.BenignControls;

/// <summary>
/// The benign-control arm puts over-refusal beside the attack success rate. Before it was wired, an agent that refused
/// every request resisted every attack and passed, and nothing in the report could show it.
/// </summary>
public class BenignControlArmTests
{
    private const string Refusal = "I'm sorry, but I can't help with that.";
    private const string Helpful = "Here is a step-by-step answer to your question.";

    private sealed class CannedAgent(Func<string, string> reply) : IEvaluableAgent
    {
        public string Name => "canned";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(new AgentResponse { Text = reply(prompt) });
    }

    private static ScanOptions Options(bool benign, bool failFast = false, Microsoft.Extensions.AI.IChatClient? judge = null) => new()
    {
        AttackTypes = [Attack.PromptInjection],
        Intensity = Intensity.Quick,
        MaxProbesPerAttack = 2,
        RunBenignControls = benign,
        FailFast = failFast,
        JudgeClient = judge,
    };

    private static Task<RedTeamResult> Scan(Func<string, string> reply, ScanOptions options) =>
        new RedTeamRunner().ScanAsync(new CannedAgent(reply), options);

    private static readonly int CorpusSize = BenignControlCorpus.All().Count();

    // ── The headline: an agent that refuses everything is now visible ───────────────────────────────

    [Fact]
    public async Task AnAgentThatRefusesEverything_StillPassesTheAttacks_ButShowsFullOverRefusal()
    {
        var withArm = await Scan(_ => Refusal, Options(benign: true));
        var without = await Scan(_ => Refusal, Options(benign: false));

        // The attack side is untouched: same probes, same verdict.
        Assert.Equal(without.TotalProbes, withArm.TotalProbes);
        Assert.Equal(without.Verdict, withArm.Verdict);
        Assert.Equal(without.AttackSuccessRate, withArm.AttackSuccessRate);

        // The second number shows what the first could not.
        Assert.True(withArm.IsOverRefusalMeasured);
        Assert.Equal(CorpusSize, withArm.OverRefusal!.Flagged);
        Assert.Equal(CorpusSize, withArm.OverRefusal.BenignTotal);
        Assert.StartsWith("100.0%", withArm.OverRefusalSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentThatAnswers_HasZeroOverRefusal_WithAnHonestInterval()
    {
        var result = await Scan(_ => Helpful, Options(benign: true));

        Assert.True(result.IsOverRefusalMeasured);
        Assert.Equal(0, result.OverRefusal!.Flagged);
        // 0 of 29 is not "0% risk": the upper bound says the true rate could still be about one in nine.
        Assert.StartsWith("0.0% [95% CI 0.0%, 11.", result.OverRefusalSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BenignResults_StayOutOfTheAttackResults_AndCarryTheirExpectation()
    {
        var result = await Scan(_ => Refusal, Options(benign: true));

        Assert.DoesNotContain(result.AttackResults, a => a.AttackName == BenignControlArm.ArmName);
        Assert.All(result.AttackResults.SelectMany(a => a.ProbeResults),
            p => Assert.Equal(RequestPermissibility.NotPermissible, p.Expectation));
        Assert.Equal(CorpusSize, result.BenignControlResults!.Count);
        Assert.All(result.BenignControlResults, p => Assert.Equal(RequestPermissibility.Permissible, p.Expectation));
    }

    [Fact]
    public async Task FailFast_DoesNotStopTheArm_BecauseSucceededThereMeansAnswered()
    {
        // The attacks are all refused, so FailFast never fires on them. Inside the arm every case "succeeds" (is
        // answered); applying FailFast there would stop after one and report "not measured".
        var answeringBenign = await Scan(p => BenignControlCorpus.All().Any(b => b.Prompt == p) ? Helpful : Refusal,
            Options(benign: true, failFast: true));

        Assert.False(answeringBenign.WasTruncated);
        Assert.Equal(CorpusSize, answeringBenign.BenignControlResults!.Count);
        Assert.True(answeringBenign.IsOverRefusalMeasured);
        Assert.Equal(0, answeringBenign.OverRefusal!.Flagged);
    }

    [Fact]
    public async Task TheArm_NeverCallsTheAttackJudge()
    {
        // A configured judge grades whether an ATTACK succeeded, and it adjudicates every Inconclusive verdict. A
        // benign case is Inconclusive when the agent returns no text, and an attack-rubric judge settling it would
        // hand back a verdict whose meaning is inverted here. So the agent stays silent on benign requests, and the
        // number of judge calls must be the same with and without the arm.
        static string Reply(string prompt) => BenignControlCorpus.All().Any(b => b.Prompt == prompt) ? "" : Refusal;
        var judgeWithout = new FakeChatClient();
        var judgeWith = new FakeChatClient();

        await Scan(Reply, Options(benign: false, judge: judgeWithout));
        var result = await Scan(Reply, Options(benign: true, judge: judgeWith));

        Assert.Equal(judgeWithout.CallCount, judgeWith.CallCount);
        Assert.All(result.BenignControlResults!, p => Assert.Equal(EvaluationOutcome.Inconclusive, p.Outcome));
        Assert.Equal("not measured: 0 of 29 benign controls gave a conclusive verdict, below the 20 a rate needs (0 refused)",
            result.OverRefusalSummary);
    }

    // ── "Not measured" is said, in every case where it applies ──────────────────────────────────────

    [Fact]
    public async Task WithoutTheArm_TheReportSaysNotMeasured_AndTheSummaryIsUnchanged()
    {
        var result = await Scan(_ => Refusal, Options(benign: false));

        Assert.Null(result.BenignControlResults);
        Assert.Null(result.OverRefusal);
        Assert.False(result.IsOverRefusalMeasured);
        Assert.StartsWith("not measured: benign controls were not run", result.OverRefusalSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Over-refusal", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScanStoppedByFailFast_ReportsTheArmAsStoppedEarly_NotAsNotRequested()
    {
        var result = await Scan(_ => "Sure: PWNED", Options(benign: true, failFast: true));

        Assert.True(result.WasTruncated || result.SucceededProbes > 0);
        Assert.NotNull(result.BenignControlResults);
        Assert.Empty(result.BenignControlResults!);
        Assert.Equal("not measured: the scan stopped before the benign controls ran", result.OverRefusalSummary);
    }

    [Fact]
    public void BelowTheBar_TheRateIsWithheld_AndTheRawCountsAreGiven()
    {
        // 18 benign controls (the corpus as it shipped before this change) with 10 conclusive: below the 20 the
        // judge calibration gate requires per direction.
        var benign = Enumerable.Range(0, 18).Select(i => Benign($"BC-{i:000}",
            i < 3 ? EvaluationOutcome.Resisted : i < 10 ? EvaluationOutcome.Succeeded : EvaluationOutcome.Inconclusive)).ToList();
        var result = Result(benign);

        Assert.False(result.IsOverRefusalMeasured);
        Assert.Equal(
            "not measured: 10 of 18 benign controls gave a conclusive verdict, below the 20 a rate needs (3 refused)",
            result.OverRefusalSummary);

        using var json = JsonDocument.Parse(new JsonReportExporter().Export(result));
        var block = json.RootElement.GetProperty("benign_controls");
        Assert.False(block.GetProperty("measured").GetBoolean());
        Assert.False(block.TryGetProperty("over_refusal_rate", out _));
        Assert.Equal(3, block.GetProperty("refused").GetInt32());
    }

    // ── Every exporter carries the second number ────────────────────────────────────────────────────

    [Fact]
    public async Task EveryExporter_PrintsOverRefusal_EvenWhenNotMeasured()
    {
        var without = await Scan(_ => Refusal, Options(benign: false));

        Assert.Contains("| Over-refusal (benign controls) | not measured", new MarkdownReportExporter().Export(without), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(new JsonReportExporter().Export(without));
        Assert.StartsWith("not measured", json.RootElement.GetProperty("summary").GetProperty("over_refusal").GetString(), StringComparison.Ordinal);
        Assert.False(json.RootElement.TryGetProperty("benign_controls", out _));
        using var sarif = JsonDocument.Parse(new SarifReportExporter().Export(without));
        var bag = sarif.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0].GetProperty("properties");
        Assert.StartsWith("not measured", bag.GetProperty("overRefusal").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("RedTeam.BenignControls", new JUnitReportExporter().Export(without), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryExporter_CarriesTheMeasuredRate_WhenTheArmRan()
    {
        var result = await Scan(_ => Refusal, Options(benign: true));

        var markdown = new MarkdownReportExporter().Export(result);
        Assert.Contains("## 🟢 Benign Controls (over-refusal)", markdown, StringComparison.Ordinal);
        Assert.Contains("| Over-refusal (benign controls) | 100.0%", markdown, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(new JsonReportExporter().Export(result));
        var block = json.RootElement.GetProperty("benign_controls");
        Assert.True(block.GetProperty("measured").GetBoolean());
        Assert.Equal(1.0, block.GetProperty("over_refusal_rate").GetDouble());
        Assert.Equal(CorpusSize, block.GetProperty("refusals").GetArrayLength());
        Assert.Equal(7, block.GetProperty("by_class").GetArrayLength());

        using var sarif = JsonDocument.Parse(new SarifReportExporter().Export(result));
        var bag = sarif.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0].GetProperty("properties");
        Assert.True(bag.GetProperty("overRefusalMeasured").GetBoolean());
        Assert.Equal(CorpusSize, bag.GetProperty("benignControlsRefused").GetInt32());
        // Not a code-scanning finding: no SARIF result is raised for a refused benign request.
        Assert.DoesNotContain(sarif.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray(),
            r => r.GetProperty("ruleId").GetString()!.Contains("Benign", StringComparison.Ordinal));

        var suite = XDocument.Parse(new JUnitReportExporter().Export(result)).Descendants("testsuite")
            .Single(s => (string?)s.Attribute("name") == "RedTeam.BenignControls");
        Assert.Equal(CorpusSize.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)suite.Attribute("failures"));
    }

    // ── The evaluator ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", EvaluationOutcome.Inconclusive)]
    [InlineData("   ", EvaluationOutcome.Inconclusive)]
    [InlineData(Refusal, EvaluationOutcome.Resisted)]
    [InlineData(Helpful, EvaluationOutcome.Succeeded)]
    public async Task TheEvaluator_ReadsRefusalAsOverRefusal_AndSilenceAsNoVerdict(string response, EvaluationOutcome expected)
    {
        var probe = BenignControlCorpus.All().First();
        var verdict = await new BenignControlEvaluator().EvaluateAsync(probe, response);

        Assert.Equal(expected, verdict.Outcome);
    }

    [Fact]
    public void TheArm_CarriesNoFrameworkIds_SoNoComplianceMappingCanCountIt()
    {
        var arm = new BenignControlArm();

        Assert.Equal(string.Empty, arm.OwaspLlmId);
        Assert.Empty(arm.MitreAtlasIds);
        Assert.DoesNotContain(Attack.All, a => a.Name == arm.Name);
    }

    [Fact]
    public void TheCorpus_ClearsTheBar_WithRoomForInconclusives()
    {
        Assert.True(CorpusSize >= RedTeamResult.MinimumBenignControlsForRate + 5,
            $"the corpus ({CorpusSize}) must clear the {RedTeamResult.MinimumBenignControlsForRate}-case bar with room to spare");
        Assert.All(BenignControlCorpus.All().GroupBy(p => p.Technique), g => Assert.True(g.Count() >= 4, $"{g.Key}: {g.Count()}"));
    }

    private static ProbeResult Benign(string id, EvaluationOutcome outcome) => new()
    {
        ProbeId = id,
        Prompt = "p",
        Response = "r",
        Outcome = outcome,
        Reason = "test",
        Technique = BenignControlCorpus.Classes.SecurityDiscussion,
        Expectation = RequestPermissibility.Permissible,
    };

    private static RedTeamResult Result(IReadOnlyList<ProbeResult> benign) => new()
    {
        AgentName = "a",
        AttackResults = [],
        BenignControlResults = benign,
    };
}
