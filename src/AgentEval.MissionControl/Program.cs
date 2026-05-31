// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl;
using AgentEval.MissionControl.GraphQL;
using AgentEval.MissionControl.Rest;
using AgentEval.MissionControl.Services;
using AgentEval.Output;

// Plan-08 MC1.7.1: when the CLI invokes `agenteval mc serve`, it shells over
// to McHost.BuildAsync directly — no `dotnet run` needed. The top-level
// statements below are the equivalent path for `dotnet run --project
// src/AgentEval.MissionControl` (still useful for development).

// SEC-14: self-contained container health probe. The Docker HEALTHCHECK invokes
// `dotnet AgentEval.MissionControl.dll --health-check`, which performs a loopback HTTP GET against the
// running server and exits 0 (healthy) / 1 (unhealthy) — so the runtime image no longer needs curl.
// Handled before any web-host setup so the probe process starts and exits quickly.
if (args.Any(a => string.Equals(a, McHealthCheck.Flag, StringComparison.OrdinalIgnoreCase)))
{
    Environment.Exit(await McHealthCheck.RunAsync());
    return;
}

// Honour `--workspace <path>` from the command line on the bare-dotnet-run
// path. The CLI form (`agenteval mc serve --workspace ...`) translates this
// flag to the `AgentEval__Root` env var inside `McServeCommand`; do the same
// here so `dotnet run --project … -- --workspace X` matches CLI behaviour
// instead of silently falling back to the process working directory (which
// `dotnet run --project` sets to the project directory, not the user's shell
// cwd — a real footgun when running from a repo root).
//
// Resolution order for relative paths:
//   1. `PWD` env var (set by bash/zsh/PowerShell — preserves user's shell cwd
//      even when `dotnet run --project` repoints `CurrentDirectory`)
//   2. `Directory.GetCurrentDirectory()` (the process cwd — works for
//      cmd.exe users and absolute paths)
for (var i = 0; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--workspace", StringComparison.OrdinalIgnoreCase))
    {
        var raw = args[i + 1];
        // MNT-03: share the canonicalise+exist rule with the CLI --root validator via
        // WorkspaceRootDiscovery.CanonicaliseExistingDirectory. Relative paths resolve against the shell's
        // PWD (preserved even when `dotnet run --project` repoints CurrentDirectory). This path owns only
        // the --workspace-specific messaging + exit(1) contract (Plan-13 T4.1b item 7 / F-MC-1).
        var basePath = Environment.GetEnvironmentVariable("PWD") ?? Directory.GetCurrentDirectory();
        var (resolved, status, detail) =
            AgentEval.Output.WorkspaceRootDiscovery.CanonicaliseExistingDirectory(raw, basePath);
        if (status == AgentEval.Output.WorkspaceRootDiscovery.PathStatus.Malformed)
        {
            Console.Error.WriteLine($"--workspace path '{raw}' is malformed: {detail}");
            Environment.Exit(1);
            return;
        }
        if (status == AgentEval.Output.WorkspaceRootDiscovery.PathStatus.NotFound)
        {
            Console.Error.WriteLine($"--workspace path '{raw}' (canonicalised to '{detail}') does not exist.");
            Console.Error.WriteLine("    Run `agenteval init` to create a workspace at that path, or point --workspace at an existing one.");
            Environment.Exit(1);
            return;
        }
        Environment.SetEnvironmentVariable("AgentEval__Root", resolved);
        break;
    }
}

var builder = WebApplication.CreateBuilder(args);

// Configure shared services + middleware via the McHost helper. The same
// configuration is used by `agenteval mc serve` (CLI path).
McHost.ConfigureServices(builder);
var app = builder.Build();
McHost.ConfigurePipeline(app);
app.Run();

// Expose Program to WebApplicationFactory<Program> in AgentEval.Tests.
// Required for end-to-end smoke tests of the GraphQL endpoint.
public partial class Program { }
