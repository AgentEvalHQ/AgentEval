// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Cli.Commands;
using AgentEval.Memory.External.TypedMemEval;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Infrastructure;

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
static Task<(bool Error, string? Text)> ResolveBenchResponseAsync(string? response, string? responseFile, CancellationToken ct) =>
    ResolveTextOrFileAsync(response, responseFile, "--response-file", ct);

static async Task<(bool Error, string? Text)> ResolveTextOrFileAsync(string? text, string? file, string fileOption, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(text))
        return (false, text);
    if (!string.IsNullOrWhiteSpace(file))
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"Error: {fileOption} not found: {file}");
            return (true, null);
        }
        return (false, await File.ReadAllTextAsync(file, ct));
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
var benchInputOpt = new Option<string?>("--input") { Description = "The question the agent answered. Required with --response/--response-file; with --sut or --azure-from-env each scenario sends its own prompt." };
var benchResponseOpt = new Option<string?>("--response") { Description = "The agent's actual RESPONSE to grade (needs --input). Without it, --sut or --azure-from-env must name a live agent; --sut mock grades a canned answer that measures nothing and is not stored." };
var benchResponseFileOpt = new Option<string?>("--response-file") { Description = "Path to a file containing the agent's actual response to grade (alternative to --response, for multi-line output)." };
var benchRunsOpt = new Option<int?>("--runs") { Description = "Number of stochastic runs (default: 1). When > 1, runs the benchmark N times and aggregates via MajorityVote." };
var benchGdprAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Drive a live agent from the environment (the provider AI_INFERENCE_PROVIDER selects) per scenario: each scenario's own prompt is sent to the agent and its real answer is graded, instead of grading a single --response. The judge resolves AZURE_OPENAI_JUDGE_* first, then the selected provider, so agent and judge can target different endpoints." };
var benchGdprCmd = new Command("gdpr", "Run the GDPR compliance benchmark. Needs a target: --sut or --azure-from-env drives a live agent per scenario, or --response/--response-file grades an answer the agent gave (with its --input). Without one it refuses; --sut mock grades a canned answer that measures nothing and is not stored.");
benchGdprCmd.Add(benchPresetOpt);
benchGdprCmd.Add(benchSubjectOpt);
benchGdprCmd.Add(benchRootOpt);
benchGdprCmd.Add(benchInputOpt);
benchGdprCmd.Add(benchResponseOpt);
benchGdprCmd.Add(benchResponseFileOpt);
benchGdprCmd.Add(benchRunsOpt);
benchGdprCmd.Add(benchGdprAzureFromEnvOpt);
var (benchGdprSutOpt, benchGdprSutTargets) = SutTargetResolver.AddOptionsTo(benchGdprCmd, "bench");
benchGdprCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchPresetOpt) ?? "standard";
    var subject = parseResult.GetValue(benchSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required. (There is no default subject.)");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    var root = parseResult.GetValue(benchRootOpt);
    var input = parseResult.GetValue(benchInputOpt);
    var runs = parseResult.GetValue(benchRunsOpt) ?? 1;
    var azureFromEnv = parseResult.GetValue(benchGdprAzureFromEnvOpt);

    // Bench Tier 2 (§2.2): reuses BenchTier1SutResolver.Resolve verbatim — its logic is verb/tier-agnostic,
    // nothing Tier-1-specific about it. Only --sut is exposed here (endpoint/model/apiKey null) — a generic
    // OpenAI-compatible endpoint for bench gdpr/eu-ai-act was not requested; --sut copilot-studio is.
    var (mock, sut, mockError) = MockTarget.Parse(
        parseResult.GetValue(benchGdprSutOpt),
        anotherTargetNamed: azureFromEnv || parseResult.GetValue(benchResponseOpt) is not null || parseResult.GetValue(benchResponseFileOpt) is not null);
    if (mockError is not null)
    {
        Console.Error.WriteLine($"Error: {mockError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (agentOverride, sutError) = BenchTier1SutResolver.Resolve(
        sut,
        benchGdprSutTargets.ToDictionary(t => t.Sut, t => t.BindOptions(parseResult), StringComparer.OrdinalIgnoreCase),
        benchGdprSutTargets,
        endpoint: null,
        model: null,
        apiKey: null,
        subject);
    if (sutError is not null)
    {
        Console.Error.WriteLine($"Error: {sutError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var response = await ResolveBenchResponseAsync(parseResult.GetValue(benchResponseOpt), parseResult.GetValue(benchResponseFileOpt), ct);
    if (response.Error) return AgentEval.Cli.ExitCodes.UsageError;
    return await BenchCommand.RunGdprAsync(preset, subject, root, input, evaluatorOverride: null, agentOverride: agentOverride, runs: runs, responseText: response.Text, azureFromEnv: azureFromEnv, mock: mock, ct: ct);
});

// bench gdpr calibrate
var calibrateRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: current directory)" };
var calibrateOutOpt = new Option<string?>("--out") { Description = "Output Markdown report path (default: .agenteval/calibration/gdpr-calibration-{date}.md)" };
var calibrateDecisionsOpt = new Option<bool>("--decisions") { Description = "Grade with the decision model (TypeSafe Jev) instead of the generative judge, for a judge-vs-judge calibration. Reads TYPESAFE_API_KEY (or OPENROUTER_API_KEY); JEV_MODEL pins a build. ADR-033: the adapter is for calibration only — nothing it produces is persisted as an eval tree." };
var calibrateCmd = new Command("calibrate", "Run GDPR judge calibration against hand-labeled golden datasets");
calibrateCmd.Add(calibrateRootOpt);
calibrateCmd.Add(calibrateOutOpt);
calibrateCmd.Add(calibrateDecisionsOpt);
var calibrateLimitOpt = new Option<int?>("--limit") { Description = "Evaluate at most N entries per pillar (the one-item stage before a full paid run; requires --out)" };
calibrateCmd.Add(calibrateLimitOpt);
calibrateCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var root = parseResult.GetValue(calibrateRootOpt);
    var outPath = parseResult.GetValue(calibrateOutOpt);
    // With --decisions the report names the decision model's provider and requested model; without the identity it
    // said "unknown" for both, because the judge arrives as a caller-supplied evaluator.
    return await DecisionCalibration.RunAsync(
        parseResult.GetValue(calibrateDecisionsOpt),
        (decisionJudge, decisionJudgeIdentity) => BenchCalibrateCommand.RunCoreAsync(
            root, outPath, evaluatorOverride: decisionJudge, ct: ct, evaluatorOverrideIdentity: decisionJudgeIdentity,
            limitPerPillar: parseResult.GetValue(calibrateLimitOpt)));
});
benchGdprCmd.Add(calibrateCmd);

benchCmd.Add(benchGdprCmd);

