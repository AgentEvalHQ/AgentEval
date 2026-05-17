// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Cli.Commands;

// ─── init ────────────────────────────────────────────────────────────────────
var nameOpt = new Option<string?>("--name") { Description = "Solution display name" };
var initCmd = new Command("init", "Initialize .agenteval/ in the current solution");
initCmd.Add(nameOpt);
initCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var name = parseResult.GetValue(nameOpt);
    return await InitCommand.RunAsync(name);
});

// ─── doctor ──────────────────────────────────────────────────────────────────
var doctorCmd = new Command("doctor", "Validate the .agenteval/ workspace structure and content hashes");
doctorCmd.SetAction(async (ParseResult _, CancellationToken ct) =>
    await DoctorCommand.RunAsync());

// ─── migrate ─────────────────────────────────────────────────────────────────
var applyOpt = new Option<bool>("--apply") { Description = "Apply migrations (default: dry-run)" };
var rootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var migrateCmd = new Command("migrate", "Migrate legacy AgentEval output paths to the canonical .agenteval/ layout");
migrateCmd.Add(applyOpt);
migrateCmd.Add(rootOpt);
migrateCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var apply = parseResult.GetValue(applyOpt);
    var root = parseResult.GetValue(rootOpt);
    return await MigrateCommand.RunAsync(apply, root);
});

// ─── bench ───────────────────────────────────────────────────────────────────
var benchCmd = new Command("bench", "Run a benchmark against an agent");

// bench gdpr — options with defaults handled in the action handler via ??
// Phase-7 Task 7.21: --subject required (breaking).
var benchPresetOpt = new Option<string?>("--preset") { Description = "Preset: smoke | standard | audit (default: standard). Domain-pack composition: standard+healthcare | standard+hr | standard+childrens (multi-pack composition like standard+healthcare+hr also supported)." };
var benchSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED — no default; previously defaulted to 'default-agent'." };
var benchRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchInputOpt = new Option<string?>("--input") { Description = "Agent input text for the evaluation (default: built-in fixture)" };
var benchRunsOpt = new Option<int?>("--runs") { Description = "Number of stochastic runs (default: 1). When > 1, runs the benchmark N times and aggregates via MajorityVote." };
var benchGdprCmd = new Command("gdpr", "Run the GDPR compliance benchmark");
benchGdprCmd.Add(benchPresetOpt);
benchGdprCmd.Add(benchSubjectOpt);
benchGdprCmd.Add(benchRootOpt);
benchGdprCmd.Add(benchInputOpt);
benchGdprCmd.Add(benchRunsOpt);
benchGdprCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchPresetOpt) ?? "standard";
    var subject = parseResult.GetValue(benchSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required. (Phase-7 7.21: the previous 'default-agent' default has been removed.)");
        return 1;
    }
    var root = parseResult.GetValue(benchRootOpt);
    var input = parseResult.GetValue(benchInputOpt);
    var runs = parseResult.GetValue(benchRunsOpt) ?? 1;
    return await BenchCommand.RunGdprAsync(preset, subject, root, input, runs: runs);
});

// bench gdpr calibrate
var calibrateRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: current directory)" };
var calibrateOutOpt = new Option<string?>("--out") { Description = "Output Markdown report path (default: strategy/FutureFeatures/calibration-baselines/gdpr-calibration-{date}.md)" };
var calibrateCmd = new Command("calibrate", "Run GDPR judge calibration against hand-labeled golden datasets");
calibrateCmd.Add(calibrateRootOpt);
calibrateCmd.Add(calibrateOutOpt);
calibrateCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var root = parseResult.GetValue(calibrateRootOpt);
    var outPath = parseResult.GetValue(calibrateOutOpt);
    return await BenchCalibrateCommand.RunAsync(root, outPath);
});
benchGdprCmd.Add(calibrateCmd);

benchCmd.Add(benchGdprCmd);

