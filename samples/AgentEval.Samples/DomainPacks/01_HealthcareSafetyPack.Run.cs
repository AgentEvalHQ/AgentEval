// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.HealthcareSafetyPack;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample O1's work once it has its model: the agent answers three cases, then the same checks grade three canned
/// unsafe replies as controls. <c>RunAsync</c> supplies the configured model; the end-to-end tests in
/// samples/AgentEval.HealthcareSafetyPack.Tests link this file and supply a scripted one, so what the sample does with
/// its model is the code the tests run.
/// </summary>
public static partial class HealthcareSafetyPackSample
{
    /// <summary>
    /// One case per kind of check: an urgent red flag, an action the deployment does not permit, a dose request
    /// without the details a safe dose depends on. The gold set labels each one's canned reply a fail.
    /// </summary>
    public static readonly IReadOnlyList<string> CaseIds = ["hc-esc-001", "hc-ab-001", "hc-med-001"];

    /// <summary>What the sample produced: the agent's three cases and the three controls.</summary>
    public sealed record Outcome(IReadOnlyList<CaseOutcome> Live, IReadOnlyList<CaseOutcome> Controls)
    {
        /// <summary>Controls whose target check produced a verdict. Only these say anything about the checks.</summary>
        public int ControlsMeasured => Controls.Count(IsMeasured);

        /// <summary>Measured controls whose target check failed, as the gold set says it should.</summary>
        public int ControlsCaught => Controls.Count(c => IsMeasured(c) && c.Target!.Score.Label == "fail");

        /// <summary>Measured controls the target check passed: the check missed a reply written to fail it.</summary>
        public int ControlsMissed => ControlsMeasured - ControlsCaught;
    }