// bench eu-ai-act — same shape as bench gdpr, EU AI Act presets
// Phase-7 Tasks 7.21 + 7.22: --subject AND --input required (breaking).
var benchEuAiActPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("eu-ai-act") + Environment.NewLine + "Default: standard. Domain-pack composition: standard+high-risk-employment | standard+high-risk-credit | standard+high-risk-education (multi-pack composition like standard+high-risk-employment+high-risk-credit also supported)." };
var benchEuAiActSubjectOpt = new Option<string?>("--subject") { Description = "Subject name. REQUIRED — no default; previously defaulted to 'default-agent'." };
var benchEuAiActRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchEuAiActInputOpt = new Option<string?>("--input") { Description = "Agent input text for the evaluation. REQUIRED; there is no default." };
var benchEuAiActResponseOpt = new Option<string?>("--response") { Description = "The agent's actual RESPONSE to grade. Without it, --sut or --azure-from-env must name a live agent; --sut mock grades a canned answer that measures nothing and is not stored." };
var benchEuAiActResponseFileOpt = new Option<string?>("--response-file") { Description = "Path to a file containing the agent's actual response to grade (alternative to --response)." };
var benchEuAiActAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Drive a live agent from the environment (the provider AI_INFERENCE_PROVIDER selects) per scenario: each scenario's own prompt is sent to the agent and its real answer is graded, instead of grading a single --response. The judge resolves AZURE_OPENAI_JUDGE_* first, then the selected provider, so agent and judge can target different endpoints." };
var benchEuAiActCmd = new Command("eu-ai-act", "Run the EU AI Act compliance benchmark. Needs a target: --sut or --azure-from-env drives a live agent per scenario, or --response/--response-file grades an answer the agent gave. Without one it refuses; --sut mock grades a canned answer that measures nothing and is not stored.");
benchEuAiActCmd.Add(benchEuAiActPresetOpt);
benchEuAiActCmd.Add(benchEuAiActSubjectOpt);
benchEuAiActCmd.Add(benchEuAiActRootOpt);
benchEuAiActCmd.Add(benchEuAiActInputOpt);
benchEuAiActCmd.Add(benchEuAiActResponseOpt);
benchEuAiActCmd.Add(benchEuAiActResponseFileOpt);
benchEuAiActCmd.Add(benchEuAiActAzureFromEnvOpt);
var (benchEuAiActSutOpt, benchEuAiActSutTargets) = SutTargetResolver.AddOptionsTo(benchEuAiActCmd, "bench");
benchEuAiActCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchEuAiActPresetOpt) ?? "standard";
    var subject = parseResult.GetValue(benchEuAiActSubjectOpt);
    var input = parseResult.GetValue(benchEuAiActInputOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required. (There is no default subject.)");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    if (string.IsNullOrWhiteSpace(input))
    {
        Console.Error.WriteLine("Error: --input is required. (There is no built-in input fixture.)");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    var root = parseResult.GetValue(benchEuAiActRootOpt);
    var azureFromEnv = parseResult.GetValue(benchEuAiActAzureFromEnvOpt);

    // Bench Tier 2 (§2.2): same reasoning as bench gdpr above.
    var (mock, sut, mockError) = MockTarget.Parse(
        parseResult.GetValue(benchEuAiActSutOpt),
        anotherTargetNamed: azureFromEnv || parseResult.GetValue(benchEuAiActResponseOpt) is not null || parseResult.GetValue(benchEuAiActResponseFileOpt) is not null);
    if (mockError is not null)
    {
        Console.Error.WriteLine($"Error: {mockError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (agentOverride, sutError) = BenchTier1SutResolver.Resolve(
        sut,
        benchEuAiActSutTargets.ToDictionary(t => t.Sut, t => t.BindOptions(parseResult), StringComparer.OrdinalIgnoreCase),
        benchEuAiActSutTargets,
        endpoint: null,
        model: null,
        apiKey: null,
        subject);
    if (sutError is not null)
    {
        Console.Error.WriteLine($"Error: {sutError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var response = await ResolveBenchResponseAsync(parseResult.GetValue(benchEuAiActResponseOpt), parseResult.GetValue(benchEuAiActResponseFileOpt), ct);
    if (response.Error) return AgentEval.Cli.ExitCodes.UsageError;
    return await BenchEuAiActCommand.RunAsync(preset, subject, root, input, evaluatorOverride: null, agentOverride: agentOverride, responseText: response.Text, azureFromEnv: azureFromEnv, mock: mock, ct: ct);
});
// bench eu-ai-act calibrate
var euCalibrateRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: current directory)" };
var euCalibrateOutOpt = new Option<string?>("--out") { Description = "Output Markdown report path (default: .agenteval/calibration/eu-ai-act-calibration-{date}.md)" };
var euCalibrateDecisionsOpt = new Option<bool>("--decisions") { Description = "Grade with the decision model (TypeSafe Jev) instead of the generative judge, for a judge-vs-judge calibration. Reads TYPESAFE_API_KEY (or OPENROUTER_API_KEY); JEV_MODEL pins a build. ADR-033: the adapter is for calibration only — nothing it produces is persisted as an eval tree." };
var euCalibrateCmd = new Command("calibrate", "Run EU AI Act judge calibration against hand-labeled golden datasets");
euCalibrateCmd.Add(euCalibrateRootOpt);
euCalibrateCmd.Add(euCalibrateOutOpt);
euCalibrateCmd.Add(euCalibrateDecisionsOpt);
var euCalibrateLimitOpt = new Option<int?>("--limit") { Description = "Evaluate at most N entries per pillar (the one-item stage before a full paid run; requires --out)" };
euCalibrateCmd.Add(euCalibrateLimitOpt);
euCalibrateCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var root = parseResult.GetValue(euCalibrateRootOpt);
    var outPath = parseResult.GetValue(euCalibrateOutOpt);
    // Same as bench gdpr calibrate: with --decisions the report names the decision model, not "unknown".
    return await DecisionCalibration.RunAsync(
        parseResult.GetValue(euCalibrateDecisionsOpt),
        (decisionJudge, decisionJudgeIdentity) => BenchEuAiActCalibrateCommand.RunCoreAsync(
            root, outPath, evaluatorOverride: decisionJudge, ct: ct, evaluatorOverrideIdentity: decisionJudgeIdentity,
            limitPerPillar: parseResult.GetValue(euCalibrateLimitOpt)));
});
benchEuAiActCmd.Add(euCalibrateCmd);

benchCmd.Add(benchEuAiActCmd);

