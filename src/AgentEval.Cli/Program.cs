// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Cli.Commands;

// ─── init (dataset scaffolder) ───────────────────────────────────────────────
// v1.1 consolidation: the canonical `init` is the dataset-scaffolding command
// ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha. The previous in-tree
// behaviour (initialise a .agenteval/ workspace) is preserved verbatim and
// exposed as `init-workspace` below — call sites that consumed
// AgentEval.Cli.Commands.InitCommand.RunAsync(...) still compile because we
// only renamed the command, not the .NET type.
var datasetInitCmd = AgentEval.Cli.Commands.Classic.DatasetInitCommand.Create();

// ─── init-workspace (formerly `init`) ────────────────────────────────────────
var nameOpt = new Option<string?>("--name") { Description = "Solution display name" };
var initWorkspaceCmd = new Command("init-workspace", "Initialize .agenteval/ in the current solution (workspace bootstrap; the dataset scaffolder is `agenteval init`)");
initWorkspaceCmd.Add(nameOpt);
initWorkspaceCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
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

// Resolves the agent response to grade for compliance benchmarks from --response / --response-file
// (BUG-18). Returns Error=true (after printing) if --response-file is given but missing.
static async Task<(bool Error, string? Text)> ResolveBenchResponseAsync(string? response, string? responseFile, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(response))
        return (false, response);
    if (!string.IsNullOrWhiteSpace(responseFile))
    {
        if (!File.Exists(responseFile))
        {
            Console.Error.WriteLine($"Error: --response-file not found: {responseFile}");
            return (true, null);
        }
        return (false, await File.ReadAllTextAsync(responseFile, ct));
    }
    return (false, null);
}

// ─── bench ───────────────────────────────────────────────────────────────────
var benchCmd = new Command("bench", "Run a benchmark against an agent");

// Phase 8 (v0.10.0-beta): anchor every benchmark-bearing assembly so module initializers
// have fired before any --help / --list surface inspects BenchmarkFamilyRegistry. The
// anchor is idempotent — same-content registrations are no-ops.
AgentEval.Cli.Commands.BenchListCommand.AnchorAssemblies();

// bench --list (Phase 8 / v0.10.0-beta): enumerates BenchmarkFamilyRegistry. NOT a hardcoded list.
var benchListOpt = new Option<bool>("--list") { Description = "List every registered benchmark family with its presets and cost tiers (sourced from BenchmarkFamilyRegistry, not a hardcoded list)." };
benchCmd.Add(benchListOpt);

// Helper for per-family --help: pull the family's preset list from BenchmarkFamilyRegistry
// so the help text is sourced from the registry rather than a hardcoded string.
static string PresetsHelpFromRegistry(string familyName)
{
    var family = AgentEval.Core.Benchmarks.BenchmarkFamilyRegistry.TryGet(familyName);
    if (family is null) return $"Preset (family '{familyName}' is not registered).";
    var lines = family.Presets.Select(p => $"  {p.Name} — {p.Description}");
    return $"Preset (sourced from BenchmarkFamilyRegistry):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
}