    /// <summary>Runs the sample against <paramref name="agent"/> and <paramref name="judge"/>, writing to <paramref name="output"/>.</summary>
    /// <param name="agent">A chat client that invokes functions (built with <c>UseFunctionInvocation</c>).</param>
    /// <param name="judge">The judge the four free-text checks ask.</param>
    /// <param name="judgeModel">The judge's model, recorded in each check's provenance.</param>
    /// <param name="output">Where the sample prints.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<Outcome> ExecuteAsync(
        IChatClient agent, IEvaluator judge, string judgeModel, TextWriter output, CancellationToken ct = default)
    {
        output.WriteLine("📝 Step 1: Building the pack...\n");
        var dataDir = HealthcareSafetyData.ResolveDataDirectory();
        var scenarios = await HealthcareSafetyData.LoadJsonlAsync<HealthcareScenario>(Path.Combine(dataDir, "scenarios.jsonl"), ct);
        var gold = await HealthcareSafetyData.LoadJsonlAsync<GoldLabel>(Path.Combine(dataDir, "gold.jsonl"), ct);
        var byId = HealthcareSafetyRunner.ValidateFixtures(scenarios, gold);
        var labels = gold.ToDictionary(g => g.ScenarioId, StringComparer.Ordinal);

        var pack = HealthcareSafetyPackFactory.Build(judge, judgeModel);
        output.WriteLine($"   ✓ '{pack.Name}': {pack.Components.Count} checks, {pack.Aggregation.Name} aggregation, no threshold");
        output.WriteLine("     hc_escalation, hc_medication_safety, hc_source_support, hc_audit_trail: LLM judges");
        output.WriteLine("     hc_action_boundary: deterministic, reads the tool calls the run recorded\n");

        output.WriteLine("📝 Step 2: The agent answers three cases, with three tools that only record their calls...\n");
        output.WriteLine("📊 RESULTS:");
        output.WriteLine(new string('─', 60));
        var live = new List<CaseOutcome>();
        foreach (var id in CaseIds)
        {
            var outcome = await HealthcareSafetyRunner.GradeCaseAsync(pack, byId[id], labels[id], agent, ct);
            live.Add(outcome);
            PrintCase(output, outcome, control: false);
        }

        output.WriteLine("\n📝 Step 3: Controls - the same checks on three CANNED unsafe replies from the pack's gold set...\n");
        output.WriteLine("   A pack that never fails proves nothing. Each reply below was written to fail its check;");
        output.WriteLine("   none of them came from the agent.");
        output.WriteLine(new string('─', 60));
        var controls = new List<CaseOutcome>();
        foreach (var id in CaseIds)
        {
            var outcome = await HealthcareSafetyRunner.GradeCaseAsync(pack, byId[id], labels[id], agent: null, ct);
            controls.Add(outcome);
            PrintCase(output, outcome, control: true);
        }

        var result = new Outcome(live, controls);
        var notMeasured = controls.Count - result.ControlsMeasured;
        output.WriteLine();
        output.WriteLine($"   Controls caught: {result.ControlsCaught} of {result.ControlsMeasured} measured" +
                         (notMeasured > 0 ? $" ({notMeasured} not measured: re-run before reading the checks)" : "") +
                         (result.ControlsMissed > 0 ? " - a check that missed one is not ready to grade this model's replies" : ""));
        return result;
    }

    private static bool IsMeasured(CaseOutcome outcome) =>
        outcome.Graded && outcome.Target!.Score.CensusBucket() == MeasurementState.Measured;

    private static void PrintCase(TextWriter output, CaseOutcome outcome, bool control)
    {
        var scenario = outcome.Scenario;
        var canned = control ? " (canned control)" : "";
        output.WriteLine($"\n   {scenario.ScenarioId}: \"{scenario.Input}\"");
        if (!outcome.Graded)
        {
            output.WriteLine($"   ⚠️  Not graded{canned}: {outcome.Error}");
            if (control)
                output.WriteLine("   ⚠️  NOT MEASURED: this control says nothing about the checks.");
            return;
        }

        output.WriteLine(control
            ? $"   CANNED REPLY (gold: {outcome.Label.ExpectedVerdict}): {Clip(outcome.Response ?? "", 120)}"
            : $"   Agent: {Clip(outcome.Response ?? "", 140)}");
        if (outcome.ToolCalls is { Count: > 0 } calls)
            output.WriteLine($"   Tools called{canned}: {string.Join(", ", calls.Select(c => c.Name))}");
        foreach (var check in CheckKeys.All)
        {
            var score = outcome.Checks[check].Score;
            var (mark, note) = score.Label switch
            {
                "pass" => ("✓", ""),
                "inapplicable" => ("·", ""),
                "error" or "skipped" => ("?", " (no verdict)"),
                _ => ("✗", $" ({score.Severity})"),
            };
            output.WriteLine($"     {mark} {check,-22} {score.Label}{note}");
        }
        output.WriteLine($"   Pack verdict{canned}: {outcome.Result!.Score.Label.ToUpperInvariant()}");
        output.WriteLine($"   Why ({scenario.CheckId}): {Clip(outcome.Target!.Details.Summary ?? "", 160)}");
        // The case targets one check, but any check can decide the verdict: say why each one that did not pass.
        foreach (var check in CheckKeys.All.Where(k => k != scenario.CheckId))
        {
            var other = outcome.Checks[check];
            if (other.Score.Label is not ("pass" or "inapplicable"))
                output.WriteLine($"   Why ({check}): {Clip(other.Details.Summary ?? "", 160)}");
        }

        if (!control)
            return;
        if (!IsMeasured(outcome))
            output.WriteLine($"   ⚠️  NOT MEASURED: {scenario.CheckId} produced no verdict ('{outcome.Target.Score.Label}'); this control says nothing about it.");
        else if (outcome.Target.Score.Label == "fail")
            output.WriteLine("   ✓ Caught: the check failed the canned unsafe reply.");
        else
            output.WriteLine($"   ⚠️  MISSED: {scenario.CheckId} read '{outcome.Target.Score.Label}' on a reply written to fail it.");
    }

    private static string Clip(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
