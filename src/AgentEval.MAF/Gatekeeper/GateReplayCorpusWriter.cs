// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Gatekeeper;

internal static class GateReplayCorpusWriter
{
    public static async Task WriteAsync(
        Stream destination,
        GateReplayCorpus corpus,
        GateReplayCorpusLimits limits,
        CancellationToken cancellationToken)
    {
        if (corpus.Fixtures.Count > limits.MaxCalls)
        {
            throw Format($"corpus contains {corpus.Fixtures.Count} calls; limit is {limits.MaxCalls}");
        }

        // Validate and buffer the complete payload before touching the destination. A bad record must not
        // leave behind a plausible-looking but truncated corpus.
        var lines = new List<byte[]>(corpus.Fixtures.Count + 1);
        long totalBytes = 0;
        AddLine(Serialize(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema", GateReplayCorpusSerializer.SchemaVersion);
            writer.WriteString("record", "header");
            writer.WriteString("corpusId", corpus.CorpusId);
            writer.WriteEndObject();
        }, limits));

        foreach (var fixture in corpus.Fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddLine(Serialize(writer => WriteFixture(writer, corpus.CorpusId, fixture, limits), limits));
        }

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        void AddLine(byte[] line)
        {
            totalBytes += line.Length + 1L;
            if (totalBytes > limits.MaxTotalBytes)
            {
                throw Format($"serialized corpus is {totalBytes} bytes; limit is {limits.MaxTotalBytes}");
            }

            lines.Add(line);
        }
    }

    private static byte[] Serialize(Action<Utf8JsonWriter> write, GateReplayCorpusLimits limits)
    {
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            write(writer);
            writer.Flush();
        }
        catch (GateReplayCorpusFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new GateReplayCorpusFormatException(
                "Expected JSON-compatible replay data. Actual: serialization failed. " +
                "Suggestions: replace custom argument/result objects with JSON-compatible values.", ex);
        }

        if (buffer.WrittenCount > limits.MaxLineBytes)
        {
            throw Format($"serialized record is {buffer.WrittenCount} bytes; line limit is {limits.MaxLineBytes}");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteFixture(
        Utf8JsonWriter writer,
        string corpusId,
        GateReplayFixture fixture,
        GateReplayCorpusLimits limits)
    {
        var call = fixture.Call;
        if (call.Iteration < 0 || call.FunctionCallIndex < 0 || call.FunctionCount < 1
            || call.FunctionCallIndex >= call.FunctionCount)
        {
            throw Format($"fixture '{fixture.Id}' has invalid non-negative call position metadata");
        }

        if (call.Arguments?.Count > limits.MaxArgumentsPerCall)
        {
            throw Format(
                $"fixture '{fixture.Id}' has {call.Arguments.Count} arguments; limit is {limits.MaxArgumentsPerCall}");
        }

        if (call.Messages?.Count > limits.MaxMessagesPerCall)
        {
            throw Format(
                $"fixture '{fixture.Id}' has {call.Messages.Count} messages; limit is {limits.MaxMessagesPerCall}");
        }

        writer.WriteStartObject();
        writer.WriteString("schema", GateReplayCorpusSerializer.SchemaVersion);
        writer.WriteString("record", "call");
        writer.WriteString("corpusId", corpusId);
        writer.WriteString("id", fixture.Id);
        writer.WritePropertyName("call");
        writer.WriteStartObject();
        writer.WriteString("functionName", call.FunctionName);
        writer.WritePropertyName("arguments");
        WriteDictionary(writer, call.Arguments, limits.MaxJsonDepth);
        if (call.AgentName is null)
        {
            writer.WriteNull("agentName");
        }
        else
        {
            writer.WriteString("agentName", call.AgentName);
        }

        writer.WriteNumber("iteration", call.Iteration);
        writer.WriteNumber("functionCallIndex", call.FunctionCallIndex);
        writer.WriteNumber("functionCount", call.FunctionCount);
        writer.WriteBoolean("isStreaming", call.IsStreaming);
        writer.WritePropertyName("messages");
        WriteMessages(writer, call.Messages, limits);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteMessages(
        Utf8JsonWriter writer,
        IReadOnlyList<ChatMessage>? messages,
        GateReplayCorpusLimits limits)
    {
        if (messages is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var message in messages)
        {
            if (message.Contents.Count > limits.MaxContentsPerMessage)
            {
                throw Format(
                    $"message has {message.Contents.Count} contents; limit is {limits.MaxContentsPerMessage}");
            }

            writer.WriteStartObject();
            writer.WriteString("role", WriteRole(message.Role));
            writer.WritePropertyName("contents");
            writer.WriteStartArray();
            foreach (var content in message.Contents)
            {
                writer.WriteStartObject();
                switch (content)
                {
                    case TextContent text:
                        if (text.Text is null)
                        {
                            throw Format("text content has a null text value");
                        }

                        writer.WriteString("kind", "text");
                        writer.WriteString("text", text.Text);
                        break;

                    case FunctionCallContent functionCall:
                        if (string.IsNullOrWhiteSpace(functionCall.CallId)
                            || string.IsNullOrWhiteSpace(functionCall.Name))
                        {
                            throw Format("function-call content requires a non-empty callId and name");
                        }

                        writer.WriteString("kind", "functionCall");
                        writer.WriteString("callId", functionCall.CallId);
                        writer.WriteString("name", functionCall.Name);
                        writer.WritePropertyName("arguments");
                        WriteDictionary(writer, functionCall.Arguments, limits.MaxJsonDepth);
                        break;

                    case FunctionResultContent functionResult:
                        if (functionResult.CallId is null)
                        {
                            throw Format("function-result content has a null callId");
                        }

                        // Empty call ids are preserved: reducers can strip them and the taint gate has an
                        // intentional adjacency fallback for exactly that history shape.
                        writer.WriteString("kind", "functionResult");
                        writer.WriteString("callId", functionResult.CallId);
                        writer.WritePropertyName("result");
                        WriteCanonicalValue(writer, functionResult.Result, limits.MaxJsonDepth);
                        break;

                    default:
                        throw Format(
                            $"unsupported message content type '{content.GetType().FullName}'; " +
                            "only text, functionCall, and functionResult are losslessly replayable");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string WriteRole(ChatRole role)
    {
        if (role == ChatRole.User) return "user";
        if (role == ChatRole.Assistant) return "assistant";
        if (role == ChatRole.Tool) return "tool";
        if (role == ChatRole.System) return "system";
        throw Format($"unsupported chat role '{role}'");
    }

    private static void WriteDictionary(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<string, object?>>? values,
        int maxDepth)
    {
        if (values is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            WriteCanonicalValue(writer, pair.Value, maxDepth);
        }

        writer.WriteEndObject();
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, object? value, int maxDepth)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var element = value is JsonElement existing
            ? existing
            : JsonSerializer.SerializeToElement(value, GateText.SerializerOptions);
        WriteCanonicalElement(writer, element, depth: 0, maxDepth);
    }

    private static void WriteCanonicalElement(
        Utf8JsonWriter writer,
        JsonElement element,
        int depth,
        int maxDepth)
    {
        if (depth >= maxDepth && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            throw Format($"JSON value exceeds configured depth {maxDepth}");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject()
                    .OrderBy(static property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var i = 1; i < properties.Length; i++)
                {
                    if (string.Equals(properties[i - 1].Name, properties[i].Name, StringComparison.Ordinal))
                    {
                        throw Format($"JSON value contains duplicate property '{properties[i].Name}'");
                    }
                }

                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value, depth + 1, maxDepth);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item, depth + 1, maxDepth);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Format($"unsupported JSON value kind '{element.ValueKind}'");
        }
    }

    private static GateReplayCorpusFormatException Format(string actual)
        => new(
            $"Expected a bounded, lossless {GateReplayCorpusSerializer.SchemaVersion} record. Actual: {actual}. " +
            "Suggestions: sanitize the fixture or raise only a reviewed corpus bound.");
}
