// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Testing;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// <c>agenteval log-file to-fixture</c> — reads a <c>--capture-fixture</c> JSONL file (see
/// <see cref="FixtureCapturingChatClient"/>/<see cref="FixtureCaptureEntry"/>) and emits an array of
/// <see cref="ScriptedFixtureTurn"/>, ready for <see cref="ScriptedChatClient.FromFixture"/> or to be embedded
/// literally in a test's <c>fixture.json</c>. A fixture scripts RESPONSES, not requests — the captured
/// request is what you'd send TO the fixture in a test, not part of it, so everything except each entry's
/// <see cref="FixtureCaptureEntry.Response"/> is dropped.
/// </summary>
public static class LogFixtureGenerator
{
    /// <summary>Reads every line of <paramref name="capturedJsonlPath"/> and maps it to a fixture turn.</summary>
    public static async Task<IReadOnlyList<ScriptedFixtureTurn>> GenerateAsync(
        string capturedJsonlPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturedJsonlPath);

        var lines = await File.ReadAllLinesAsync(capturedJsonlPath, cancellationToken).ConfigureAwait(false);
        var turns = new List<ScriptedFixtureTurn>();
        var skippedAbandoned = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<FixtureCaptureEntry>(line)
                ?? throw new InvalidOperationException($"Captured fixture line deserialized to null: {line}");

            switch (entry.Kind)
            {
                case "response":
                    if (entry.Response is null)
                    {
                        throw new InvalidOperationException($"'response'-kind entry at index {entry.Index} has no Response.");
                    }

                    turns.Add(MapResponse(entry.Response));
                    break;
                case "error":
                    turns.Add(new ScriptedFixtureTurn { Throw = ExtractMessage(entry.Error) });
                    break;
                case "abandoned":
                    // No scripted-fixture equivalent for "the stream was abandoned mid-flight" — not silently
                    // dropped without disclosure, see the summary note below.
                    skippedAbandoned++;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown captured entry kind '{entry.Kind}' at index {entry.Index}.");
            }
        }

        if (skippedAbandoned > 0)
        {
            Console.Error.WriteLine(
                $"  Note: {skippedAbandoned} 'abandoned' entr{(skippedAbandoned == 1 ? "y" : "ies")} skipped — " +
                "no scripted-fixture equivalent for a stream abandoned mid-flight.");
        }

        return turns;
    }

    private static ScriptedFixtureTurn MapResponse(FixtureCaptureResponse response)
    {
        var usage = response.Usage;

        if (response.ToolCalls is { Count: >= 2 })
        {
            return new ScriptedFixtureTurn
            {
                ToolCalls = response.ToolCalls
                    .Select(c => new ScriptedFixtureToolCall { ToolCallId = c.CallId, ToolName = c.Name, ToolArgs = c.Arguments })
                    .ToList(),
                FinishReason = response.FinishReason,
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
            };
        }

        if (response.ToolCalls is { Count: 1 })
        {
            var call = response.ToolCalls[0];
            return new ScriptedFixtureTurn
            {
                ToolCallId = call.CallId,
                ToolName = call.Name,
                ToolArgs = call.Arguments,
                FinishReason = response.FinishReason,
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
            };
        }

        return new ScriptedFixtureTurn
        {
            Text = response.Text,
            FinishReason = response.FinishReason,
            InputTokens = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens,
        };
    }

    // A fixture throw doesn't need to reproduce a real stack trace — the first line of a captured
    // Exception.ToString() is conventionally "{ExceptionType}: {Message}", a reasonable message-only summary
    // without this generator needing the capture format to separately carry ex.Message alongside the full text.
    private static string ExtractMessage(string? fullExceptionText) =>
        string.IsNullOrEmpty(fullExceptionText) ? "Simulated provider error" : fullExceptionText.Split('\n')[0].TrimEnd('\r');
}
