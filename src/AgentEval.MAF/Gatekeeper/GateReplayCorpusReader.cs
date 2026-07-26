// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

internal static class GateReplayCorpusReader
{
    private static readonly HashSet<string> HeaderProperties =
        ["schema", "record", "corpusId"];
    private static readonly HashSet<string> RecordProperties =
        ["schema", "record", "corpusId", "id", "call"];
    private static readonly HashSet<string> CallProperties =
    [
        "functionName", "arguments", "agentName", "iteration", "functionCallIndex",
        "functionCount", "isStreaming", "messages",
    ];
    private static readonly HashSet<string> MessageProperties = ["role", "contents"];
    private static readonly HashSet<string> TextProperties = ["kind", "text"];
    private static readonly HashSet<string> FunctionCallProperties = ["kind", "callId", "name", "arguments"];
    private static readonly HashSet<string> FunctionResultProperties = ["kind", "callId", "result"];

    public static async Task<GateReplayCorpus> ReadAsync(
        Stream source,
        GateReplayCorpusLimits limits,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(capacity: Math.Min(limits.MaxTotalBytes, 64 * 1024));
        var chunk = new byte[Math.Min(64 * 1024, limits.MaxTotalBytes)];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limits.MaxTotalBytes)
            {
                throw Format($"corpus exceeds total-size limit {limits.MaxTotalBytes} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length));
        string? corpusId = null;
        var fixtures = new List<GateReplayFixture>();
        var lineNumber = 0;
        var cursor = 0;
        while (cursor <= bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = bytes[cursor..];
            var newline = remaining.IndexOf((byte)'\n');
            var length = newline >= 0 ? newline : remaining.Length;
            var line = remaining[..length];
            cursor += length + (newline >= 0 ? 1 : 0);
            lineNumber++;

            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            if (line.Length > limits.MaxLineBytes)
            {
                throw FormatAt(lineNumber, $"line is {line.Length} bytes; limit is {limits.MaxLineBytes}");
            }

            if (IsBlank(line))
            {
                if (newline < 0)
                {
                    break;
                }

                continue;
            }

            using var document = ParseLine(line, lineNumber, limits);
            var root = document.RootElement;
            ValidateObject(root, lineNumber, corpusId is null ? HeaderProperties : RecordProperties);
            RequireSchema(root, lineNumber);

            if (corpusId is null)
            {
                if (RequireString(root, "record", lineNumber) != "header")
                {
                    throw FormatAt(lineNumber, "first non-blank record is not a header");
                }

                corpusId = RequireString(root, "corpusId", lineNumber);
                ValidateOpaqueId(corpusId, lineNumber, "corpusId");
            }
            else
            {
                if (RequireString(root, "record", lineNumber) != "call")
                {
                    throw FormatAt(lineNumber, "record is not a call");
                }

                var recordCorpusId = RequireString(root, "corpusId", lineNumber);
                if (!string.Equals(corpusId, recordCorpusId, StringComparison.Ordinal))
                {
                    throw FormatAt(lineNumber, "record corpusId does not match the header");
                }

                fixtures.Add(ParseFixture(root, lineNumber, limits));
                if (fixtures.Count > limits.MaxCalls)
                {
                    throw FormatAt(lineNumber, $"call count exceeds limit {limits.MaxCalls}");
                }
            }

            if (newline < 0)
            {
                break;
            }
        }

        if (corpusId is null)
        {
            throw Format("corpus has no header record");
        }

        try
        {
            return new GateReplayCorpus(corpusId, fixtures);
        }
        catch (ArgumentException ex)
        {
            throw new GateReplayCorpusFormatException(
                $"Expected valid corpus identities. Actual: {ex.Message} Suggestions: assign unique opaque ids.", ex);
        }
    }

    private static JsonDocument ParseLine(
        ReadOnlySpan<byte> line,
        int lineNumber,
        GateReplayCorpusLimits limits)
    {
        try
        {
            var document = JsonDocument.Parse(
                line.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaxJsonDepth,
                });
            ValidateNoDuplicateProperties(document.RootElement, lineNumber);
            return document;
        }
        catch (GateReplayCorpusFormatException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new GateReplayCorpusFormatException(
                $"Expected valid JSON on replay-corpus line {lineNumber}. Actual: {ex.Message} " +
                "Suggestions: regenerate the corpus with GateReplayCorpusSerializer.", ex);
        }
    }