// bench eu-ai-act — same shape as bench gdpr, EU AI Act presets
// Phase-7 Tasks 7.21 + 7.22: --subject AND --input required (breaking).
var benchEuAiActPresetOpt = new Option<string?>("--preset") { Description = "Preset: smoke | standard | audit (default: standard). Domain-pack composition: standard+high-risk-employment | standard+high-risk-credit | standard+high-risk-education (multi-pack composition like standard+high-risk-employment+high-risk-credit also supported)." };
var benchEuAiActSubjectOpt = new Option<string?>("--subject") { Description = "Subject name. REQUIRED — no default; previously defaulted to 'default-agent'." };
var benchEuAiActRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchEuAiActInputOpt = new Option<string?>("--input") { Description = "Agent input text for the evaluation. REQUIRED — no default; previously a hard-coded fixture was used." };
var benchEuAiActCmd = new Command("eu-ai-act", "Run the EU AI Act compliance benchmark");
benchEuAiActCmd.Add(benchEuAiActPresetOpt);
benchEuAiActCmd.Add(benchEuAiActSubjectOpt);
benchEuAiActCmd.Add(benchEuAiActRootOpt);
benchEuAiActCmd.Add(benchEuAiActInputOpt);
benchEuAiActCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchEuAiActPresetOpt) ?? "standard";
    var subject = parseResult.GetValue(benchEuAiActSubjectOpt);
    var input = parseResult.GetValue(benchEuAiActInputOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required. (Phase-7 7.21: 'default-agent' default removed.)");
        return 1;
    }
    if (string.IsNullOrWhiteSpace(input))
    {
        Console.Error.WriteLine("Error: --input is required. (Phase-7 7.22: built-in fixture removed.)");
        return 1;
    }
    var root = parseResult.GetValue(benchEuAiActRootOpt);
    return await BenchEuAiActCommand.RunAsync(preset, subject, root, input);
});
// bench eu-ai-act calibrate
var euCalibrateRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: current directory)" };
var euCalibrateOutOpt = new Option<string?>("--out") { Description = "Output Markdown report path (default: strategy/FutureFeatures/calibration-baselines/eu-ai-act-calibration-{date}.md)" };
var euCalibrateCmd = new Command("calibrate", "Run EU AI Act judge calibration against hand-labeled golden datasets");
euCalibrateCmd.Add(euCalibrateRootOpt);
euCalibrateCmd.Add(euCalibrateOutOpt);
euCalibrateCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var root = parseResult.GetValue(euCalibrateRootOpt);
    var outPath = parseResult.GetValue(euCalibrateOutOpt);
    return await BenchEuAiActCalibrateCommand.RunAsync(root, outPath);
});
benchEuAiActCmd.Add(euCalibrateCmd);

benchCmd.Add(benchEuAiActCmd);