// bench gdpr — options with defaults handled in the action handler via ??
// Phase-7 Task 7.21: --subject required (breaking).
var benchPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("gdpr") + Environment.NewLine + "Default: standard. Domain-pack composition: standard+healthcare | standard+hr | standard+childrens (multi-pack composition like standard+healthcare+hr also supported)." };
var benchSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED — no default; previously defaulted to 'default-agent'." };
var benchRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchInputOpt = new Option<string?>("--input") { Description = "Agent input text for the evaluation (default: built-in fixture)" };
var benchResponseOpt = new Option<string?>("--response") { Description = "The agent's actual RESPONSE to grade. If omitted, a built-in fixture is graded and a warning is emitted (the evidence then reflects no real agent)." };
var benchResponseFileOpt = new Option<string?>("--response-file") { Description = "Path to a file containing the agent's actual response to grade (alternative to --response, for multi-line output)." };
var benchRunsOpt = new Option<int?>("--runs") { Description = "Number of stochastic runs (default: 1). When > 1, runs the benchmark N times and aggregates via MajorityVote." };
var benchGdprCmd = new Command("gdpr", "Run the GDPR compliance benchmark");
benchGdprCmd.Add(benchPresetOpt);
benchGdprCmd.Add(benchSubjectOpt);
benchGdprCmd.Add(benchRootOpt);
benchGdprCmd.Add(benchInputOpt);
benchGdprCmd.Add(benchResponseOpt);
benchGdprCmd.Add(benchResponseFileOpt);
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
    var response = await ResolveBenchResponseAsync(parseResult.GetValue(benchResponseOpt), parseResult.GetValue(benchResponseFileOpt), ct);
    if (response.Error) return 1;
    return await BenchCommand.RunGdprAsync(preset, subject, root, input, runs: runs, responseText: response.Text);
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
var benchEuAiActPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("eu-ai-act") + Environment.NewLine + "Default: standard. Domain-pack composition: standard+high-risk-employment | standard+high-risk-credit | standard+high-risk-education (multi-pack composition like standard+high-risk-employment+high-risk-credit also supported)." };
var benchEuAiActSubjectOpt = new Option<string?>("--subject") { Description = "Subject name. REQUIRED — no default; previously defaulted to 'default-agent'." };
var benchEuAiActRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchEuAiActInputOpt = new Option<string?>("--input") { Description = "Agent input text for the evaluation. REQUIRED — no default; previously a hard-coded fixture was used." };
var benchEuAiActResponseOpt = new Option<string?>("--response") { Description = "The agent's actual RESPONSE to grade. If omitted, a built-in fixture is graded and a warning is emitted (the evidence then reflects no real agent)." };
var benchEuAiActResponseFileOpt = new Option<string?>("--response-file") { Description = "Path to a file containing the agent's actual response to grade (alternative to --response)." };
var benchEuAiActCmd = new Command("eu-ai-act", "Run the EU AI Act compliance benchmark");
benchEuAiActCmd.Add(benchEuAiActPresetOpt);
benchEuAiActCmd.Add(benchEuAiActSubjectOpt);
benchEuAiActCmd.Add(benchEuAiActRootOpt);
benchEuAiActCmd.Add(benchEuAiActInputOpt);
benchEuAiActCmd.Add(benchEuAiActResponseOpt);
benchEuAiActCmd.Add(benchEuAiActResponseFileOpt);
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
    var response = await ResolveBenchResponseAsync(parseResult.GetValue(benchEuAiActResponseOpt), parseResult.GetValue(benchEuAiActResponseFileOpt), ct);
    if (response.Error) return 1;
    return await BenchEuAiActCommand.RunAsync(preset, subject, root, input, responseText: response.Text);
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
var benchAgenticPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("agentic") + Environment.NewLine + "Default: agentic-execution. judge-quality / telemetry / stochastic-stability are pure-code (no LLM cost); safety needs a programmatic policy resolver." };
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
var benchOwaspPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("owasp") + Environment.NewLine + "Default: top10. The smoke preset uses 3 attacks (PromptInjection + Jailbreak + PIILeakage); audit runs at Comprehensive intensity for higher-confidence verdicts." };
var benchOwaspSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
var benchOwaspRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchOwaspInputOpt = new Option<string?>("--input") { Description = "Provenance text for the run (the OWASP attack pipeline generates its own probes; --input is recorded for traceability, not consumed by attacks)." };
var benchOwaspAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Build an Azure OpenAI chat agent from AZURE_OPENAI_* env vars instead of scanning the built-in stub. Requires AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT." };
var benchOwaspCmd = new Command("owasp", "Run the OWASP LLM Top 10 red-team benchmark");
benchOwaspCmd.Add(benchOwaspPresetOpt);
benchOwaspCmd.Add(benchOwaspSubjectOpt);
benchOwaspCmd.Add(benchOwaspRootOpt);
benchOwaspCmd.Add(benchOwaspInputOpt);
benchOwaspCmd.Add(benchOwaspAzureFromEnvOpt);
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
    var azureFromEnv = parseResult.GetValue(benchOwaspAzureFromEnvOpt);
    return await BenchOwaspCommand.RunAsync(preset, subject, root, input, azureFromEnv);
});
benchCmd.Add(benchOwaspCmd);

