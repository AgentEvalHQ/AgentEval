// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// One JSONL line written by <see cref="FixtureCapturingChatClient"/> — one COMPLETE round-trip (request +
/// outcome together), unlike <c>VerboseLoggingChatClient</c>'s human-readable log (which writes a REQUEST
/// entry and a separate RESPONSE/ERROR/ABANDONED entry). A fixture consumer (<c>log-file to-fixture</c>)
/// needs each entry to be self-contained, not paired by matching index across two lines.
/// </summary>
/// <param name="Index">0-based round-trip index for this client instance (matches <c>VerboseLoggingChatClient</c>'s numbering scheme).</param>
/// <param name="Label">The optional role label passed to the wrapping call (e.g. "sut"/"judge"/"attacker").</param>
/// <param name="Kind">One of "response" / "error" / "abandoned" — mirrors <c>VerboseLoggingChatClient</c>'s three outcome kinds.</param>
/// <param name="TimestampUtc">When this round-trip completed.</param>
/// <param name="ElapsedMs">Wall-clock duration of the round-trip.</param>
/// <param name="Request">The exact request sent (full message array — the whole point of this capture, unlike the lossy human-readable log).</param>
/// <param name="Response">The response received; <see langword="null"/> for "error"/"abandoned".</param>
/// <param name="Error">The full exception text (<c>Exception.ToString()</c>); <see langword="null"/> except for "error".</param>
public sealed record FixtureCaptureEntry(
    int Index,
    string? Label,
    string Kind,
    DateTimeOffset TimestampUtc,
    long ElapsedMs,
    FixtureCaptureRequest Request,
    FixtureCaptureResponse? Response,
    string? Error);

/// <summary>The exact request sent on one round-trip.</summary>
public sealed record FixtureCaptureRequest(
    string? Instructions,
    FixtureCaptureOptions? Options,
    IReadOnlyList<FixtureCaptureMessage> Messages);

/// <summary>The subset of <see cref="Microsoft.Extensions.AI.ChatOptions"/> relevant to replay/fixture generation.</summary>
public sealed record FixtureCaptureOptions(
    float? Temperature,
    int? MaxOutputTokens,
    string? ModelId,
    IReadOnlyList<FixtureCaptureTool>? Tools);

/// <summary>One tool definition offered on the request.</summary>
public sealed record FixtureCaptureTool(string Name, string? Description, JsonElement? ParametersSchema);

/// <summary>One message in the request's full history.</summary>
public sealed record FixtureCaptureMessage(string Role, string? Text);

/// <summary>The response received (only populated when <see cref="FixtureCaptureEntry.Kind"/> is "response").</summary>
public sealed record FixtureCaptureResponse(
    string? Text,
    IReadOnlyList<FixtureCaptureToolCall>? ToolCalls,
    string? FinishReason,
    FixtureCaptureUsage? Usage);

/// <summary>One tool call the model emitted.</summary>
public sealed record FixtureCaptureToolCall(string? CallId, string Name, IDictionary<string, object?>? Arguments);

/// <summary>Token usage, when the response reported any.</summary>
public sealed record FixtureCaptureUsage(long? InputTokens, long? OutputTokens);
