// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results;
using AgentEval.Results.Json;
using AgentEval.Results.Runner;

namespace AgentEval.Cli.Runner;

/// <summary>Runs an AEF dataset plan through the shared bounded engine.</summary>
internal static class RunCommand
{
    internal static bool IsDryRun(ParseResult parsed) => parsed.CommandResult.Command.Options.OfType<Option<bool>>()
        .Any(option => option.Name.TrimStart('-') == "dry-run" && parsed.GetValue(option));

    internal static Command Create()
    {
        var command = new Command("run", "Execute a bounded AEF dataset plan against configured endpoints.");
        var plan = new Option<string>("--plan") { Required = true, Description = "AEF run-plan JSON file; dataset paths are relative to this file." };
        var root = new Option<string?>("--root") { Description = "Existing workspace root; otherwise discover from the current directory." };
        var id = new Option<string?>("--runner-id") { Description = "Runner ID; defaults to AGENTEVAL_RUNNER_ID, then ~/.agenteval/runner.json." };
        var json = new Option<bool>("--json") { Description = "Print the terminal job report as JSON." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Resolve and estimate without requests, events or file writes." };
        var progress = new Option<string?>("--progress") { Description = "Use ndjson to stream exactly the persisted job events to stdout." };
        var maxUsd = new Option<double?>("--max-usd") { Description = "Additional finite non-negative cost cap; can only lower the plan's cap." };
        var refuse = new Option<bool>("--refuse-over-budget") { Description = "Refuse before acceptance if the full upper estimate exceeds the effective cap." };
        foreach (var option in new Option[] { plan, root, id, json, dryRun, progress, maxUsd, refuse }) command.Add(option);
        command.SetAction((ParseResult p, CancellationToken ct) => ExecuteAsync(p.GetValue(plan)!, p.GetValue(root),
            p.GetValue(id), p.GetValue(json), Console.Out, Console.Error, ct,
            dryRun: p.GetValue(dryRun), progress: p.GetValue(progress), maxUsd: p.GetValue(maxUsd), refuseOverBudget: p.GetValue(refuse)));
        return command;
    }

    internal static async Task<int> ExecuteAsync(string planPath, string? root, string? runnerId, bool json,
        TextWriter output, TextWriter error, CancellationToken ct = default,
        Func<string, string?>? environment = null, string? home = null, bool dryRun = false,
        string? progress = null, double? maxUsd = null, bool refuseOverBudget = false, Stream? progressStream = null)
    {
        if ((progress is not null && (progress != "ndjson" || json || dryRun))
            || (maxUsd is { } cap && (!double.IsFinite(cap) || cap < 0)))
        {
            error.WriteLine("Use --progress ndjson without --json or --dry-run; --max-usd must be finite and non-negative.");
            return 2;
        }
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
            using var ownedProgress = progress == "ndjson" && progressStream is null ? Console.OpenStandardOutput() : null;
            var options = new AefJobOptions
            {
                At = DateTimeOffset.UtcNow,
                Environment = environment ?? Environment.GetEnvironmentVariable,
                MaxUsd = maxUsd, RefuseOverBudget = refuseOverBudget,
                Progress = progress == "ndjson" ? progressStream ?? ownedProgress : null,
            };
            if (dryRun)
            {
                var analysis = AefJobRunner.Analyze(bytes, manifest, target, options);
                var description = analysis.Estimate is { } estimate ? target.DescribeEstimate(estimate) : new JsonObject();
                description["accepted"] = analysis.Accepted;
                if (analysis.Reason is not null) description["reason"] = analysis.Reason;
                if (analysis.Accepted)
                {
                    var planCap = AefJsonReader.ParseDocument(bytes)["limits"]!["maxUsd"]!.GetValue<double>();
                    description["effectiveMaxUsd"] = Math.Min(planCap, maxUsd ?? double.MaxValue);
                }
                output.WriteLine(json ? description.ToJsonString() : analysis.Accepted
                    ? $"Plan accepted: {analysis.Estimate!.Cases} case(s). {description.ToJsonString()}"
                    : $"Plan refused: {analysis.Reason}");
                return analysis.Accepted ? 0 : 3;
            }
            var result = await AefJobRunner.RunWorkspaceAsync(bytes, manifest, target,
                Path.Combine(workspace, ".agenteval", "aef", "jobs"), options, ct).ConfigureAwait(false);
            if (progress == "ndjson") return ExitCode(result);
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