// bench mitre — Phase 6 (v0.10.0-beta): MITRE ATLAS red-team scan.
// Presets: atlas-baseline | atlas-smoke | atlas-audit-grade.
var benchMitrePresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("mitre") + Environment.NewLine + "Default: atlas-baseline. The atlas-smoke preset uses 3 attacks (PromptInjection + Jailbreak + PIILeakage); atlas-audit-grade runs at Comprehensive intensity for higher-confidence verdicts." };
var benchMitreSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
var benchMitreRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchMitreInputOpt = new Option<string?>("--input") { Description = "Provenance text for the run (the MITRE ATLAS attack pipeline generates its own probes; --input is recorded for traceability, not consumed by attacks)." };
var benchMitreAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Build an Azure OpenAI chat agent from AZURE_OPENAI_* env vars instead of scanning the built-in stub. Requires AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT." };
var benchMitreCmd = new Command("mitre", "Run the MITRE ATLAS red-team benchmark");
benchMitreCmd.Add(benchMitrePresetOpt);
benchMitreCmd.Add(benchMitreSubjectOpt);
benchMitreCmd.Add(benchMitreRootOpt);
benchMitreCmd.Add(benchMitreInputOpt);
benchMitreCmd.Add(benchMitreAzureFromEnvOpt);
benchMitreCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchMitrePresetOpt) ?? "atlas-baseline";
    var subject = parseResult.GetValue(benchMitreSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required.");
        return 1;
    }
    var root = parseResult.GetValue(benchMitreRootOpt);
    var input = parseResult.GetValue(benchMitreInputOpt);
    var azureFromEnv = parseResult.GetValue(benchMitreAzureFromEnvOpt);
    return await BenchMitreCommand.RunAsync(preset, subject, root, input, azureFromEnv);
});
benchCmd.Add(benchMitreCmd);