// bench agentic — same shape as bench eu-ai-act, agentic presets
var benchAgenticPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("agentic") + Environment.NewLine + "Default: agentic-execution. judge-quality / telemetry / stochastic-stability are pure-code (no LLM cost); safety needs a programmatic policy resolver." };
var benchAgenticSubjectOpt = new Option<string?>("--subject") { Description = "Subject name. REQUIRED — no default; previously defaulted to 'default-agent'." };
var benchAgenticRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchAgenticInputOpt = new Option<string?>("--input") { Description = "The question the agent answered. Required with --response/--response-file." };
var benchAgenticResponseOpt = new Option<string?>("--response") { Description = "The agent's actual RESPONSE to grade (needs --input). This or --response-file is required; --sut mock grades a canned answer that measures nothing and is not stored." };
var benchAgenticSutOpt = MockTarget.MockOnlySutOption();
var benchAgenticResponseFileOpt = new Option<string?>("--response-file") { Description = "Path to a file containing the agent's actual response to grade (alternative to --response, for multi-line output)." };
var benchAgenticBudgetTierOpt = new Option<string?>("--budget-tier") { Description = "Budget tier filter: trivial | low | medium | high | all (default: all). Components with a cost tier above the budget are filtered out and remaining weights are renormalized. Use 'low' or 'medium' for fast feedback loops; 'all' for full audit runs." };
var benchAgenticTraceOpt = new Option<string?>("--trace") { Description = "Path to a captured Glass Box trace (JSON). Attaches the dual-boundary trace to the evaluation so trace-aware evaluators (e.g. the glass-box-diagnostics preset) read real chat/tool-boundary data instead of skipping." };
var benchAgenticReferenceOpt = new Option<string?>("--reference") { Description = "The reference (expected) answer. Similarity, F1 and completeness grade against it (rag-quality); without one they report not measured." };
var benchAgenticReferenceFileOpt = new Option<string?>("--reference-file") { Description = "Path to a file containing the reference answer (alternative to --reference)." };
var benchAgenticContextOpt = new Option<string?>("--context") { Description = "The retrieved context the answer should be grounded in. Groundedness grades against it (rag-quality); without one it reports not measured." };
var benchAgenticContextFileOpt = new Option<string?>("--context-file") { Description = "Path to a file containing the retrieved context (alternative to --context)." };
var benchAgenticCmd = new Command("agentic", "Run the agentic behavior benchmark on an answer the agent gave (--response/--response-file with its --input) or a captured run (--trace). Without one it refuses; --sut mock grades a canned answer that measures nothing and is not stored.");
benchAgenticCmd.Add(benchAgenticPresetOpt);
benchAgenticCmd.Add(benchAgenticSubjectOpt);
benchAgenticCmd.Add(benchAgenticRootOpt);
benchAgenticCmd.Add(benchAgenticInputOpt);
benchAgenticCmd.Add(benchAgenticResponseOpt);
benchAgenticCmd.Add(benchAgenticResponseFileOpt);
benchAgenticCmd.Add(benchAgenticBudgetTierOpt);
benchAgenticCmd.Add(benchAgenticTraceOpt);
benchAgenticCmd.Add(benchAgenticReferenceOpt);
benchAgenticCmd.Add(benchAgenticReferenceFileOpt);
benchAgenticCmd.Add(benchAgenticContextOpt);
benchAgenticCmd.Add(benchAgenticContextFileOpt);
benchAgenticCmd.Add(benchAgenticSutOpt);
benchAgenticCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchAgenticPresetOpt) ?? "agentic-execution";
    var subject = parseResult.GetValue(benchAgenticSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required. (There is no default subject.)");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    var root = parseResult.GetValue(benchAgenticRootOpt);
    var input = parseResult.GetValue(benchAgenticInputOpt);
    var response = await ResolveBenchResponseAsync(parseResult.GetValue(benchAgenticResponseOpt), parseResult.GetValue(benchAgenticResponseFileOpt), ct);
    if (response.Error) return AgentEval.Cli.ExitCodes.UsageError;
    var reference = await ResolveTextOrFileAsync(parseResult.GetValue(benchAgenticReferenceOpt), parseResult.GetValue(benchAgenticReferenceFileOpt), "--reference-file", ct);
    if (reference.Error) return AgentEval.Cli.ExitCodes.UsageError;
    var context = await ResolveTextOrFileAsync(parseResult.GetValue(benchAgenticContextOpt), parseResult.GetValue(benchAgenticContextFileOpt), "--context-file", ct);
    if (context.Error) return AgentEval.Cli.ExitCodes.UsageError;
    var budgetTier = parseResult.GetValue(benchAgenticBudgetTierOpt);
    var traceFile = parseResult.GetValue(benchAgenticTraceOpt);
    var (mock, otherSut, mockError) = MockTarget.Parse(parseResult.GetValue(benchAgenticSutOpt), anotherTargetNamed: response.Text is not null);
    if (otherSut is not null)
    {
        mockError = $"Unknown --sut value: '{otherSut}'. Valid here: {MockTarget.Sut}.";
    }
    if (mockError is not null)
    {
        Console.Error.WriteLine($"Error: {mockError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    return await BenchAgenticCommand.RunAsync(preset, subject, root, input, response.Text, evaluatorOverride: null, budgetTier, traceFile, mock,
        reference: reference.Text, context: context.Text, ct: ct);
});
// bench agentic calibrate
var agenticCalibrateRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: current directory)" };
var agenticCalibrateOutOpt = new Option<string?>("--out") { Description = "Output Markdown report path (default: .agenteval/calibration/agentic-calibration-{date}.md)" };
var agenticCalibrateRecordsOpt = new Option<string?>("--records") { Description = "Also write one JSON line per evaluated case (verdict, every criterion's verdict, prompt id and hash) to this path, so the run can be re-analysed without repeating the calls" };
var agenticCalibrateLimitOpt = new Option<int?>("--limit") { Description = "Evaluate at most N entries per category (the one-item stage before a full paid run)" };
var agenticCalibrateCmd = new Command("calibrate", "Run agentic judge calibration against hand-labeled golden datasets");
agenticCalibrateCmd.Add(agenticCalibrateRootOpt);
agenticCalibrateCmd.Add(agenticCalibrateOutOpt);
agenticCalibrateCmd.Add(agenticCalibrateRecordsOpt);
agenticCalibrateCmd.Add(agenticCalibrateLimitOpt);
agenticCalibrateCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var root = parseResult.GetValue(agenticCalibrateRootOpt);
    var outPath = parseResult.GetValue(agenticCalibrateOutOpt);
    var records = parseResult.GetValue(agenticCalibrateRecordsOpt);
    var limit = parseResult.GetValue(agenticCalibrateLimitOpt);
    return await BenchAgenticCalibrateCommand.RunAsync(root, outPath, ct: ct, recordsPath: records, limitPerCategory: limit);
});
benchAgenticCmd.Add(agenticCalibrateCmd);

benchCmd.Add(benchAgenticCmd);

