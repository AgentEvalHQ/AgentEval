// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The <c>agenteval log-file</c> command group — turns a <c>--capture-fixture</c> JSONL capture into a
/// deterministic, versionable test fixture. See <see cref="LogFixtureGenerator"/> for the mapping logic this
/// wraps. <c>replay</c> (comparing a fixture against a live target) is a separate, not-yet-built increment.
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
}
