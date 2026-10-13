// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Json;
using AgentEval.Results.Runner;
using AgentEval.Results.Schemas;

namespace AgentEval.Cli.Runner;

/// <summary>The local runner's explicit identity and schema-checked capabilities.</summary>
internal static class RunnerManifest
{
    internal static JsonObject Build(string? runnerId = null, Func<string, string?>? environment = null, string? home = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".agenteval", "runner.json");
        JsonObject config = new();
        try
        {
            if (File.Exists(path)) config = AefJsonReader.ParseDocument(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AefReadException)
        { throw new AefInputException("The runner configuration could not be read as I-JSON."); }
        if (config.Any(p => p.Key is not ("runnerId" or "tags")))
            throw new AefInputException("Runner configuration supports runnerId and tags only.");
        runnerId ??= environment("AGENTEVAL_RUNNER_ID");
        if (runnerId is null && config["runnerId"] is JsonValue value && value.TryGetValue<string>(out var configured))
            runnerId = configured;
        if (string.IsNullOrWhiteSpace(runnerId))
            throw new AefInputException("Set --runner-id, AGENTEVAL_RUNNER_ID, or runnerId in ~/.agenteval/runner.json.");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0", ["runnerId"] = runnerId, ["kind"] = "local",
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            ["runtime"] = new JsonObject { ["name"] = "dotnet", ["version"] = Environment.Version.ToString() },
            ["providers"] = new JsonArray("local"), ["tags"] = config["tags"]?.DeepClone() ?? new JsonArray(),
            ["targetModes"] = new JsonArray("live"), ["version"] = AefScriptedRunner.DefaultProducer.Version,
        };
        if (!AefSchemas.Writer.IsValid("runner", manifest))
            throw new AefInputException("The runner identity or tags do not satisfy the AEF writer schema.");
        return manifest;
    }

    internal static Command Create()
    {
        var runner = new Command("runner", "Inspect the local AEF runner.");
        var manifest = new Command("manifest", "Show the local runner's explicit identity and capabilities.");
        var id = new Option<string?>("--runner-id") { Description = "Runner ID; defaults to AGENTEVAL_RUNNER_ID, then ~/.agenteval/runner.json." };
        var json = new Option<bool>("--json") { Description = "Print the AEF runner manifest as JSON." };
        manifest.Add(id); manifest.Add(json);
        manifest.SetAction(parse => Print(parse.GetValue(id), parse.GetValue(json), Console.Out, Console.Error));
        runner.Add(manifest);
        return runner;
    }

    internal static int Print(string? id, bool json, TextWriter output, TextWriter error,
        Func<string, string?>? environment = null, string? home = null)
    {
        try
        {
            var manifest = Build(id, environment, home);
            output.WriteLine(json ? manifest.ToJsonString() :
                $"Runner {manifest["runnerId"]} · local · {manifest["os"]} · .NET {Environment.Version}\nProvider: local; target mode: live; isolation: process\nTags: {manifest["tags"]!.ToJsonString()}");
            return 0;
        }
        catch (AefInputException ex) { error.WriteLine(ex.Message); return 2; }
    }
}
