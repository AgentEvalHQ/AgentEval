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
var benchPresetOpt = new Option<string?>("--preset") { Description = "Benchmark preset: smoke | standard | audit (default: standard)" };
var benchSubjectOpt = new Option<string?>("--subject") { Description = "Subject name (agent or workflow under evaluation, default: default-agent)" };
var benchRootOpt = new Option<string?>("--root") { Description = "Workspace root path (default: auto-detected)" };
var benchInputOpt = new Option<string?>("--input") { Description = "Agent input text for the evaluation (default: built-in fixture)" };
var benchGdprCmd = new Command("gdpr", "Run the GDPR compliance benchmark");
benchGdprCmd.Add(benchPresetOpt);
benchGdprCmd.Add(benchSubjectOpt);
benchGdprCmd.Add(benchRootOpt);
benchGdprCmd.Add(benchInputOpt);
benchGdprCmd.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    var preset = parseResult.GetValue(benchPresetOpt) ?? "standard";
    var subject = parseResult.GetValue(benchSubjectOpt) ?? "default-agent";
    var root = parseResult.GetValue(benchRootOpt);
    var input = parseResult.GetValue(benchInputOpt);
    return await BenchCommand.RunGdprAsync(preset, subject, root, input);
});
benchCmd.Add(benchGdprCmd);

// ─── compliance ───────────────────────────────────────────────────────────────
var complianceCmd = new Command("compliance", "Compliance reporting commands");

// compliance render — required values validated inside RunAsync
var renderRegulationOpt = new Option<string?>("--regulation") { Description = "Regulation identifier (currently: gdpr)" };
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

// ─── root ─────────────────────────────────────────────────────────────────────
var rootCmd = new RootCommand("AgentEval CLI — output-store lifecycle management");
rootCmd.Add(initCmd);
rootCmd.Add(doctorCmd);
rootCmd.Add(migrateCmd);
rootCmd.Add(benchCmd);
rootCmd.Add(complianceCmd);

return await rootCmd.Parse(args).InvokeAsync();
