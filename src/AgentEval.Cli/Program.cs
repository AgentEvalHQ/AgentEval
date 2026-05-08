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

// ─── root ─────────────────────────────────────────────────────────────────────
var rootCmd = new RootCommand("AgentEval CLI — output-store lifecycle management");
rootCmd.Add(initCmd);
rootCmd.Add(doctorCmd);
rootCmd.Add(migrateCmd);

return await rootCmd.Parse(args).InvokeAsync();