    private static GateReplayFixture ParseFixture(
        JsonElement root,
        int lineNumber,
        GateReplayCorpusLimits limits)
    {
        var id = RequireString(root, "id", lineNumber);
        ValidateOpaqueId(id, lineNumber, "id");
        var callElement = Require(root, "call", JsonValueKind.Object, lineNumber);
        ValidateObject(callElement, lineNumber, CallProperties);

        var functionName = RequireString(callElement, "functionName", lineNumber);
        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw FormatAt(lineNumber, "functionName is empty");
        }

        IReadOnlyDictionary<string, object?>? arguments = null;
        var argumentsElement = Require(callElement, "arguments", lineNumber);
        if (argumentsElement.ValueKind != JsonValueKind.Null)
        {
            if (argumentsElement.ValueKind != JsonValueKind.Object)
            {
                throw FormatAt(lineNumber, "arguments is neither an object nor null");
            }

            if (argumentsElement.GetPropertyCount() > limits.MaxArgumentsPerCall)
            {
                throw FormatAt(
                    lineNumber,
                    $"argument count exceeds limit {limits.MaxArgumentsPerCall}");
            }

            arguments = ReadDictionary(argumentsElement);
        }

        var agentElement = Require(callElement, "agentName", lineNumber);
        var agentName = agentElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => agentElement.GetString(),
            _ => throw FormatAt(lineNumber, "agentName is neither a string nor null"),
        };

        var iteration = RequireNonNegativeInt(callElement, "iteration", lineNumber);
        var functionCallIndex = RequireNonNegativeInt(callElement, "functionCallIndex", lineNumber);
        var functionCount = RequireNonNegativeInt(callElement, "functionCount", lineNumber);
        if (functionCount < 1 || functionCallIndex >= functionCount)
        {
            throw FormatAt(lineNumber, "functionCallIndex is outside functionCount");
        }

        var isStreamingElement = Require(callElement, "isStreaming", lineNumber);
        if (isStreamingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw FormatAt(lineNumber, "isStreaming is not a boolean");
        }

        var messages = ReadMessages(
            Require(callElement, "messages", lineNumber),
            lineNumber,
            limits);
        var call = new GatedToolCall(
            functionName,
            arguments,
            agentName,
            iteration,
            functionCallIndex,
            functionCount,
            isStreamingElement.GetBoolean(),
            messages);
        return new GateReplayFixture(id, call);
    }

    private static IReadOnlyList<ChatMessage>? ReadMessages(
        JsonElement element,
        int lineNumber,
        GateReplayCorpusLimits limits)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw FormatAt(lineNumber, "messages is neither an array nor null");
        }

        if (element.GetArrayLength() > limits.MaxMessagesPerCall)
        {
            throw FormatAt(lineNumber, $"message count exceeds limit {limits.MaxMessagesPerCall}");
        }

        var messages = new List<ChatMessage>(element.GetArrayLength());
        foreach (var messageElement in element.EnumerateArray())
        {
            ValidateObject(messageElement, lineNumber, MessageProperties);
            var role = ReadRole(RequireString(messageElement, "role", lineNumber), lineNumber);
            var contentsElement = Require(messageElement, "contents", JsonValueKind.Array, lineNumber);
            if (contentsElement.GetArrayLength() > limits.MaxContentsPerMessage)
            {
                throw FormatAt(lineNumber, $"content count exceeds limit {limits.MaxContentsPerMessage}");
            }

            var contents = new List<AIContent>(contentsElement.GetArrayLength());
            foreach (var contentElement in contentsElement.EnumerateArray())
            {
                contents.Add(ReadContent(contentElement, lineNumber, limits));
            }

            messages.Add(new ChatMessage(role, contents));
        }

        return messages;
    }

    private static AIContent ReadContent(
        JsonElement element,
        int lineNumber,
        GateReplayCorpusLimits limits)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw FormatAt(lineNumber, "message content is not an object");
        }

        var kind = RequireString(element, "kind", lineNumber);
        switch (kind)
        {
            case "text":
                ValidateObject(element, lineNumber, TextProperties);
                return new TextContent(RequireString(element, "text", lineNumber));

            case "functionCall":
            {
                ValidateObject(element, lineNumber, FunctionCallProperties);
                var callId = RequireString(element, "callId", lineNumber);
                var name = RequireString(element, "name", lineNumber);
                if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name))
                {
                    throw FormatAt(lineNumber, "functionCall requires a non-empty callId and name");
                }

                var argumentsElement = Require(element, "arguments", lineNumber);
                IDictionary<string, object?>? arguments = null;
                if (argumentsElement.ValueKind != JsonValueKind.Null)
                {
                    if (argumentsElement.ValueKind != JsonValueKind.Object)
                    {
                        throw FormatAt(lineNumber, "functionCall arguments is neither an object nor null");
                    }

                    if (argumentsElement.GetPropertyCount() > limits.MaxArgumentsPerCall)
                    {
                        throw FormatAt(
                            lineNumber,
                            $"functionCall argument count exceeds limit {limits.MaxArgumentsPerCall}");
                    }

                    arguments = ReadDictionary(argumentsElement);
                }

                return new FunctionCallContent(callId, name, arguments);
            }

            case "functionResult":
                ValidateObject(element, lineNumber, FunctionResultProperties);
                return new FunctionResultContent(
                    RequireString(element, "callId", lineNumber),
                    ReadValue(Require(element, "result", lineNumber)));

            default:
                throw FormatAt(lineNumber, $"unsupported message content kind '{kind}'");
        }
    }

    private static Dictionary<string, object?> ReadDictionary(JsonElement element)
        => element.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => ReadValue(property.Value),
            StringComparer.Ordinal);

    private static object? ReadValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.Clone(),
        };

    private static ChatRole ReadRole(string role, int lineNumber)
        => role switch
        {
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            "system" => ChatRole.System,
            _ => throw FormatAt(lineNumber, $"unsupported chat role '{role}'"),
        };

    private static void RequireSchema(JsonElement root, int lineNumber)
    {
        var schema = RequireString(root, "schema", lineNumber);
        if (schema != GateReplayCorpusSerializer.SchemaVersion)
        {
            throw FormatAt(
                lineNumber,
                $"unsupported schema '{schema}' (expected '{GateReplayCorpusSerializer.SchemaVersion}')");
        }
    }

    private static JsonElement Require(
        JsonElement element,
        string propertyName,
        int lineNumber)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw FormatAt(lineNumber, $"required property '{propertyName}' is missing");
        }

        return property;
    }

    private static JsonElement Require(
        JsonElement element,
        string propertyName,
        JsonValueKind kind,
        int lineNumber)
    {
        var property = Require(element, propertyName, lineNumber);
        if (property.ValueKind != kind)
        {
            throw FormatAt(lineNumber, $"property '{propertyName}' is not a {kind}");
        }

        return property;
    }

    private static string RequireString(JsonElement element, string propertyName, int lineNumber)
    {
        var property = Require(element, propertyName, JsonValueKind.String, lineNumber);
        return property.GetString()!;
    }

    private static int RequireNonNegativeInt(
        JsonElement element,
        string propertyName,
        int lineNumber)
    {
        var property = Require(element, propertyName, JsonValueKind.Number, lineNumber);
        if (!property.TryGetInt32(out var result) || result < 0)
        {
            throw FormatAt(lineNumber, $"property '{propertyName}' is not a non-negative Int32");
        }

        return result;
    }

    private static void ValidateObject(
        JsonElement element,
        int lineNumber,
        IReadOnlySet<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw FormatAt(lineNumber, "record is not a JSON object");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name))
            {
                throw FormatAt(lineNumber, $"unknown property '{property.Name}'");
            }
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element, int lineNumber)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw FormatAt(lineNumber, $"duplicate property '{property.Name}'");
                }

                ValidateNoDuplicateProperties(property.Value, lineNumber);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item, lineNumber);
            }
        }
    }

    private static void ValidateOpaqueId(string value, int lineNumber, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw FormatAt(
                lineNumber,
                $"{propertyName} must be non-empty, at most 256 characters, and contain no control characters");
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var value in line)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }

    private static GateReplayCorpusFormatException Format(string actual)
        => new(
            $"Expected a bounded, strict {GateReplayCorpusSerializer.SchemaVersion} corpus. Actual: {actual}. " +
            "Suggestions: regenerate it with GateReplayCorpusSerializer.");

    private static GateReplayCorpusFormatException FormatAt(int lineNumber, string actual)
        => new(
            $"Expected a bounded, strict {GateReplayCorpusSerializer.SchemaVersion} record at line {lineNumber}. " +
            $"Actual: {actual}. Suggestions: regenerate it with GateReplayCorpusSerializer.");
}