// bench agentic — same shape as bench eu-ai-act, agentic presets
var benchAgenticPresetOpt = new Option<string?>("--preset") { Description = "Preset: agentic-execution | tool-call-accuracy | rag-quality | judge-quality | safety | telemetry | stochastic-stability | conversational | reasoning | user-experience | adversarial-direct (default: agentic-execution). The judge-quality, telemetry, and stochastic-stability presets are pure-code (no LLM cost). The safety preset uses a default empty policy; supply custom policies programmatically." };
var benchAgenticSubjectOpt = new Option<string?>("--subject") { Description = "Subject name. REQUIRED — no default; previously defaulted to 'default-agent'." };
var benchAgenticRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchAgenticInputOpt = new Option<string?>("--input") { Description = "Agent input text for the evaluation (default: built-in fixture)" };
var benchAgenticBudgetTierOpt = new Option<string?>("--budget-tier") { Description = "Budget tier filter: trivial | low | medium | high | all (default: all). Components with a cost tier above the budget are filtered out and remaining weights are renormalized. Use 'low' or 'medium' for fast feedback loops; 'all' for full audit runs." };
var benchAgenticCmd = new Command("agentic", "Run the agentic behavior benchmark");
benchAgenticCmd.Add(benchAgenticPresetOpt);
benchAgenticCmd.Add(benchAgenticSubjectOpt);
benchAgenticCmd.Add(benchAgenticRootOpt);
benchAgenticCmd.Add(benchAgenticInputOpt);
benchAgenticCmd.Add(benchAgenticBudgetTierOpt);
benchAgenticCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchAgenticPresetOpt) ?? "agentic-execution";
    var subject = parseResult.GetValue(benchAgenticSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required. (Phase-7 7.21: 'default-agent' default removed.)");
        return 1;
    }
    var root = parseResult.GetValue(benchAgenticRootOpt);
    var input = parseResult.GetValue(benchAgenticInputOpt);
    var budgetTier = parseResult.GetValue(benchAgenticBudgetTierOpt);
    return await BenchAgenticCommand.RunAsync(preset, subject, root, input, budgetTier);
});
// bench agentic calibrate
var agenticCalibrateRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: current directory)" };
var agenticCalibrateOutOpt = new Option<string?>("--out") { Description = "Output Markdown report path (default: strategy/FutureFeatures/calibration-baselines/agentic-calibration-{date}.md)" };
var agenticCalibrateCmd = new Command("calibrate", "Run agentic judge calibration against hand-labeled golden datasets");
agenticCalibrateCmd.Add(agenticCalibrateRootOpt);
agenticCalibrateCmd.Add(agenticCalibrateOutOpt);
agenticCalibrateCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var root = parseResult.GetValue(agenticCalibrateRootOpt);
    var outPath = parseResult.GetValue(agenticCalibrateOutOpt);
    return await BenchAgenticCalibrateCommand.RunAsync(root, outPath);
});
benchAgenticCmd.Add(agenticCalibrateCmd);

benchCmd.Add(benchAgenticCmd);

// bench owasp — Phase 5 (v0.10.0-beta): OWASP LLM Top 10 red-team scan.
// Presets: top10 | smoke | audit | top10-rag.
var benchOwaspPresetOpt = new Option<string?>("--preset") { Description = "Preset: top10 | smoke | audit (=auditgrade) | top10-rag (=top10forrag). Default: top10. The smoke preset uses 3 attacks (PromptInjection + Jailbreak + PIILeakage); top10 / top10-rag / audit use all 9 attacks. audit runs at Comprehensive intensity for higher-confidence verdicts." };
var benchOwaspSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
var benchOwaspRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchOwaspInputOpt = new Option<string?>("--input") { Description = "Provenance text for the run (the OWASP attack pipeline generates its own probes; --input is recorded for traceability, not consumed by attacks)." };
var benchOwaspCmd = new Command("owasp", "Run the OWASP LLM Top 10 red-team benchmark");
benchOwaspCmd.Add(benchOwaspPresetOpt);
benchOwaspCmd.Add(benchOwaspSubjectOpt);
benchOwaspCmd.Add(benchOwaspRootOpt);
benchOwaspCmd.Add(benchOwaspInputOpt);
benchOwaspCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchOwaspPresetOpt) ?? "top10";
    var subject = parseResult.GetValue(benchOwaspSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required.");
        return 1;
    }
    var root = parseResult.GetValue(benchOwaspRootOpt);
    var input = parseResult.GetValue(benchOwaspInputOpt);
    return await BenchOwaspCommand.RunAsync(preset, subject, root, input);
});
benchCmd.Add(benchOwaspCmd);

// ─── compliance ───────────────────────────────────────────────────────────────
var complianceCmd = new Command("compliance", "Compliance reporting commands");

