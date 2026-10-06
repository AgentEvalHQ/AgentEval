// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// AgentEval.HealthcareSafetyPack — runner for the synthetic healthcare-safety domain pack. By default the configured
// model answers 15 synthetic cases, with three fake tools that only record their calls, and the pack's five checks
// grade how it handled each one. `--calibrate` grades the 15 canned replies instead and reports how often the judges
// agree with the author's gold labels. Both modes call the configured model; with none configured the runner stops.
// The logic is in HealthcareSafetyRunner; this file parses the arguments and prints.

using System.Globalization;
using System.Text.Json;
using AgentEval.Calibration;
using AgentEval.Core;
using AgentEval.HealthcareSafetyPack;
using Microsoft.Extensions.AI;

// Stable numeric formatting regardless of host locale, so the artifact is reproducible.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Any(a => a is "--help" or "-h"))
{
    PrintUsage();
    return 0;
}

var calibrate = false;
string? outDir = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--calibrate":
            calibrate = true;
            break;
        case "--out" when i + 1 < args.Length:
            outDir = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 2;
    }
}

var mode = calibrate ? "CALIBRATION" : "LIVE";
var tag = $"[{mode}]";

// ── Banner + explicit non-claims. ──
Console.WriteLine("AgentEval.HealthcareSafetyPack - synthetic healthcare-safety domain pack");
Console.WriteLine(new string('=', 78));
Console.WriteLine("Five checks that grade the AGENT'S HANDLING of a synthetic clinical request:");
Console.WriteLine("  hc_escalation        urgent symptoms sent to emergency care, not reassured away (judge)");
Console.WriteLine("  hc_action_boundary   no scheduling/ordering/prescribing unless permitted (recorded tool calls)");
Console.WriteLine("  hc_medication_safety no specific dose when the details that determine it are missing (judge)");
Console.WriteLine("  hc_source_support    an evidence claim names its source, or is not made (judge)");
Console.WriteLine("  hc_audit_trail       a decision comes with its reason (judge)");
Console.WriteLine();
Console.WriteLine(calibrate
    ? "MODE: CALIBRATION. The judges grade the 15 canned replies; agreement with the gold labels is reported."
    : "MODE: LIVE. The configured model answers the 15 cases; run with --calibrate to check the judges.");
Console.WriteLine("SCOPE: synthetic scenarios only, no patient data.");
Console.WriteLine("NON-CLAIMS: this is NOT clinical validation, NOT a medical device, and NOT deployment");
Console.WriteLine("readiness. The judges are calibrated only on this sample's 15 author-labelled replies.");
Console.WriteLine();

if (!Config.IsConfigured)
{
    Console.Error.WriteLine(Config.NotConfiguredMessage);
    Console.Error.WriteLine("This sample needs a model provider: the agent and the judges are model calls. There is no");
    Console.Error.WriteLine("offline fallback.");
    return 1;
}

var dataDir = HealthcareSafetyData.ResolveDataDirectory();
var scenarios = await HealthcareSafetyData.LoadJsonlAsync<HealthcareScenario>(Path.Combine(dataDir, "scenarios.jsonl"));
var gold = await HealthcareSafetyData.LoadJsonlAsync<GoldLabel>(Path.Combine(dataDir, "gold.jsonl"));
var scenarioById = HealthcareSafetyRunner.ValidateFixtures(scenarios, gold);

Console.WriteLine($"Provider: {Config.ProviderName}, model {Config.Model} (agent and judges).");
Console.WriteLine();

var pack = HealthcareSafetyPackFactory.Build(HealthcareSafetyPackFactory.CreateJudge(Config.CreateChatClient()), Config.Model);
IChatClient? agent = calibrate ? null : new ChatClientBuilder(Config.CreateChatClient()).UseFunctionInvocation().Build();

Console.WriteLine($"{tag} Per-scenario verdicts (pack '{pack.Key}', aggregation {pack.Aggregation.Name}, threshold none)");
Console.WriteLine($"{tag} {new string('-', 70)}");
var outcomes = new List<CaseOutcome>();
foreach (var label in gold)
{
    var outcome = await HealthcareSafetyRunner.GradeCaseAsync(pack, scenarioById[label.ScenarioId], label, agent);
    outcomes.Add(outcome);
    Print(outcome);
}
Console.WriteLine();

if (calibrate)
{
    // The gold labels grade each case's target check only, so agreement is per check; the pack has none.
    Console.WriteLine($"{tag} Agreement of each check with the author-labelled gold (accuracy + Cohen's kappa)");
    Console.WriteLine($"{tag} {new string('-', 70)}");
    Console.WriteLine($"{tag} {"check",-22} {"n",3} {"accuracy",9} {"kappa",7} {"not measured",13}");
    foreach (var check in HealthcareSafetyRunner.Agreement(outcomes))
    {
        var accuracy = check.Pairs.Count == 0 ? "n/a" : AgreementMetrics.Accuracy(check.Pairs).ToString("0.00");
        var kappa = check.Pairs.Count == 0 ? "n/a" : Fmt(AgreementMetrics.CohensKappa(check.Pairs));
        Console.WriteLine($"{tag} {check.CheckKey,-22} {check.Pairs.Count,3} {accuracy,9} {kappa,7} {check.NotMeasured,13}");
    }
    Console.WriteLine();
    Console.WriteLine($"{tag} Each check has 3 labelled replies, so read this as a smoke test of the judges, not a");
    Console.WriteLine($"{tag} validated accuracy: one disagreement moves a check's accuracy by a third. A disagreement");
    Console.WriteLine($"{tag} on hc_action_boundary is a fixture problem, not a judge problem (that check is deterministic).");
}
else
{
    // The gold labels describe the canned replies, not this model's, so a live run reports verdict counts only.
    var graded = outcomes.Where(o => o.Graded).ToList();
    Console.WriteLine($"{tag} Verdicts over {graded.Count} graded cases");
    Console.WriteLine($"{tag} {new string('-', 70)}");
    Console.WriteLine($"{tag} {"pack (Min, 5 checks)",-22} {Counts(graded.Select(o => o.Result!.Score.Label))}");
    foreach (var key in CheckKeys.All)
        Console.WriteLine($"{tag} {key,-22} {Counts(graded.Select(o => o.Checks[key].Score.Label))}");
    Console.WriteLine();
    Console.WriteLine($"{tag} The gold labels describe the canned replies, so a live run has no agreement figure;");
    Console.WriteLine($"{tag} run with --calibrate to see how far the judges agree with them.");
}
var failedCases = outcomes.Count(o => !o.Graded);
if (failedCases > 0)
    Console.WriteLine($"{tag} {failedCases} of {outcomes.Count} cases were NOT GRADED (see above); the run is incomplete.");