// bench perf — Phase 8 (v0.10.0-beta): Performance benchmark CLI surface (previously CLI-less).
// Sub-commands resolve the "perf" family from BenchmarkFamilyRegistry and dispatch to its
// Convention-2 EvaluateAsync adapter.
{
    var benchPerfSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
    var benchPerfPromptOpt = new Option<string?>("--prompt") { Description = "Prompt to measure against (default: 'Hello!')." };
    var benchPerfRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
    var benchPerfAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Measure a real Azure OpenAI chat agent built from AZURE_OPENAI_* env vars instead of the built-in EchoAgent stub. Requires AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT." };

    // bench longmemeval — T0.6 (v1.1): closes CLI ↔ registry gap.
    {
        var benchLmePresetOpt = new Option<string?>("--preset") { Description = "subset | full. Default: subset. 'full' requires LONGMEMEVAL_DATASET_PATH." };
        var benchLmeSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent under evaluation). REQUIRED." };
        var benchLmeRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };

        var benchLmeCmd = new Command("longmemeval", "Run the LongMemEval ICLR 2025 memory benchmark. Reads AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT — there is no stub fallback (the runner makes ~2 LLM calls per question; the LLM round-trip IS the correctness signal).");
        benchLmeCmd.Add(benchLmePresetOpt);
        benchLmeCmd.Add(benchLmeSubjectOpt);
        benchLmeCmd.Add(benchLmeRootOpt);
        benchLmeCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var preset = parseResult.GetValue(benchLmePresetOpt) ?? "subset";
            var subject = parseResult.GetValue(benchLmeSubjectOpt);
            if (string.IsNullOrWhiteSpace(subject))
            {
                Console.Error.WriteLine("Error: --subject is required.");
                return 1;
            }
            var root = parseResult.GetValue(benchLmeRootOpt);
            return await BenchLongMemEvalCommand.RunAsync(preset, subject, root);
        });
        benchCmd.Add(benchLmeCmd);
    }

    // bench memory — T0.6 (v1.1): closes CLI ↔ registry gap.
    {
        var benchMemPresetOpt = new Option<string?>("--preset") { Description = "quick | standard | full | diagnostic | overflow. Default: quick." };
        var benchMemSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent under evaluation). REQUIRED." };
        var benchMemRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };

        var benchMemCmd = new Command("memory", "Run the AgentEval memory benchmark. Reads AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT — there is no stub fallback (the benchmark needs a real LLM-backed agent under test).");
        benchMemCmd.Add(benchMemPresetOpt);
        benchMemCmd.Add(benchMemSubjectOpt);
        benchMemCmd.Add(benchMemRootOpt);
        benchMemCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var preset = parseResult.GetValue(benchMemPresetOpt) ?? "quick";
            var subject = parseResult.GetValue(benchMemSubjectOpt);
            if (string.IsNullOrWhiteSpace(subject))
            {
                Console.Error.WriteLine("Error: --subject is required.");
                return 1;
            }
            var root = parseResult.GetValue(benchMemRootOpt);
            return await BenchMemoryCommand.RunAsync(preset, subject, root);
        });
        benchCmd.Add(benchMemCmd);
    }

    var benchPerfCmd = new Command("perf", "Run a performance benchmark (latency, throughput, cost)");

    foreach (var presetName in new[] { "latency", "throughput", "cost" })
    {
        var presetCmd = new Command(presetName, $"Run the perf {presetName} preset");
        presetCmd.Add(benchPerfSubjectOpt);
        presetCmd.Add(benchPerfPromptOpt);
        presetCmd.Add(benchPerfRootOpt);
        presetCmd.Add(benchPerfAzureFromEnvOpt);
        var capturedPreset = presetName;
        presetCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var subject = parseResult.GetValue(benchPerfSubjectOpt);
            if (string.IsNullOrWhiteSpace(subject))
            {
                Console.Error.WriteLine("Error: --subject is required.");
                return 1;
            }
            var prompt = parseResult.GetValue(benchPerfPromptOpt);
            var root = parseResult.GetValue(benchPerfRootOpt);
            var azureFromEnv = parseResult.GetValue(benchPerfAzureFromEnvOpt);
            return await BenchPerfCommand.RunAsync(capturedPreset, subject, prompt, root, azureFromEnv);
        });
        benchPerfCmd.Add(presetCmd);
    }
    benchCmd.Add(benchPerfCmd);
}

// bench --list handler attaches to the bench root via SetAction so that
// `agenteval bench --list` (without a sub-command) prints the registry listing.
benchCmd.SetAction((ParseResult parseResult, CancellationToken ct) =>
{
    if (parseResult.GetValue(benchListOpt))
    {
        return Task.FromResult(BenchListCommand.Run());
    }
    Console.Error.WriteLine("Usage: agenteval bench {family} [--preset NAME] ... | agenteval bench --list");
    return Task.FromResult(1);
});

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
var rootCmd = new RootCommand("AgentEval CLI — evaluate AI agents, run benchmark suites, manage the .agenteval/ output store, and serve Mission Control.");

// Legacy command surface ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha
// (documentation and CI pipelines depend on these exact names and flags):
rootCmd.Add(datasetInitCmd);                  // init — scaffold an evaluation dataset
rootCmd.Add(EvalCommand.Create());            // eval — run an agent against a dataset
rootCmd.Add(ListCommand.Create());            // list — catalogues of metrics/attacks/exporters/datasets
rootCmd.Add(RedTeamCommand.Create());         // redteam — low-level red-team scanner

// v0.10+ command surface (output store, benchmark families, Mission Control):
rootCmd.Add(initWorkspaceCmd);
rootCmd.Add(doctorCmd);
rootCmd.Add(migrateCmd);
rootCmd.Add(benchCmd);
rootCmd.Add(complianceCmd);
rootCmd.Add(renderBenchCmd);
rootCmd.Add(mcCmd);

return await rootCmd.Parse(args).InvokeAsync();