// compliance render — required values validated inside RunAsync
var renderRegulationOpt = new Option<string?>("--regulation") { Description = "Regulation identifier: gdpr | eu-ai-act" };
var renderSubjectOpt = new Option<string?>("--subject") { Description = "Subject name to render evidence for" };
var renderTsOpt = new Option<string?>("--ts") { Description = "Timestamp directory (yyyy-MM-dd_HH-mm-ss); defaults to most recent" };
var renderRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var renderCmd = new Command("render", "Render a PDF report from existing compliance evidence (no LLM cost)");
renderCmd.Add(renderRegulationOpt);
renderCmd.Add(renderSubjectOpt);
renderCmd.Add(renderTsOpt);
renderCmd.Add(renderRootOpt);
renderCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var regulation = parseResult.GetValue(renderRegulationOpt);
    var subject = parseResult.GetValue(renderSubjectOpt);
    if (string.IsNullOrWhiteSpace(regulation) || string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --regulation and --subject are required.");
        return 1;
    }
    var ts = parseResult.GetValue(renderTsOpt);
    var root = parseResult.GetValue(renderRootOpt);
    return await ComplianceRenderCommand.RunAsync(regulation, subject, ts, root);
});
complianceCmd.Add(renderCmd);

// ─── render ───────────────────────────────────────────────────────────────────
var renderBenchmarkOpt = new Option<string?>("--benchmark") { Description = "Benchmark type to render (currently: agentic)" };
var renderBenchSubjectOpt = new Option<string?>("--subject") { Description = "Subject name to render results for (required)" };
var renderBenchTsOpt = new Option<string?>("--ts") { Description = "Timestamp directory (yyyy-MM-dd_HH-mm-ss); defaults to most recent" };
var renderBenchRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var renderBenchCmd = new Command("render", "Render a Markdown report from existing agentic benchmark results (no LLM cost)");
renderBenchCmd.Add(renderBenchmarkOpt);
renderBenchCmd.Add(renderBenchSubjectOpt);
renderBenchCmd.Add(renderBenchTsOpt);
renderBenchCmd.Add(renderBenchRootOpt);
renderBenchCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var benchmark = parseResult.GetValue(renderBenchmarkOpt);
    var subject = parseResult.GetValue(renderBenchSubjectOpt);
    if (string.IsNullOrWhiteSpace(benchmark) || string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --benchmark and --subject are required.");
        return 1;
    }
    var ts = parseResult.GetValue(renderBenchTsOpt);
    var root = parseResult.GetValue(renderBenchRootOpt);
    return await RenderCommand.RunAsync(benchmark, subject, ts, root);
});

// ─── mc — Mission Control (plan-08 MC1.7.1) ──────────────────────────────────
var mcCmd = new Command("mc", "Mission Control web portal commands");

var mcServePortOpt = new Option<int?>("--port") { Description = "Port to bind (default: 5000)" };
var mcServeWorkspaceOpt = new Option<string?>("--workspace") { Description = "Workspace root (default: current directory). Mission Control reads {workspace}/.agenteval/" };
var mcServeCmd = new Command("serve", "Start the Mission Control web portal (GraphQL + REST + SPA on one port). Requires .NET 10.");
mcServeCmd.Add(mcServePortOpt);
mcServeCmd.Add(mcServeWorkspaceOpt);
mcServeCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var port = parseResult.GetValue(mcServePortOpt) ?? 5000;
    var workspace = parseResult.GetValue(mcServeWorkspaceOpt);
    return await McServeCommand.RunAsync(port, workspace);
});
mcCmd.Add(mcServeCmd);

// mc doctor — verifies the Mission Control bundle (DLL + SPA wwwroot/) is
// present and well-formed. Sibling to `agenteval doctor` (which validates
// the workspace data, not the portal binaries).
var mcDoctorCmd = new Command("doctor", "Verify Mission Control's runtime artefacts are co-located with the CLI and the SPA bundle is intact. Requires .NET 10.");
mcDoctorCmd.SetAction(async (ParseResult _, CancellationToken ct) =>
    await McDoctorCommand.RunAsync());
mcCmd.Add(mcDoctorCmd);

// ─── root ─────────────────────────────────────────────────────────────────────
var rootCmd = new RootCommand("AgentEval CLI — output-store lifecycle management");
rootCmd.Add(initCmd);
rootCmd.Add(doctorCmd);
rootCmd.Add(migrateCmd);
rootCmd.Add(benchCmd);
rootCmd.Add(complianceCmd);
rootCmd.Add(renderBenchCmd);
rootCmd.Add(mcCmd);

return await rootCmd.Parse(args).InvokeAsync();
