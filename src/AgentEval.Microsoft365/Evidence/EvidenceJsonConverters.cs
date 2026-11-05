// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// Serializes <see cref="ImmutableArray{T}"/> as a JSON array; a default (uninitialised) array writes <c>[]</c>
/// instead of throwing, and <c>null</c> reads back as the empty array.
/// </summary>
internal sealed class ImmutableArrayJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter?)Activator.CreateInstance(typeof(ImmutableArrayJsonConverter<>).MakeGenericType(elementType));
    }
}

internal sealed class ImmutableArrayJsonConverter<T> : JsonConverter<ImmutableArray<T>>
{
    public override bool HandleNull => true;

    public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return ImmutableArray<T>.Empty;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected a JSON array for ImmutableArray<{typeof(T).Name}> but found {reader.TokenType}.");
        }

        var builder = ImmutableArray.CreateBuilder<T>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return builder.ToImmutable();
            }

            builder.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
        }

        throw new JsonException("Unterminated JSON array.");
    }

    public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (!value.IsDefault)
        {
            foreach (var item in value)
            {
                JsonSerializer.Serialize(writer, item, options);
            }
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// Serializes <see cref="Observed{T}"/> so that <c>value</c> appears only when <see cref="Observed{T}.State"/> is
/// <see cref="FieldState.Present"/>: a missing, null, invalid or redacted field never shows a substitute value in
/// the document.
/// </summary>
internal sealed class ObservedJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Observed<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter?)Activator.CreateInstance(typeof(ObservedJsonConverter<>).MakeGenericType(valueType));
    }
}

internal sealed class ObservedJsonConverter<T> : JsonConverter<Observed<T>>
{
    private const string StateName = "State";
    private const string ValueName = "Value";
    private const string SourcesName = "Sources";
    private const string DerivationName = "Derivation";
    private const string DerivationRuleIdName = "DerivationRuleId";

    public override Observed<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a JSON object for Observed<{typeof(T).Name}> but found {reader.TokenType}.");
        }

        FieldState? state = null;
        T? value = default;
        var hasValue = false;
        var sources = ImmutableArray<SourceRef>.Empty;
        var derivation = EvidenceDerivation.ProviderValue;
        string? ruleId = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = reader.GetString() ?? string.Empty;
            reader.Read();

            if (Is(name, StateName))
            {
                state = JsonSerializer.Deserialize<FieldState>(ref reader, options);
            }
            else if (Is(name, ValueName))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    hasValue = false;
                }
                else
                {
                    value = JsonSerializer.Deserialize<T>(ref reader, options);
                    hasValue = true;
                }
            }
            else if (Is(name, SourcesName))
            {
                sources = JsonSerializer.Deserialize<ImmutableArray<SourceRef>>(ref reader, options);
            }
            else if (Is(name, DerivationName))
            {
                derivation = JsonSerializer.Deserialize<EvidenceDerivation>(ref reader, options);
            }
            else if (Is(name, DerivationRuleIdName))
            {
                ruleId = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        if (state is null)
        {
            throw new JsonException($"Observed<{typeof(T).Name}> is missing its 'state'.");
        }

        if (state == FieldState.Present && !hasValue)
        {
            throw new JsonException($"Observed<{typeof(T).Name}> is Present but carries no value.");
        }

        return new Observed<T>(state.Value, state == FieldState.Present ? value : default, sources, derivation, ruleId);
    }

    public override void Write(Utf8JsonWriter writer, Observed<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName(Name(StateName, options));
        JsonSerializer.Serialize(writer, value.State, options);

        if (value.State == FieldState.Present)
        {
            writer.WritePropertyName(Name(ValueName, options));
            JsonSerializer.Serialize(writer, value.Value, options);
        }

        writer.WritePropertyName(Name(SourcesName, options));
        JsonSerializer.Serialize(writer, value.Sources, options);

        writer.WritePropertyName(Name(DerivationName, options));
        JsonSerializer.Serialize(writer, value.Derivation, options);

        writer.WritePropertyName(Name(DerivationRuleIdName, options));
        if (value.DerivationRuleId is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.DerivationRuleId);
        }

        writer.WriteEndObject();
    }

    private static string Name(string clrName, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(clrName) ?? clrName;

    private static bool Is(string jsonName, string clrName) =>
        string.Equals(jsonName, clrName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads every <see cref="JsonElement"/> as a detached clone (never backed by a disposable document) and writes an
/// undefined element as <c>null</c> instead of throwing.
/// </summary>
internal sealed class DetachedJsonElementConverter : JsonConverter<JsonElement>
{
    public override bool HandleNull => true;

    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        // JsonElement.ParseValue does not use pooled buffers, so the returned element is already detached.
        return JsonElement.ParseValue(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteTo(writer);
    }
}
