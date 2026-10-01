// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// AgentEval.HealthcareSafetyPack — runner for the synthetic, fully offline healthcare-safety
// domain pack. It grades the AGENT'S HANDLING of 15 synthetic scenarios with five deterministic
// checks composed into one MinAggregation composite, reports agreement against an author-labelled
// gold set, and writes a per-check audit trail. No API keys, no network, no model calls.

using System.Globalization;
using System.Text.Json;
using AgentEval.Calibration;
using AgentEval.Evals;
using AgentEval.HealthcareSafetyPack;

// Stable numeric formatting regardless of host locale, so the artifact is reproducible.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var jsonl = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var dataDir = HealthcareSafetyData.ResolveDataDirectory();
var scenarios = await HealthcareSafetyData.LoadJsonlAsync<HealthcareScenario>(
    Path.Combine(dataDir, "scenarios.jsonl"));
var gold = await HealthcareSafetyData.LoadJsonlAsync<GoldLabel>(
    Path.Combine(dataDir, "gold.jsonl"));

// ── Cross-check: the runner grades the SAME fixtures the gold labels refer to. ──
var scenarioById = scenarios.ToDictionary(s => s.ScenarioId, StringComparer.Ordinal);
if (scenarios.Count != gold.Count)
    throw new InvalidOperationException(
        $"scenario/gold count mismatch: {scenarios.Count} scenarios vs {gold.Count} labels.");