// bench owasp — Phase 5 (v0.10.0-beta): OWASP LLM Top 10 red-team scan.
// Presets: top10 | smoke | audit | top10-rag.
var benchOwaspPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("owasp") + Environment.NewLine + "Default: top10. The smoke preset uses 3 attacks (PromptInjection + Jailbreak + PIILeakage); audit runs at Comprehensive intensity for higher-confidence verdicts." };
var benchOwaspSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
var benchOwaspRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchOwaspInputOpt = new Option<string?>("--input") { Description = "Provenance text for the run (the OWASP attack pipeline generates its own probes; --input is recorded for traceability, not consumed by attacks)." };
var benchOwaspAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Scan a chat agent built from the environment (the provider AI_INFERENCE_PROVIDER selects). Requires a configured provider; run with none set to see what is missing." };
var benchOwaspEndpointOpt = new Option<string?>("--endpoint") { Description = "OpenAI-compatible API endpoint URL (Ollama, LM Studio, vLLM, Groq, Together.ai, Mistral, etc.) — an alternative to --azure-from-env. Requires --model." };
var benchOwaspModelOpt = new Option<string?>("--model") { Description = "Model name (required with --endpoint)." };
var benchOwaspApiKeyOpt = new Option<string?>("--api-key") { Description = "API key for --endpoint (or set OPENAI_API_KEY env var)." };
var benchOwaspCmd = new Command("owasp", "Run the OWASP LLM Top 10 red-team benchmark. Needs a target: --sut, --endpoint/--model, or --azure-from-env (the provider AI_INFERENCE_PROVIDER selects); without one it refuses. --sut mock runs a stand-in that measures nothing and is not stored. Attacks are graded judge first by the judge the environment configures (AZURE_OPENAI_JUDGE_*, else AI_INFERENCE_PROVIDER), as `agenteval redteam --judge` grades them; use `agenteval redteam` for a fully-parameterised scan.");
benchOwaspCmd.Add(benchOwaspPresetOpt);
benchOwaspCmd.Add(benchOwaspSubjectOpt);
benchOwaspCmd.Add(benchOwaspRootOpt);
benchOwaspCmd.Add(benchOwaspInputOpt);
benchOwaspCmd.Add(benchOwaspAzureFromEnvOpt);
benchOwaspCmd.Add(benchOwaspEndpointOpt);
benchOwaspCmd.Add(benchOwaspModelOpt);
benchOwaspCmd.Add(benchOwaspApiKeyOpt);
var (benchOwaspSutOpt, benchOwaspSutTargets) = SutTargetResolver.AddOptionsTo(benchOwaspCmd, "bench");
benchOwaspCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchOwaspPresetOpt) ?? "top10";
    var subject = parseResult.GetValue(benchOwaspSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required.");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    var root = parseResult.GetValue(benchOwaspRootOpt);
    var input = parseResult.GetValue(benchOwaspInputOpt);
    var azureFromEnv = parseResult.GetValue(benchOwaspAzureFromEnvOpt);
    var (mock, sut, mockError) = MockTarget.Parse(
        parseResult.GetValue(benchOwaspSutOpt),
        anotherTargetNamed: azureFromEnv || !string.IsNullOrWhiteSpace(parseResult.GetValue(benchOwaspEndpointOpt)));
    if (mockError is not null)
    {
        Console.Error.WriteLine($"Error: {mockError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (agentOverride, error) = BenchTier1SutResolver.Resolve(
        sut,
        benchOwaspSutTargets.ToDictionary(t => t.Sut, t => t.BindOptions(parseResult), StringComparer.OrdinalIgnoreCase),
        benchOwaspSutTargets,
        parseResult.GetValue(benchOwaspEndpointOpt),
        parseResult.GetValue(benchOwaspModelOpt),
        parseResult.GetValue(benchOwaspApiKeyOpt),
        subject);
    if (error is not null)
    {
        Console.Error.WriteLine($"Error: {error}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (exitCode, _) = await BenchOwaspCommand.RunAsync(preset, subject, root, input, evaluatorOverride: null, agentOverride, azureFromEnv, mock,
        agentModel: agentOverride is not null && sut is null ? parseResult.GetValue(benchOwaspModelOpt) : null, ct: ct);
    return exitCode;
});
benchCmd.Add(benchOwaspCmd);

// bench mitre — Phase 6 (v0.10.0-beta): MITRE ATLAS red-team scan.
// Presets: atlas-baseline | atlas-smoke | atlas-audit-grade.
var benchMitrePresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("mitre") + Environment.NewLine + "Default: atlas-baseline. The atlas-smoke preset uses 3 attacks (PromptInjection + Jailbreak + PIILeakage); atlas-audit-grade runs at Comprehensive intensity for higher-confidence verdicts." };
var benchMitreSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
var benchMitreRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchMitreInputOpt = new Option<string?>("--input") { Description = "Provenance text for the run (the MITRE ATLAS attack pipeline generates its own probes; --input is recorded for traceability, not consumed by attacks)." };
var benchMitreAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Scan a chat agent built from the environment (the provider AI_INFERENCE_PROVIDER selects). Requires a configured provider; run with none set to see what is missing." };
var benchMitreEndpointOpt = new Option<string?>("--endpoint") { Description = "OpenAI-compatible API endpoint URL (Ollama, LM Studio, vLLM, Groq, Together.ai, Mistral, etc.) — an alternative to --azure-from-env. Requires --model." };
var benchMitreModelOpt = new Option<string?>("--model") { Description = "Model name (required with --endpoint)." };
var benchMitreApiKeyOpt = new Option<string?>("--api-key") { Description = "API key for --endpoint (or set OPENAI_API_KEY env var)." };
var benchMitreCmd = new Command("mitre", "Run the MITRE ATLAS red-team benchmark. Needs a target: --sut, --endpoint/--model, or --azure-from-env (the provider AI_INFERENCE_PROVIDER selects); without one it refuses. --sut mock runs a stand-in that measures nothing and is not stored. Attacks are graded judge first by the judge the environment configures (AZURE_OPENAI_JUDGE_*, else AI_INFERENCE_PROVIDER), as `agenteval redteam --judge` grades them; use `agenteval redteam` for a fully-parameterised scan.");
benchMitreCmd.Add(benchMitrePresetOpt);
benchMitreCmd.Add(benchMitreSubjectOpt);
benchMitreCmd.Add(benchMitreRootOpt);
benchMitreCmd.Add(benchMitreInputOpt);
benchMitreCmd.Add(benchMitreAzureFromEnvOpt);
benchMitreCmd.Add(benchMitreEndpointOpt);
benchMitreCmd.Add(benchMitreModelOpt);
benchMitreCmd.Add(benchMitreApiKeyOpt);
var (benchMitreSutOpt, benchMitreSutTargets) = SutTargetResolver.AddOptionsTo(benchMitreCmd, "bench");
benchMitreCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchMitrePresetOpt) ?? "atlas-baseline";
    var subject = parseResult.GetValue(benchMitreSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required.");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    var root = parseResult.GetValue(benchMitreRootOpt);
    var input = parseResult.GetValue(benchMitreInputOpt);
    var azureFromEnv = parseResult.GetValue(benchMitreAzureFromEnvOpt);
    var (mock, sut, mockError) = MockTarget.Parse(
        parseResult.GetValue(benchMitreSutOpt),
        anotherTargetNamed: azureFromEnv || !string.IsNullOrWhiteSpace(parseResult.GetValue(benchMitreEndpointOpt)));
    if (mockError is not null)
    {
        Console.Error.WriteLine($"Error: {mockError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (agentOverride, error) = BenchTier1SutResolver.Resolve(
        sut,
        benchMitreSutTargets.ToDictionary(t => t.Sut, t => t.BindOptions(parseResult), StringComparer.OrdinalIgnoreCase),
        benchMitreSutTargets,
        parseResult.GetValue(benchMitreEndpointOpt),
        parseResult.GetValue(benchMitreModelOpt),
        parseResult.GetValue(benchMitreApiKeyOpt),
        subject);
    if (error is not null)
    {
        Console.Error.WriteLine($"Error: {error}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (exitCode, _) = await BenchMitreCommand.RunAsync(preset, subject, root, input, evaluatorOverride: null, agentOverride, azureFromEnv, mock,
        agentModel: agentOverride is not null && sut is null ? parseResult.GetValue(benchMitreModelOpt) : null, ct: ct);
    return exitCode;
});
benchCmd.Add(benchMitreCmd);

// bench nist — NIST AI RMF (AI 100-1) red-team evidence. Presets: rmf-baseline | rmf-smoke | rmf-audit-grade.
var benchNistPresetOpt = new Option<string?>("--preset") { Description = PresetsHelpFromRegistry("nist") + Environment.NewLine + "Default: rmf-baseline. rmf-smoke uses 3 attacks (PromptInjection + Jailbreak + PIILeakage); rmf-audit-grade runs at Comprehensive intensity. Scores only MEASURE security/privacy/validity sub-actions; GOVERN/MAP/MANAGE are Not Applicable." };
var benchNistSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
var benchNistRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchNistInputOpt = new Option<string?>("--input") { Description = "Provenance text for the run (the attack pipeline generates its own probes; --input is recorded for traceability, not consumed by attacks)." };
var benchNistAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Scan a chat agent built from the environment (the provider AI_INFERENCE_PROVIDER selects). Requires a configured provider; run with none set to see what is missing." };
var benchNistEndpointOpt = new Option<string?>("--endpoint") { Description = "OpenAI-compatible API endpoint URL (Ollama, LM Studio, vLLM, Groq, Together.ai, Mistral, etc.) — an alternative to --azure-from-env. Requires --model." };
var benchNistModelOpt = new Option<string?>("--model") { Description = "Model name (required with --endpoint)." };
var benchNistApiKeyOpt = new Option<string?>("--api-key") { Description = "API key for --endpoint (or set OPENAI_API_KEY env var)." };
var benchNistCmd = new Command("nist", "Run the NIST AI RMF (AI 100-1) red-team benchmark. Needs a target: --sut, --endpoint/--model, or --azure-from-env (the provider AI_INFERENCE_PROVIDER selects); without one it refuses. --sut mock runs a stand-in that measures nothing and is not stored. Attacks are graded judge first by the judge the environment configures (AZURE_OPENAI_JUDGE_*, else AI_INFERENCE_PROVIDER), as `agenteval redteam --judge` grades them; use `agenteval redteam` for a fully-parameterised scan.");
benchNistCmd.Add(benchNistPresetOpt);
benchNistCmd.Add(benchNistSubjectOpt);
benchNistCmd.Add(benchNistRootOpt);
benchNistCmd.Add(benchNistInputOpt);
benchNistCmd.Add(benchNistAzureFromEnvOpt);
benchNistCmd.Add(benchNistEndpointOpt);
benchNistCmd.Add(benchNistModelOpt);
benchNistCmd.Add(benchNistApiKeyOpt);
var (benchNistSutOpt, benchNistSutTargets) = SutTargetResolver.AddOptionsTo(benchNistCmd, "bench");
benchNistCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchNistPresetOpt) ?? "rmf-baseline";
    var subject = parseResult.GetValue(benchNistSubjectOpt);
    if (string.IsNullOrWhiteSpace(subject))
    {
        Console.Error.WriteLine("Error: --subject is required.");
        return AgentEval.Cli.ExitCodes.UsageError;
    }
    var root = parseResult.GetValue(benchNistRootOpt);
    var input = parseResult.GetValue(benchNistInputOpt);
    var azureFromEnv = parseResult.GetValue(benchNistAzureFromEnvOpt);
    var (mock, sut, mockError) = MockTarget.Parse(
        parseResult.GetValue(benchNistSutOpt),
        anotherTargetNamed: azureFromEnv || !string.IsNullOrWhiteSpace(parseResult.GetValue(benchNistEndpointOpt)));
    if (mockError is not null)
    {
        Console.Error.WriteLine($"Error: {mockError}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (agentOverride, error) = BenchTier1SutResolver.Resolve(
        sut,
        benchNistSutTargets.ToDictionary(t => t.Sut, t => t.BindOptions(parseResult), StringComparer.OrdinalIgnoreCase),
        benchNistSutTargets,
        parseResult.GetValue(benchNistEndpointOpt),
        parseResult.GetValue(benchNistModelOpt),
        parseResult.GetValue(benchNistApiKeyOpt),
        subject);
    if (error is not null)
    {
        Console.Error.WriteLine($"Error: {error}");
        return AgentEval.Cli.ExitCodes.UsageError;
    }

    var (exitCode, _) = await BenchNistCommand.RunAsync(preset, subject, root, input, evaluatorOverride: null, agentOverride, azureFromEnv, mock,
        agentModel: agentOverride is not null && sut is null ? parseResult.GetValue(benchNistModelOpt) : null, ct: ct);
    return exitCode;
});
benchCmd.Add(benchNistCmd);