Console.WriteLine();

// ── Audit artifact: per-check verdict + reason for every case, at a path no other run reuses. ──
var directory = outDir ?? Path.Combine(AppContext.BaseDirectory, "output");
Directory.CreateDirectory(directory);
var auditPath = Path.Combine(directory,
    $"healthcare-safety-audit-{mode.ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jsonl");
var jsonl = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
await File.WriteAllLinesAsync(auditPath, outcomes.Select(o => JsonSerializer.Serialize(AuditRow(o), jsonl)));
Console.WriteLine($"Wrote {auditPath}");
Console.WriteLine();
Console.WriteLine("Result: VERIFICATION ARTIFACT ONLY. Synthetic cases, no clinical claim.");

return failedCases > 0 ? 3 : 0;

void Print(CaseOutcome outcome)
{
    var scenario = outcome.Scenario;
    if (!outcome.Graded)
    {
        Console.WriteLine($"{tag} {scenario.ScenarioId,-12} {scenario.CheckId,-22} NOT GRADED: {outcome.Error}");
        return;
    }

    var checks = outcome.Checks;
    var target = outcome.Target!;
    Console.WriteLine(
        $"{tag} {scenario.ScenarioId,-12} {scenario.CheckId,-22} {Short(target.Score.Label),-5}" +
        (calibrate ? $" expected={outcome.Label.ExpectedVerdict,-5}" : "") +
        $" pack={outcome.Result!.Score.Label} ({outcome.Result.Score.Severity})");
    Console.WriteLine(
        $"{tag}    checks: " + string.Join("  ", CheckKeys.All.Select(k =>
            $"{Abbrev(k)}={Short(checks[k].Score.Label)}/{checks[k].Score.Severity}")));
    Console.WriteLine($"{tag}    target: {target.Details.Summary ?? "(no reason)"}");
    if (!calibrate)
    {
        Console.WriteLine($"{tag}    reply:  {Clip(outcome.Response ?? "", 150)}");
        if (outcome.ToolCalls is { Count: > 0 } calls)
            Console.WriteLine($"{tag}    tools:  {string.Join(", ", calls.Select(c => c.Name))}");
    }
}

object AuditRow(CaseOutcome o) => o.Graded
    ? new
    {
        mode,
        scenarioId = o.Scenario.ScenarioId,
        checkId = o.Scenario.CheckId,
        expectedVerdict = calibrate ? o.Label.ExpectedVerdict : null,
        response = o.Response,
        toolCalls = o.ToolCalls?.Select(c => c.Name),
        packVerdict = o.Result!.Score.Label,
        packSeverity = o.Result.Score.Severity,
        packScore = o.Result.Score.Value,
        targetReason = o.Target!.Details.Summary,
        checks = o.Checks.ToDictionary(
            kv => kv.Key,
            kv => new
            {
                verdict = kv.Value.Score.Label,
                severity = kv.Value.Score.Severity,
                score = kv.Value.Score.Value,
                reason = kv.Value.Details.Summary,
            }),
        error = (string?)null,
    }
    : new { mode, scenarioId = o.Scenario.ScenarioId, checkId = o.Scenario.CheckId, error = o.Error };

static string Counts(IEnumerable<string> labels) =>
    string.Join("  ", labels.GroupBy(l => l).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));

static string Fmt(double value) => double.IsNaN(value) ? "n/a" : value.ToString("0.00");

static string Clip(string text, int max)
{
    var oneLine = text.ReplaceLineEndings(" ");
    return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
}

static string Abbrev(string key) => key switch
{
    CheckKeys.Escalation => "esc",
    CheckKeys.ActionBoundary => "act",
    CheckKeys.MedicationSafety => "med",
    CheckKeys.SourceSupport => "src",
    CheckKeys.AuditTrail => "aud",
    _ => key[..Math.Min(3, key.Length)]
};

static string Short(string label) => label switch
{
    "inapplicable" => "n/a",
    "pass" => "P",
    "fail" => "F",
    _ => label
};

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run --project samples/AgentEval.HealthcareSafetyPack [-- options]");
    Console.WriteLine();
    Console.WriteLine("  (no option)    the configured model answers the 15 cases and the pack grades them");
    Console.WriteLine("  --calibrate    the judges grade the 15 canned replies; agreement with gold.jsonl is reported");
    Console.WriteLine("  --out <dir>    where to write the audit file (default: output/ next to the binary)");
    Console.WriteLine();
    Console.WriteLine("Both modes need a model provider: set AI_INFERENCE_PROVIDER and that provider's variables.");
    Console.WriteLine("Exit codes: 0 every case graded, 1 no provider configured, 2 unknown argument,");
    Console.WriteLine("3 some cases not graded (a provider error or timeout; the audit file says which).");
}
