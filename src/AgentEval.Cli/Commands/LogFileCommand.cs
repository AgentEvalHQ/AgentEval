// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Testing;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The <c>agenteval log-file</c> command group — <c>to-fixture</c> turns a <c>--capture-fixture</c> JSONL
/// capture into a deterministic, versionable test fixture (see <see cref="LogFixtureGenerator"/>);
/// <c>replay</c> resends every captured round-trip to a different target and diffs structurally/behaviorally
/// (see <see cref="LogFileReplayer"/>).
/// </summary>
internal static class LogFileCommand
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Create()
    {
        var logFileCmd = new Command("log-file", "Utilities for working with --capture-fixture JSONL captures.");
        logFileCmd.Subcommands.Add(BuildToFixtureCommand());
        logFileCmd.Subcommands.Add(BuildReplayCommand());
        return logFileCmd;
    }

    private static Command BuildToFixtureCommand()
    {
        var toFixtureCmd = new Command(
            "to-fixture",
            "Turn a --capture-fixture JSONL capture into a ScriptedFixtureTurn[] JSON array — a deterministic, " +
            "versionable test fixture (load it with ScriptedChatClient.FromFixture).");

        var capturedArg = new Argument<FileInfo>("captured")
            { Description = "Path to a JSONL file written by --capture-fixture." };
        var outOpt = new Option<FileInfo>("--out") { Required = true, Description = "Where to write the generated fixture JSON array." };

        toFixtureCmd.Arguments.Add(capturedArg);
        toFixtureCmd.Options.Add(outOpt);

        toFixtureCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await ToFixtureAsync(parseResult.GetValue(capturedArg)!, parseResult.GetValue(outOpt)!, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        return toFixtureCmd;
    }

    internal static async Task<int> ToFixtureAsync(FileInfo captured, FileInfo output, CancellationToken ct)
    {
        if (!captured.Exists)
        {
            Console.Error.WriteLine($"  Error: capture file not found: {captured.FullName}");
            return ExitCodes.RuntimeError;
        }

        var turns = await LogFixtureGenerator.GenerateAsync(captured.FullName, ct).ConfigureAwait(false);

        var parentDir = output.Directory;
        if (parentDir is not null && !parentDir.Exists)
        {
            parentDir.Create();
        }

        var json = JsonSerializer.Serialize(turns, OutputJsonOptions);
        await File.WriteAllTextAsync(output.FullName, json, ct).ConfigureAwait(false);

        Console.WriteLine($"  Fixture written: {output.FullName} ({turns.Count} turn(s))");
        return ExitCodes.Success;
    }

    /// <summary>Loads a fixture JSON array previously written by <see cref="ToFixtureAsync"/>.</summary>
    internal static async Task<IReadOnlyList<ScriptedFixtureTurn>> LoadFixtureAsync(string path, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IReadOnlyList<ScriptedFixtureTurn>>(json)
            ?? throw new InvalidOperationException($"Fixture JSON at '{path}' deserialized to null.");
    }

    // ═══════════════════════════════ replay ═══════════════════════════════

    private static Command BuildReplayCommand()
    {
        var replayCmd = new Command(
            "replay",
            "Resend every captured round-trip in a --capture-fixture JSONL file to a different target and diff " +
            "structurally/behaviorally (tool calls, finish reason, response shape) — never by exact text unless " +
            "--strict-text is passed. Reads the RAW capture directly, not a to-fixture-generated fixture (which " +
            "deliberately drops request data).");

        var capturedArg = new Argument<FileInfo>("captured")
            { Description = "Path to a JSONL file written by --capture-fixture." };
        var outOpt = new Option<FileInfo>("--out") { Required = true, Description = "Where to write the Markdown replay report." };
        var endpointOpt = new Option<string?>("--endpoint") { Description = "OpenAI-compatible endpoint URL to replay against." };
        var modelOpt = new Option<string?>("--model") { Description = "Model/deployment name (required with --endpoint)." };
        var apiKeyOpt = new Option<string?>("--api-key") { Description = "API key for --endpoint. Falls back to OPENAI_API_KEY." };
        var azureFromEnvOpt = new Option<bool>("--azure-from-env")
            { Description = "Replay against Azure OpenAI, configured via AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT." };
        var strictTextOpt = new Option<bool>("--strict-text")
            { Description = "Also compare exact response text (in addition to the structural comparison). Off by default — LLM output isn't reproducible even against the literal same model/config at temperature > 0." };

        replayCmd.Arguments.Add(capturedArg);
        replayCmd.Options.Add(outOpt);
        replayCmd.Options.Add(endpointOpt);
        replayCmd.Options.Add(modelOpt);
        replayCmd.Options.Add(apiKeyOpt);
        replayCmd.Options.Add(azureFromEnvOpt);
        replayCmd.Options.Add(strictTextOpt);

        replayCmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await ReplayAsync(
                    parseResult.GetValue(capturedArg)!,
                    parseResult.GetValue(outOpt)!,
                    parseResult.GetValue(endpointOpt),
                    parseResult.GetValue(modelOpt),
                    parseResult.GetValue(apiKeyOpt),
                    parseResult.GetValue(azureFromEnvOpt),
                    parseResult.GetValue(strictTextOpt),
                    ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        return replayCmd;
    }

    internal static async Task<int> ReplayAsync(
        FileInfo captured, FileInfo output, string? endpoint, string? model, string? apiKey, bool azureFromEnv, bool strictText, CancellationToken ct)
    {
        if (!captured.Exists)
        {
            Console.Error.WriteLine($"  Error: capture file not found: {captured.FullName}");
            return ExitCodes.RuntimeError;
        }

        var (against, againstLabel, resolveError) = ResolveAgainst(endpoint, model, apiKey, azureFromEnv);
        if (against is null)
        {
            Console.Error.WriteLine($"  Error: {resolveError}");
            return ExitCodes.UsageError;
        }

        var entries = await LoadCapturedEntriesAsync(captured.FullName, ct).ConfigureAwait(false);
        var report = await LogFileReplayer.ReplayAsync(entries, against, strictText, ct).ConfigureAwait(false);

        var rendered = ReplayReportRenderer.Render(report, captured.FullName, againstLabel);
        var parentDir = output.Directory;
        if (parentDir is not null && !parentDir.Exists)
        {
            parentDir.Create();
        }

        await File.WriteAllTextAsync(output.FullName, rendered, ct).ConfigureAwait(false);
        Console.WriteLine(rendered);
        Console.Error.WriteLine($"  Report written to: {output.FullName}");

        return report.FailCount > 0 ? ExitCodes.TestFailure : ExitCodes.Success;
    }

    private static (IChatClient? Client, string Label, string? Error) ResolveAgainst(
        string? endpoint, string? model, string? apiKey, bool azureFromEnv)
    {
        if (azureFromEnv)
        {
            var (chatClient, deployment, exitCode) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
            return exitCode == ExitCodes.Success
                ? (chatClient, $"azure:{deployment}", null)
                : (null, string.Empty, "Azure OpenAI env vars not configured — see the message above for what's missing.");
        }

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return (null, string.Empty, "--model is required when using --endpoint.");
            }

            // Reuses the SAME EndpointFactory.CreateOpenAICompatible helper BenchTier1SutResolver itself
            // calls for the identical --endpoint/--model/--api-key resolution problem every bench subcommand
            // already solves — not new endpoint-resolution code. A raw IChatClient (not an IEvaluableAgent,
            // which BenchTier1SutResolver returns) is what replay actually needs: it resends the exact
            // captured message array + options, which IEvaluableAgent.InvokeAsync(string prompt) cannot carry.
            return (EndpointFactory.CreateOpenAICompatible(endpoint, model, apiKey), $"{endpoint} ({model})", null);
        }

        return (null, string.Empty, "log-file replay needs a target: pass --azure-from-env, or --endpoint/--model.");
    }

    private static async Task<IReadOnlyList<FixtureCaptureEntry>> LoadCapturedEntriesAsync(string path, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        var entries = new List<FixtureCaptureEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            entries.Add(JsonSerializer.Deserialize<FixtureCaptureEntry>(line)
                ?? throw new InvalidOperationException($"Captured line deserialized to null: {line}"));
        }

        return entries;
    }
}