foreach (var label in gold)
{
    if (!scenarioById.TryGetValue(label.ScenarioId, out var scenario))
        throw new InvalidOperationException($"gold label '{label.ScenarioId}' has no matching scenario.");
    if (!string.Equals(scenario.Input, label.Input, StringComparison.Ordinal) ||
        !string.Equals(scenario.AgentResponse, label.AgentResponse, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"fixture drift on '{label.ScenarioId}': scenario and gold input/response differ.");
}

// ── Banner + explicit non-claims. ──
Console.WriteLine("AgentEval.HealthcareSafetyPack - synthetic healthcare-safety domain pack");
Console.WriteLine(new string('=', 78));
Console.WriteLine("Five checks that grade the AGENT'S HANDLING of a synthetic clinical request:");
Console.WriteLine("  hc_escalation        urgent symptoms routed to care, not reassured away");
Console.WriteLine("  hc_action_boundary   no scheduling/ordering/prescribing/diagnosis unless permitted");
Console.WriteLine("  hc_medication_safety no dose-specific advice when key context is missing");
Console.WriteLine("  hc_source_support    cited guidance versus an unsupported assertion");
Console.WriteLine("  hc_audit_trail       the result records why it blocked / routed / allowed");
Console.WriteLine();
Console.WriteLine("SCOPE: synthetic scenarios only, no patient data. Fully offline (no API keys).");
Console.WriteLine("NON-CLAIMS: this is NOT clinical validation, NOT a medical device, and NOT");
Console.WriteLine("deployment readiness. The judges are offline keyword/regex heuristics, uncalibrated");
Console.WriteLine("beyond this sample's small author-labelled gold set.");
Console.WriteLine();

// ── The pack + its five checks. ──
var pack = HealthcareSafetyPackFactory.Build();
string[] checkOrder =
[
    EscalationCheck.CheckKey,
    ActionBoundaryCheck.CheckKey,
    MedicationSafetyCheck.CheckKey,
    SourceSupportCheck.CheckKey,
    AuditTrailCheck.CheckKey,
];

var packPairs = new List<(string Expected, string Actual)>();
var perCheckPairs = checkOrder.ToDictionary(k => k, _ => new List<(string Expected, string Actual)>(), StringComparer.Ordinal);
var auditRows = new List<object>();

Console.WriteLine($"Per-scenario verdicts (pack '{pack.Key}', aggregation {pack.Aggregation.Name}, threshold none)");
Console.WriteLine(new string('-', 78));
foreach (var label in gold)
{
    var scenario = scenarioById[label.ScenarioId];
    var result = await pack.EvaluateAsync(BuildInput(scenario));
    var subs = result.Details.SubResults!.ToDictionary(s => s.Metric.Key, StringComparer.Ordinal);
    var target = subs[scenario.CheckId];

    packPairs.Add((label.ExpectedVerdict, result.Score.Label));
    perCheckPairs[scenario.CheckId].Add((label.ExpectedVerdict, target.Score.Label));

    Console.WriteLine(
        $"{scenario.ScenarioId,-12} {scenario.CheckId,-24} expected={label.ExpectedVerdict,-4} " +
        $"pack={result.Score.Label,-4} score={result.Score.Value:0.000} ({result.Score.Severity})");
    Console.WriteLine(
        "   checks: " + string.Join("  ", checkOrder.Select(k =>
            $"{Abbrev(k)}={Short(subs[k].Score.Label)}/{subs[k].Score.Severity}")));
    Console.WriteLine($"   target: {target.Details.Summary ?? "(no reason)"}");

    auditRows.Add(new
    {
        scenarioId = scenario.ScenarioId,
        checkId = scenario.CheckId,
        expectedVerdict = label.ExpectedVerdict,
        packVerdict = result.Score.Label,
        packSeverity = result.Score.Severity,
        packScore = result.Score.Value,
        targetReason = target.Details.Summary,
        checks = subs.ToDictionary(
            kv => kv.Key,
            kv => new
            {
                verdict = kv.Value.Score.Label,
                severity = kv.Value.Score.Severity,
                score = kv.Value.Score.Value,
                reason = kv.Value.Details.Summary,
            }),
    });
}
Console.WriteLine();

// ── Agreement: pack + per check, via the shared single-sourced metrics. ──
Console.WriteLine("Agreement vs author-labelled gold (accuracy + Cohen's kappa) - UNCALIBRATED");
Console.WriteLine(new string('-', 78));
Console.WriteLine($"{"scope",-26} {"n",3} {"accuracy",9} {"kappa",7}");
PrintRow("pack (Min, 5 checks)", packPairs);
foreach (var key in checkOrder)
    PrintRow(key, perCheckPairs[key]);
Console.WriteLine();
Console.WriteLine("Read agreement as a consistency smoke signal, not validated accuracy:");
Console.WriteLine("the labels and the heuristics were authored together on a tiny corpus.");
Console.WriteLine();

// ── Audit artifact: per-check verdict + reason for every scenario. ──
var auditPath = Path.Combine(Path.GetTempPath(), "healthcare-safety-audit.jsonl");
await File.WriteAllLinesAsync(auditPath, auditRows.Select(r => JsonSerializer.Serialize(r, jsonl)));
Console.WriteLine($"Wrote {auditPath}");
Console.WriteLine();
Console.WriteLine("Result: VERIFICATION ARTIFACT ONLY. Synthetic cases, offline heuristics, no clinical claim.");

return 0;

static EvalInput BuildInput(HealthcareScenario scenario)
{
    var toolCalls = scenario.ToolCalls is null
        ? null
        : scenario.ToolCalls.Select(tc => new ToolCall(
            tc.Name,
            tc.Arguments?.ToDictionary(k => k.Key, v => (object)v.Value),
            tc.Result)).ToList();

    var metadata = new Dictionary<string, object>
    {
        ["urgent"] = scenario.Urgent,
        ["medicationCase"] = scenario.MedicationCase,
        ["doseContextComplete"] = scenario.DoseContextComplete,
        ["clinicalClaimCase"] = scenario.ClinicalClaimCase,
        ["permittedActions"] = scenario.PermittedActions ?? Array.Empty<string>(),
    };

    return new EvalInput(
        Query: scenario.Input,
        Response: scenario.AgentResponse,
        ToolCalls: toolCalls,
        Metadata: metadata);
}

static void PrintRow(string scope, IReadOnlyList<(string Expected, string Actual)> pairs)
{
    var accuracy = AgreementMetrics.Accuracy(pairs);
    var kappa = AgreementMetrics.CohensKappa(pairs);
    Console.WriteLine($"{scope,-26} {pairs.Count,3} {accuracy,9:0.00} {Fmt(kappa),7}");
}

static string Fmt(double value) => double.IsNaN(value) ? "n/a" : value.ToString("0.00");

static string Abbrev(string key) => key switch
{
    EscalationCheck.CheckKey => "esc",
    ActionBoundaryCheck.CheckKey => "act",
    MedicationSafetyCheck.CheckKey => "med",
    SourceSupportCheck.CheckKey => "src",
    AuditTrailCheck.CheckKey => "aud",
    _ => key[..Math.Min(3, key.Length)]
};

static string Short(string label) => label switch
{
    "inapplicable" => "n/a",
    "pass" => "P",
    "fail" => "F",
    _ => label
};
