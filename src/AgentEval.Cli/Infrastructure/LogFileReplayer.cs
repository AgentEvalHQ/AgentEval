// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// <c>agenteval log-file replay</c> — reads a <c>--capture-fixture</c> JSONL capture directly (NOT a
/// <c>to-fixture</c>-generated fixture, which deliberately drops request data — see
/// <see cref="LogFixtureGenerator"/>'s own remarks; a fixture scripts responses, not requests) and resends
/// every captured "response"-kind round-trip INDEPENDENTLY (not chained into a simulated multi-turn session —
/// each captured <c>(messages[], options)</c> pair was already a complete, self-contained request at the
/// moment it was sent, since the standard <see cref="IChatClient"/> contract means the caller resends full
/// history every round) to a target <see cref="IChatClient"/>, diffing STRUCTURALLY/BEHAVIORALLY against the
/// capture — never by exact text (LLM output isn't reproducible even against the literal same model/config at
/// temperature &gt; 0), unless the caller opts into <c>--strict-text</c>.
/// </summary>
public static class LogFileReplayer
{
    /// <summary>Replays every "response"-kind entry in <paramref name="entries"/> against <paramref name="against"/>.</summary>
    public static async Task<ReplayReport> ReplayAsync(
        IReadOnlyList<FixtureCaptureEntry> entries, IChatClient against, bool strictText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(against);

        var rows = new List<ReplayRow>();
        var skipped = 0;

        foreach (var entry in entries)
        {
            if (entry.Kind != "response" || entry.Response is null)
            {
                skipped++;
                continue;
            }

            var messages = BuildMessages(entry.Request);
            var options = BuildOptions(entry.Request);

            var sw = Stopwatch.StartNew();
            ChatResponse? actual = null;
            Exception? error = null;
            try
            {
                actual = await against.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            sw.Stop();
            rows.Add(BuildRow(entry, actual, error, sw.ElapsedMilliseconds, strictText));
        }

        return new ReplayReport(rows, skipped);
    }

    private static List<ChatMessage> BuildMessages(FixtureCaptureRequest request) =>
        request.Messages.Select(m => new ChatMessage(new ChatRole(m.Role), m.Text)).ToList();

    private static ChatOptions? BuildOptions(FixtureCaptureRequest request)
    {
        if (request.Options is null && string.IsNullOrEmpty(request.Instructions))
        {
            return null;
        }

        var options = new ChatOptions
        {
            Instructions = request.Instructions,
            Temperature = request.Options?.Temperature,
            MaxOutputTokens = request.Options?.MaxOutputTokens,
            ModelId = request.Options?.ModelId,
        };

        if (request.Options?.Tools is { Count: > 0 } tools)
        {
            // Declaration-only — the target model needs to SEE the tool definitions to decide whether it
            // would call them (surfacing as FunctionCallContent in the response), but replay calls
            // GetResponseAsync directly (no UseFunctionInvocation), so a tool is never actually invoked.
            options.Tools = tools.Select(t => (AITool)new ReplayToolDeclaration(t.Name, t.Description, t.ParametersSchema)).ToList();
        }

        return options;
    }

    private static ReplayRow BuildRow(
        FixtureCaptureEntry entry, ChatResponse? actual, Exception? error, long actualElapsedMs, bool strictText)
    {
        if (error is not null)
        {
            return new ReplayRow(
                entry.Index,
                ReplayVerdict.Fail,
                $"Replay target threw {error.GetType().Name}",
                new[] { error.Message },
                entry.ElapsedMs,
                actualElapsedMs);
        }

        var response = actual!;
        var details = new List<string>();

        var capturedTools = CapturedToolSignatures(entry.Response!.ToolCalls);
        var actualTools = ActualToolSignatures(response);
        var toolsMatch = capturedTools.SetEquals(actualTools);
        if (!toolsMatch)
        {
            details.Add(
                $"Tool calls differ: captured=[{string.Join(", ", capturedTools.OrderBy(s => s, StringComparer.Ordinal))}] " +
                $"actual=[{string.Join(", ", actualTools.OrderBy(s => s, StringComparer.Ordinal))}]");
        }

        var capturedFinish = entry.Response.FinishReason;
        var actualFinish = response.FinishReason?.Value;
        var finishMatches = string.Equals(capturedFinish, actualFinish, StringComparison.Ordinal);
        if (!finishMatches)
        {
            details.Add($"FinishReason differs: captured={capturedFinish ?? "(none)"} actual={actualFinish ?? "(none)"}");
        }

        var capturedShape = ResponseShape(entry.Response.Text);
        var actualShape = ResponseShape(response.Text);
        var shapeMatches = string.Equals(capturedShape, actualShape, StringComparison.Ordinal);
        if (!shapeMatches)
        {
            details.Add($"Response shape differs: captured={capturedShape} actual={actualShape}");
        }

        // Informational only — never affects the verdict.
        var capturedUsage = entry.Response.Usage;
        details.Add(
            $"Token usage (informational): captured(in={FormatOrUnknown(capturedUsage?.InputTokens)}, " +
            $"out={FormatOrUnknown(capturedUsage?.OutputTokens)}) actual(in={FormatOrUnknown(response.Usage?.InputTokenCount)}, " +
            $"out={FormatOrUnknown(response.Usage?.OutputTokenCount)})");
        details.Add($"Latency (informational): captured={entry.ElapsedMs}ms actual={actualElapsedMs}ms (delta {actualElapsedMs - entry.ElapsedMs}ms)");

        if (strictText)
        {
            var textMatches = string.Equals(entry.Response.Text, response.Text, StringComparison.Ordinal);
            if (!textMatches)
            {
                details.Add(
                    "Exact text differs (--strict-text). Note: even 'deterministic' providers don't formally " +
                    "guarantee bit-identical output over time (model updates, infra changes).");
                return new ReplayRow(entry.Index, ReplayVerdict.Fail, "Exact text mismatch under --strict-text", details, entry.ElapsedMs, actualElapsedMs);
            }
        }

        if (!toolsMatch || !finishMatches)
        {
            return new ReplayRow(entry.Index, ReplayVerdict.Fail, "Tool calls or finish reason diverged", details, entry.ElapsedMs, actualElapsedMs);
        }

        if (!shapeMatches)
        {
            return new ReplayRow(entry.Index, ReplayVerdict.Flag, "Response shape diverged (tool calls / finish reason matched)", details, entry.ElapsedMs, actualElapsedMs);
        }

        return new ReplayRow(entry.Index, ReplayVerdict.Pass, "Matched (tool calls, finish reason, response shape)", details, entry.ElapsedMs, actualElapsedMs);
    }

    private static string FormatOrUnknown(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";

    private static HashSet<string> CapturedToolSignatures(IReadOnlyList<FixtureCaptureToolCall>? calls) =>
        (calls ?? Array.Empty<FixtureCaptureToolCall>())
            .Select(c => ToolSignature(c.Name, c.Arguments))
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ActualToolSignatures(ChatResponse response) =>
        response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => ToolSignature(c.Name, c.Arguments))
            .ToHashSet(StringComparer.Ordinal);

    // Set of (name, sorted arg KEYS) — did the model choose to call the same tools with recognizably the same
    // shape of arguments? Exact argument VALUES are a diff detail elsewhere, never a pass/fail signal — a
    // different but valid phrasing of the same intent is not a regression.
    private static string ToolSignature(string name, IDictionary<string, object?>? args)
    {
        var keys = args is null ? Array.Empty<string>() : args.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return $"{name}({string.Join(",", keys)})";
    }

    private static readonly Regex NumberedListPattern = new(@"(?m)^\s*\d+\.\s", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex BulletListPattern = new(@"(?m)^\s*[-*]\s", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Coarse shape comparison, not a diff library on prose — a length bucket plus presence of key structural
    // markers (numbered list, code block).
    private static string ResponseShape(string? text)
    {
        text ??= string.Empty;
        var bucket = text.Length < 100 ? "short" : text.Length <= 500 ? "medium" : "long";

        var markers = new List<string>();
        try
        {
            if (NumberedListPattern.IsMatch(text) || BulletListPattern.IsMatch(text))
            {
                markers.Add("list");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Adversarial/pathological input — treat as "no marker detected" rather than failing the whole replay.
        }

        if (text.Contains("```", StringComparison.Ordinal))
        {
            markers.Add("code");
        }

        return markers.Count == 0 ? bucket : $"{bucket}+{string.Join('+', markers)}";
    }

    /// <summary>
    /// A schema-only tool declaration reconstructed from a capture — never actually invoked (replay calls
    /// <see cref="IChatClient.GetResponseAsync"/> directly, without <c>UseFunctionInvocation</c>), so there is
    /// no real delegate to reconstruct; only the shape the target model needs to see to decide whether it
    /// WOULD call this tool.
    /// </summary>
    private sealed class ReplayToolDeclaration : AIFunction
    {
        public ReplayToolDeclaration(string name, string? description, JsonElement? schema)
        {
            Name = name;
            Description = description ?? string.Empty;
            JsonSchema = schema ?? default;
        }

        public override string Name { get; }

        public override string Description { get; }

        public override JsonElement JsonSchema { get; }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => throw new NotSupportedException(
                "Replay tool declarations are never invoked — log-file replay calls GetResponseAsync directly, without UseFunctionInvocation.");
    }
}