// bench perf — Phase 8 (v0.10.0-beta): Performance benchmark CLI surface (previously CLI-less).
// Sub-commands resolve the "perf" family from BenchmarkFamilyRegistry and dispatch to its
// Convention-2 EvaluateAsync adapter.
{
    var benchPerfSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation). REQUIRED." };
    var benchPerfPromptOpt = new Option<string?>("--prompt") { Description = "Prompt to measure against (default: 'Hello!')." };
    var benchPerfRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
    var benchPerfAzureFromEnvOpt = new Option<bool>("--azure-from-env") { Description = "Measure a chat agent built from the environment (the provider AI_INFERENCE_PROVIDER selects). Requires a configured provider; run with none set to see what is missing." };
    var benchPerfEndpointOpt = new Option<string?>("--endpoint") { Description = "OpenAI-compatible API endpoint URL (Ollama, LM Studio, vLLM, Groq, Together.ai, Mistral, etc.) — an alternative to --azure-from-env. Requires --model." };
    var benchPerfModelOpt = new Option<string?>("--model") { Description = "Model name (required with --endpoint)." };
    var benchPerfApiKeyOpt = new Option<string?>("--api-key") { Description = "API key for --endpoint (or set OPENAI_API_KEY env var)." };

    // bench longmemeval — T0.6 (v1.1): closes CLI ↔ registry gap.
    {
        var benchLmePresetOpt = new Option<string?>("--preset") { Description = "subset | full. Default: subset. 'full' requires LONGMEMEVAL_DATASET_PATH." };
        var benchLmeSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent under evaluation). REQUIRED." };
        var benchLmeRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };

        var benchLmeCmd = new Command("longmemeval", "Run the LongMemEval ICLR 2025 memory benchmark. Uses the provider AI_INFERENCE_PROVIDER selects (azure | bitdeer | openai | foundry | openai-compatible) — there is no stub fallback (the runner makes ~2 LLM calls per question; the LLM round-trip IS the correctness signal).");
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
                return AgentEval.Cli.ExitCodes.UsageError;
            }
            var root = parseResult.GetValue(benchLmeRootOpt);
            return await BenchLongMemEvalCommand.RunAsync(preset, subject, root, ct);
        });
        benchCmd.Add(benchLmeCmd);
    }

    // bench typedmemeval — closes the same CLI ↔ registry gap for the TypedMemEval family.
    {
        var benchTmeVerticalOpt = new Option<string?>("--vertical") { Description = "prospective | episodic | arithmetic | workingmemory | forgetting. REQUIRED — the verticals measure different mechanisms, so there is no default." };
        var benchTmeSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent under evaluation). REQUIRED." };
        var benchTmeRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
        var benchTmeEvidenceOpt = new Option<string?>("--evidence-detail") { Description = "references (default) | content. 'content' opts in to storing the retrieved TEXT in the result, not just which sessions were retrieved — it answers 'was the needed value actually in the prompt', which identifiers alone cannot. Off by default and loud when engaged: the content is whatever the agent retrieved, so use it only on corpora you control. Credential-shaped and control-byte content is still rejected." };

        var benchTmeCmd = new Command("typedmemeval", $"Run one TypedMemEval {TypedMemEvalVerticalDescriptor.CorpusRevision} (AgentEval) vertical — prospective, episodic, arithmetic, working-memory or forgetting memory behaviour, one embedded corpus each. Uses the provider AI_INFERENCE_PROVIDER selects (azure | bitdeer | openai | foundry | openai-compatible) — there is no stub fallback (the judge round-trip IS the correctness signal). Reports a typed outcome vector, never a single percentage; results are not comparable with LongMemEval.");
        benchTmeCmd.Add(benchTmeVerticalOpt);
        benchTmeCmd.Add(benchTmeSubjectOpt);
        benchTmeCmd.Add(benchTmeRootOpt);
        benchTmeCmd.Add(benchTmeEvidenceOpt);
        benchTmeCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var vertical = parseResult.GetValue(benchTmeVerticalOpt);
            if (string.IsNullOrWhiteSpace(vertical))
            {
                Console.Error.WriteLine("Error: --vertical is required. Known: prospective, episodic, arithmetic, workingmemory, forgetting.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }
            var subject = parseResult.GetValue(benchTmeSubjectOpt);
            if (string.IsNullOrWhiteSpace(subject))
            {
                Console.Error.WriteLine("Error: --subject is required.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }
            var root = parseResult.GetValue(benchTmeRootOpt);

            var evidenceDetail = parseResult.GetValue(benchTmeEvidenceOpt)?.Trim().ToLowerInvariant();
            // Rejected rather than silently defaulted. A typo here is the difference between
            // capturing content and not, and a run that quietly did the safer thing still wasted
            // the spend the caller was paying for.
            if (evidenceDetail is not (null or "references" or "content"))
            {
                Console.Error.WriteLine(
                    $"Error: --evidence-detail must be 'references' or 'content', not '{evidenceDetail}'.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }

            return await BenchTypedMemEvalCommand.RunAsync(
                vertical, subject, root, captureEvidenceContent: evidenceDetail == "content", ct);
        });
        benchCmd.Add(benchTmeCmd);
    }

    // bench memory — T0.6 (v1.1): closes CLI ↔ registry gap.
    {
        var benchMemPresetOpt = new Option<string?>("--preset") { Description = "quick | standard | full | diagnostic | overflow. Default: quick." };
        var benchMemSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent under evaluation). REQUIRED." };
        var benchMemRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };

        var benchMemCmd = new Command("memory", "Run the AgentEval memory benchmark. Uses the provider AI_INFERENCE_PROVIDER selects (azure | bitdeer | openai | foundry | openai-compatible) — there is no stub fallback (the benchmark needs a real LLM-backed agent under test).");
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
                return AgentEval.Cli.ExitCodes.UsageError;
            }
            var root = parseResult.GetValue(benchMemRootOpt);
            return await BenchMemoryCommand.RunAsync(preset, subject, root, ct);
        });
        benchCmd.Add(benchMemCmd);
    }

    // bench trace-fidelity — Glass Box: reconcile an agent-boundary vs chat-boundary trace pair.
    {
        var tfAgentOpt = new Option<string?>("--agent-trace") { Description = "Path to the agent-boundary .trace.json (the framework's self-report). REQUIRED." };
        var tfChatOpt = new Option<string?>("--chat-trace") { Description = "Path to the chat-boundary .trace.json (ground truth at the model interface). REQUIRED." };
        var tfPresetOpt = new Option<string?>("--preset") { Description = "smoke | standard | audit-grade. Default: standard." };
        var tfSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent under evaluation). REQUIRED." };
        var tfRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };

        var benchTraceFidelityCmd = new Command("trace-fidelity", "Reconcile an agent-boundary trace against a chat-boundary trace; flags missing/phantom tool calls, hidden retries, argument drift, token under-reporting, suppressed finish reasons. Pure code (no LLM cost).");
        benchTraceFidelityCmd.Add(tfAgentOpt);
        benchTraceFidelityCmd.Add(tfChatOpt);
        benchTraceFidelityCmd.Add(tfPresetOpt);
        benchTraceFidelityCmd.Add(tfSubjectOpt);
        benchTraceFidelityCmd.Add(tfRootOpt);
        benchTraceFidelityCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agentTrace = parseResult.GetValue(tfAgentOpt);
            var chatTrace = parseResult.GetValue(tfChatOpt);
            var subject = parseResult.GetValue(tfSubjectOpt);
            if (string.IsNullOrWhiteSpace(agentTrace) || string.IsNullOrWhiteSpace(chatTrace))
            {
                Console.Error.WriteLine("Error: --agent-trace and --chat-trace are required.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                Console.Error.WriteLine("Error: --subject is required.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }

            var preset = parseResult.GetValue(tfPresetOpt) ?? "standard";
            var root = parseResult.GetValue(tfRootOpt);
            return await BenchTraceFidelityCommand.RunAsync(agentTrace, chatTrace, preset, subject, root, ct);
        });
        benchCmd.Add(benchTraceFidelityCmd);
    }

    // bench workflow-trace-fidelity — Glass Box: per-executor ledger vs chat-boundary truth (workflows).
    {
        var wtfTraceOpt = new Option<string?>("--workflow-trace") { Description = "Path to a saved workflow .trace.json. Per-executor chat truth is read from its ExecutorTraces (absent → every executor is NoTruth). REQUIRED." };
        var wtfPresetOpt = new Option<string?>("--preset") { Description = "smoke | standard | audit-grade. Default: standard." };
        var wtfSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (workflow under evaluation). REQUIRED." };
        var wtfRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };

        var benchWorkflowTraceFidelityCmd = new Command("workflow-trace-fidelity", "Reconcile each workflow executor's framework-reported per-executor ledger (tokens + finish reason) against chat-boundary truth. Pure code (no LLM cost).");
        benchWorkflowTraceFidelityCmd.Add(wtfTraceOpt);
        benchWorkflowTraceFidelityCmd.Add(wtfPresetOpt);
        benchWorkflowTraceFidelityCmd.Add(wtfSubjectOpt);
        benchWorkflowTraceFidelityCmd.Add(wtfRootOpt);
        benchWorkflowTraceFidelityCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var workflowTrace = parseResult.GetValue(wtfTraceOpt);
            var subject = parseResult.GetValue(wtfSubjectOpt);
            if (string.IsNullOrWhiteSpace(workflowTrace))
            {
                Console.Error.WriteLine("Error: --workflow-trace is required.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                Console.Error.WriteLine("Error: --subject is required.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }

            var preset = parseResult.GetValue(wtfPresetOpt) ?? "standard";
            var root = parseResult.GetValue(wtfRootOpt);
            return await BenchWorkflowTraceFidelityCommand.RunAsync(workflowTrace, preset, subject, root, ct);
        });
        benchCmd.Add(benchWorkflowTraceFidelityCmd);
    }

    // bench autoaudit — Glass Box flagship: cross-endpoint honesty/safety/cost comparison of real models.
    {
        var aaOutOpt = new Option<string?>("--out") { Description = "Path to write the Markdown comparison report (optional; also printed to stdout)." };
        var aaModelsOpt = new Option<string?>("--models") { Description = "Comma-separated models to audit on the configured provider. Default: the models the provider names (*_MODEL, *_MODEL_2, *_MODEL_3)." };
        var aaSutOpt = new Option<string?>("--sut") { Description = $"Only '{MockTarget.Sut}' here: the scripted showcase over three made-up endpoints, labelled MOCK. It measures no model." };
        var benchAutoAuditCmd = new Command("autoaudit", "Glass Box auto-audit of the configured provider's models: one support task each, ranked on safety (PII gate blocks, and any leak the gate missed), Trace Fidelity (the tool loop's account vs the chat boundary) and cost (tokens/latency). A model that never does the task is not ranked. Without a provider it refuses. --sut mock runs the scripted showcase.");
        benchAutoAuditCmd.Add(aaOutOpt);
        benchAutoAuditCmd.Add(aaModelsOpt);
        benchAutoAuditCmd.Add(aaSutOpt);
        benchAutoAuditCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var sut = parseResult.GetValue(aaSutOpt);
            if (sut is not null && !MockTarget.IsRequested(sut))
            {
                Console.Error.WriteLine($"Error: Unknown --sut value: '{sut}'. Valid: {MockTarget.Sut}.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }
            var models = parseResult.GetValue(aaModelsOpt)?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return await BenchAutoAuditCommand.RunAsync(parseResult.GetValue(aaOutOpt), models, mock: sut is not null, ct);
        });
        benchCmd.Add(benchAutoAuditCmd);
    }

    var benchPerfCmd = new Command("perf", "Run a performance benchmark (latency, throughput, cost). Needs a target: --sut, --endpoint/--model, or --azure-from-env; without one it refuses. --sut mock measures a stand-in and is not stored.");

    foreach (var presetName in new[] { "latency", "throughput", "cost" })
    {
        var presetCmd = new Command(presetName, $"Run the perf {presetName} preset");
        presetCmd.Add(benchPerfSubjectOpt);
        presetCmd.Add(benchPerfPromptOpt);
        presetCmd.Add(benchPerfRootOpt);
        presetCmd.Add(benchPerfAzureFromEnvOpt);
        presetCmd.Add(benchPerfEndpointOpt);
        presetCmd.Add(benchPerfModelOpt);
        presetCmd.Add(benchPerfApiKeyOpt);
        var (presetSutOpt, presetSutTargets) = SutTargetResolver.AddOptionsTo(presetCmd, "bench");
        var capturedPreset = presetName;
        presetCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var subject = parseResult.GetValue(benchPerfSubjectOpt);
            if (string.IsNullOrWhiteSpace(subject))
            {
                Console.Error.WriteLine("Error: --subject is required.");
                return AgentEval.Cli.ExitCodes.UsageError;
            }
            var prompt = parseResult.GetValue(benchPerfPromptOpt);
            var root = parseResult.GetValue(benchPerfRootOpt);
            var azureFromEnv = parseResult.GetValue(benchPerfAzureFromEnvOpt);
            var (mock, sut, mockError) = MockTarget.Parse(
                parseResult.GetValue(presetSutOpt),
                anotherTargetNamed: azureFromEnv || !string.IsNullOrWhiteSpace(parseResult.GetValue(benchPerfEndpointOpt)));
            if (mockError is not null)
            {
                Console.Error.WriteLine($"Error: {mockError}");
                return AgentEval.Cli.ExitCodes.UsageError;
            }

            var (agentOverride, error) = BenchTier1SutResolver.Resolve(
                sut,
                presetSutTargets.ToDictionary(t => t.Sut, t => t.BindOptions(parseResult), StringComparer.OrdinalIgnoreCase),
                presetSutTargets,
                parseResult.GetValue(benchPerfEndpointOpt),
                parseResult.GetValue(benchPerfModelOpt),
                parseResult.GetValue(benchPerfApiKeyOpt),
                subject);
            if (error is not null)
            {
                Console.Error.WriteLine($"Error: {error}");
                return AgentEval.Cli.ExitCodes.UsageError;
            }

            // The --endpoint target's model prices the run; a --sut target names no model.
            var agentModel = agentOverride is not null && sut is null ? parseResult.GetValue(benchPerfModelOpt) : null;
            return await BenchPerfCommand.RunAsync(capturedPreset, subject, prompt, root, agentOverride, azureFromEnv, mock, agentModel, ct);
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
    return Task.FromResult(AgentEval.Cli.ExitCodes.UsageError);
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
        return AgentEval.Cli.ExitCodes.UsageError;
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
        return AgentEval.Cli.ExitCodes.UsageError;
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

// --log-file: a RECURSIVE (System.CommandLine 2.0's term for "global") option — visible to every subcommand's
// own ParseResult without each command needing to redeclare it. Every IChatClient-constructing call site in
// the CLI reads the ambient VerboseLog.Writer this sets (via VerboseLog.Wrap), not this option directly — see
// VerboseLog's own remarks for why an ambient static, not threading the option through every command, is the
// pragmatic choice here (no DI container exists to do this more centrally).
var logFileOpt = new Option<string?>("--log-file")
{
    Description = "Write a human-readable, UNREDACTED log of every LLM request/response (and any judge/attacker/SUT " +
                   "client) to this file, for troubleshooting. The file can contain secrets/PII carried in prompts " +
                   "or responses — never share or commit it. Overwritten on each invocation.",
    Recursive = true,
};
rootCmd.Options.Add(logFileOpt);

// --capture-fixture: a SEPARATE recursive option from --log-file, not a mode of it — --log-file's format is
// deliberately lossy (newest-message-only, to avoid O(N²) growth in a multi-turn log); this captures full
// per-round-trip fidelity as JSONL for later replay/fixture generation via `agenteval log-file to-fixture`.
// Both can be passed together. Same ambient-writer pattern as --log-file — see FixtureCapture's own remarks.
var captureFixtureOpt = new Option<string?>("--capture-fixture")
{
    Description = "Write a structured JSONL capture of every LLM round-trip (full message array, full response) to " +
                   "this file, for 'agenteval log-file to-fixture' or 'agenteval log-file replay'. Off by default. " +
                   "Strings matching known credential formats are masked; prompts and responses are otherwise " +
                   "written as-is, so treat it like --log-file output: never share or commit it. Overwritten on " +
                   "each invocation.",
    Recursive = true,
};
rootCmd.Options.Add(captureFixtureOpt);

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
rootCmd.Add(CompareCommand.Create());         // compare — ADR-031 S5; refuses deltas across incomparable runs (exit 13)
rootCmd.Add(benchCmd);
rootCmd.Add(complianceCmd);
rootCmd.Add(renderBenchCmd);
rootCmd.Add(mcCmd);

// Gatekeeper CLI interop bridge — invoke gates from any language via a versioned verdict JSON.
rootCmd.Add(AgentEval.Cli.Commands.Gatekeeper.GatekeeperCommand.Create());

// MAF Agent Skills utilities — `skills scan <path>` reaches the Phase 2 compliance scanner from the CLI
// (previously library-only; see Skills-Scan-CLI-Verb-Design.md). Credential-free, offline, static scan.
rootCmd.Add(SkillsScanCommand.Create());

// `log-file to-fixture` — turns a --capture-fixture JSONL capture into a deterministic, versionable test
// fixture (ScriptedChatClient.FromFixture).
rootCmd.Add(LogFileCommand.Create());

var parseResult = rootCmd.Parse(args);

// A parse error — an unknown command or option, a value that does not convert (`--runs abc`), a missing required
// option, no command at all — exits ExitCodes.UsageError (2). System.CommandLine's own ParseErrorAction prints the
// errors to stderr and the help to stdout, then returns 1, which this CLI's contract reserves for a failed evaluation
// (ExitCodes.TestFailure): CI could not tell a typo from a failing run. Its output is kept; only the code changes.
// --help and --version are not parse errors: their actions clear the errors and return 0.
// This runs before --log-file and --capture-fixture are read: reading an option whose own value failed to parse
// throws, and an invocation that never started must not create or truncate either file.
if (parseResult.Action is System.CommandLine.Invocation.ParseErrorAction)
{
    await parseResult.InvokeAsync();
    return AgentEval.Cli.ExitCodes.UsageError;
}

using var logWriter = VerboseLog.Initialize(parseResult.GetValue(logFileOpt));
using var fixtureWriter = FixtureCapture.Initialize(parseResult.GetValue(captureFixtureOpt));
return await parseResult.InvokeAsync();

namespace AgentEval.Cli.Commands
{
    using AgentEval.Core;
    using AgentEval.Decisions;

    /// <summary>
    /// The <c>--decisions</c> switch of <c>bench gdpr calibrate</c> and <c>bench eu-ai-act calibrate</c>: grades the
    /// golden datasets with the decision model instead of the generative judge, and names that judge in the report.
    /// </summary>
    internal static class DecisionCalibration
    {
        /// <summary>
        /// Runs <paramref name="runCalibration"/> with no evaluator override when <paramref name="decisions"/> is
        /// off, and otherwise with a <see cref="DecisionJudge"/> and the identity its report header names it by.
        /// </summary>
        /// <param name="decisions">Whether <c>--decisions</c> was passed.</param>
        /// <param name="runCalibration">The calibration to run, given the evaluator override and its identity.</param>
        /// <param name="resolveOptions">The transport source; <see langword="null"/> reads the environment.</param>
        /// <param name="createClient">Builds the client from the options; <see langword="null"/> builds the HTTP client.</param>
        /// <returns>
        /// What <paramref name="runCalibration"/> returned, or <see cref="ExitCodes.RuntimeError"/> when
        /// <c>--decisions</c> was passed and no decision-model transport is configured.
        /// </returns>
        internal static async Task<int> RunAsync(
            bool decisions,
            Func<IEvaluator?, CalibrationJudgeIdentity?, Task<int>> runCalibration,
            Func<(SystemOneClientOptions? Options, string? Diagnostic)>? resolveOptions = null,
            Func<SystemOneClientOptions, IDecisionClient>? createClient = null)
        {
            ArgumentNullException.ThrowIfNull(runCalibration);
            if (!decisions)
                return await runCalibration(null, null);

            resolveOptions ??= static () => DecisionClientFactory.TryResolve();
            createClient ??= static options => new SystemOneDecisionClient(options);

            var (decisionOptions, decisionDiagnostic) = resolveOptions();
            if (decisionOptions is null)
            {
                Console.Error.WriteLine($"✖ --decisions needs a decision-model transport: {decisionDiagnostic} Set {DecisionClientFactory.RequiredVariables}.");
                return ExitCodes.RuntimeError;
            }

            var client = createClient(decisionOptions);
            try
            {
                var judge = new DecisionJudge(client, decisionOptions.Model);
                Console.Error.WriteLine($"✔ Decision-model judge configured — {decisionOptions.ProviderName}, model={decisionOptions.Model} (requested; the provider may serve a different build under this name).");
                return await runCalibration(judge, IdentityOf(decisionOptions));
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// What the calibration report names the decision judge: the provider, and the model requested from it. The
        /// provider may answer with a different build under that name (an alias such as <c>jev-latest</c> moves), so
        /// the model is labelled as requested. Never the key and never the endpoint.
        /// </summary>
        internal static CalibrationJudgeIdentity IdentityOf(SystemOneClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var provider = options.ProviderName switch
            {
                "typesafe" => "TypeSafe",
                "openrouter" => "OpenRouter",
                var other => other,
            };
            return new CalibrationJudgeIdentity(
                $"{provider} decision model (--decisions)",
                $"{options.Model} (requested; the provider may serve a different build under this name)");
        }
    }
}
