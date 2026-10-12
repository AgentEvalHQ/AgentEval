// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results;
using AgentEval.Results.Runner;

namespace AgentEval.Cli.Runner;

/// <summary>Runs an AEF dataset plan through the shared bounded engine.</summary>
internal static class RunCommand
{
    internal static Command Create()
    {
        var command = new Command("run", "Execute a bounded AEF dataset plan against configured endpoints.");
        var plan = new Option<string>("--plan") { Required = true, Description = "AEF run-plan JSON file; dataset paths are relative to this file." };
        var root = new Option<string?>("--root") { Description = "Existing workspace root; otherwise discover from the current directory." };
        var id = new Option<string?>("--runner-id") { Description = "Runner ID; defaults to AGENTEVAL_RUNNER_ID, then ~/.agenteval/runner.json." };
        var json = new Option<bool>("--json") { Description = "Print the terminal job report as JSON." };
        foreach (var option in new Option[] { plan, root, id, json }) command.Add(option);
        command.SetAction((ParseResult p, CancellationToken ct) => ExecuteAsync(p.GetValue(plan)!, p.GetValue(root),
            p.GetValue(id), p.GetValue(json), Console.Out, Console.Error, ct));
        return command;
    }

    internal static async Task<int> ExecuteAsync(string planPath, string? root, string? runnerId, bool json,
        TextWriter output, TextWriter error, CancellationToken ct = default,
        Func<string, string?>? environment = null, string? home = null)
    {
        byte[] bytes;
        string planDirectory, workspace;
        JsonObject manifest;
        AefDatasetJobTarget target;
        try
        {
            var fullPath = Path.GetFullPath(planPath);
            planDirectory = Path.GetDirectoryName(fullPath)!;
            bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            workspace = root is null ? WorkspaceRootDiscovery.Find(Environment.CurrentDirectory) ?? Environment.CurrentDirectory : Path.GetFullPath(root);
            if (!Directory.Exists(workspace)) throw new AefInputException("The workspace root must be an existing directory.");
            manifest = RunnerManifest.Build(runnerId, environment, home);
            target = await AefDatasetJobTarget.LoadAsync(bytes, planDirectory, ct).ConfigureAwait(false);
        }
        catch (AefInputException ex) { error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { error.WriteLine("The plan or workspace could not be read."); return 2; }
        catch (OperationCanceledException) { error.WriteLine("The job was cancelled before execution."); return 3; }

        try
        {
            var result = await AefJobRunner.RunWorkspaceAsync(bytes, manifest, target,
                Path.Combine(workspace, ".agenteval", "aef", "jobs"), new AefJobOptions
                {
                    At = DateTimeOffset.UtcNow,
                    Environment = environment ?? Environment.GetEnvironmentVariable,
                }, ct).ConfigureAwait(false);
            var report = new JsonObject
            {
                ["jobId"] = result.JobId, ["terminal"] = result.Terminal,
                ["runs"] = new JsonArray(result.Runs.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            };
            if (result.Limit is not null) report["limit"] = result.Limit;
            if (result.Reason is not null) report["reason"] = result.Reason;
            output.WriteLine(json ? report.ToJsonString() : $"{result.JobId}: {result.Terminal}; {result.Runs.Count} sealed run(s)." +
                (result.Reason is null ? "" : $" {result.Reason}"));
            return ExitCode(result);
        }
        catch (AefInputException ex) { error.WriteLine(ex.Message); return 2; }
        catch (OperationCanceledException) { error.WriteLine("The job was cancelled."); return 3; }
        catch (Exception) { error.WriteLine("The job failed; inspect any retained evidence in the workspace."); return 3; }
    }

    internal static int ExitCode(AefJobResult result) => result.Terminal == "job.sealed" ? 0
        : result.Terminal == "job.failed" && result.Limit == "maxUsd" ? 8
        : result.Terminal == "job.failed" && result.Limit is "cases" or "timeout" ? 11 : 3;
}
