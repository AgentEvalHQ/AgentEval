// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The demo's entry: parses the command line, explains a missing config, creates the run folder, then hands over to
/// <see cref="DemoRun"/> (banner → readiness → one prompt → benchmark → summary). All demo output goes to standard
/// output; standard error carries usage errors (exit 2) and runtime errors (exit 3) only.
/// </summary>
internal static class DemoHost
{
    /// <summary>Runs the demo and returns the exit code (<see cref="DemoExitCodes"/>).</summary>
    /// <param name="args">The command line.</param>
    /// <param name="overrides">Host seams; null for none.</param>
    /// <param name="cancellationToken">Stops the run (Ctrl+C): the stage in progress ends, the rest are not run, the run folder is written.</param>
    internal static async Task<int> RunAsync(string[] args, DemoHostOverrides? overrides = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        overrides ??= new DemoHostOverrides();
        var stdout = overrides.Out ?? Console.Out;
        var stderr = overrides.Err ?? Console.Error;
        var env = overrides.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var now = overrides.Now ?? (() => DateTimeOffset.UtcNow);

        // ── the command line (usage errors exit 2 before anything is created or sent) ──
        var cli = new DemoCommandLine.Definition();
        var parse = cli.Root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                stderr.WriteLine("m365 demo: " + error.Message);
            }

            stderr.WriteLine("Run with --help to see every option.");
            stderr.Flush();
            return DemoExitCodes.Usage;
        }

        if (parse.Action is not null)
        {
            // --help or --version.
            return await parse.InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, cancellationToken).ConfigureAwait(false);
        }

        if (!DemoCommandLine.TryBind(parse, cli, env, out var settings, out var usageError) || settings is null)
        {
            stderr.WriteLine("m365 demo: " + usageError);
            stderr.WriteLine("Run with --help to see every option.");
            stderr.Flush();
            return DemoExitCodes.Usage;
        }

        DemoCaseSet? caseSet = null;
        if (settings.DatasetFile is not null)
        {
            try
            {
                caseSet = DemoDataset.Load(settings.DatasetFile);
            }
            catch (InvalidDataException ex)
            {
                stderr.WriteLine("m365 demo: " + ex.Message);
                stderr.Flush();
                return DemoExitCodes.Usage;
            }
        }

        var colors = overrides.Out is null && !Console.IsOutputRedirected && string.IsNullOrEmpty(env("NO_COLOR"));

        // A redirected screen (a file, a pipe, a CI log) gets ASCII markers: on Windows it is in the legacy code page, where
        // the symbols print as '?'. console.log is UTF-8 whatever the screen.
        var ascii = overrides.AsciiScreen ?? (overrides.Out is null && DemoConsole.NeedsAsciiScreen(Console.IsOutputRedirected, Console.OutputEncoding));
        using var console = new DemoConsole(stdout, stderr, settings.Verbosity, colors, ascii);

        // ── no config: say exactly what is missing and how to create it; nothing is created, nothing is sent ──
        settings.ConfigFile.Refresh();
        if (!settings.ConfigFile.Exists)
        {
            WriteMissingConfig(console, settings);
            return DemoExitCodes.NotMeasured;
        }

        // ── the run folder ──
        RunFolder folder;
        try
        {
            folder = RunFolder.Create(settings.OutDirectory, overrides.RunsRoot, now());
            console.OpenLog(folder.ConsoleLog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            console.Error($"m365 demo: the run folder could not be created ({ex.GetType().Name}: {ex.Message}). Nothing was sent.");
            return DemoExitCodes.Runtime;
        }

        var run = new DemoRun(new DemoContext(settings, overrides, console, folder, env, now), caseSet);
        return await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The "no config file" message: what is missing, where to get it, how to point at an existing one.</summary>
    /// <param name="console">The console.</param>
    /// <param name="settings">The settings.</param>
    internal static void WriteMissingConfig(DemoConsole console, DemoSettings settings)
    {
        var cwd = Directory.GetCurrentDirectory();
        var template = new FileInfo(Path.Combine(cwd, DemoCommandLine.TemplateRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var example = new FileInfo(Path.Combine(cwd, "samples", "AgentEval.Microsoft365Demo", "workiq.example.json"));
        if (!example.Exists)
        {
            example = new FileInfo(Path.Combine(AppContext.BaseDirectory, "workiq.example.json"));
        }

        var text = new StringBuilder(2048);
        text.Append("Microsoft 365 demo: no config file at ").Append(settings.ConfigFile.FullName).Append(" (").Append(settings.ConfigSource).Append(").\n");
        text.Append("Nothing was sent to any tenant and no run folder was created.\n\n");
        text.Append("What is missing: the Work IQ connection file (JSON, no secret in it). It needs two values from your Entra app registration:\n");
        text.Append("  tenantId     the Directory (tenant) ID (the app registration's Overview page)\n");
        text.Append("  appClientId  the Application (client) ID of a public-client app registration (\"Allow public client flows\" on) with the\n");
        text.Append("               delegated permission WorkIQAgent.Ask on Work IQ, admin consent granted\n");
        text.Append("Optional: agentId (null = the default Work IQ agent), agentName, timeZone; leave endpoint, scope and trustedHosts as they are.\n\n");
        text.Append("Create it from the template, then replace both REPLACE-WITH-... values:\n");
        text.Append("  cp ").Append(DemoCommandLine.TemplateRelativePath).Append(" ./").Append(DemoCommandLine.DefaultConfigFileName).Append("               (bash, from the repository root)\n");
        text.Append("  Copy-Item ").Append(DemoCommandLine.TemplateRelativePath).Append(" ./").Append(DemoCommandLine.DefaultConfigFileName).Append("        (PowerShell)\n");
        text.Append("  template: ").Append(template.Exists ? template.FullName : DemoCommandLine.TemplateRelativePath + " (in the repository)").Append('\n');
        if (example.Exists)
        {
            text.Append("  the same template next to this demo: ").Append(example.FullName).Append('\n');
        }

        text.Append("Or point at an existing file: --config <file>, or set ").Append(DemoCommandLine.ConfigPathVariable).Append("=<file>.\n\n");
        text.Append("Then check the connection without sending a prompt:\n");
        text.Append("  ").Append(DemoCommandLine.RunCommand).Append(" --readiness-only\n\n");
        text.Append("Exit 11: nothing failed, nothing was measured.");
        console.Line(DemoVerbosity.Quiet, text.ToString(), Tint.Warn);
    }
}
