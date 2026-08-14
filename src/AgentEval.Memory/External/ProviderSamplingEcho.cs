// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External;

/// <summary>
/// Recovers sampling parameters a provider echoed back on a response, when it echoed any.
/// </summary>
/// <remarks>
/// <para>
/// A provider that ignores a seed answers exactly like one that used it, so "the call succeeded"
/// is not evidence that the parameter was honoured. Some providers do echo the effective sampling
/// parameters back — in the loosely-typed <see cref="ChatResponse.AdditionalProperties"/> bag, or on
/// the provider-specific <see cref="ChatResponse.RawRepresentation"/> object. That echo is the only
/// observation available that separates "received" from "dropped", so it is read where present and
/// reported as its own disposition rather than being folded into "applied".
/// </para>
/// <para>
/// Absence is reported as null and never inferred. Nothing here fails a run: a provider object whose
/// getter throws, or a value in an unexpected shape, yields null.
/// </para>
/// </remarks>
internal static class ProviderSamplingEcho
{
    /// <summary>Keys checked for an echoed seed, in order.</summary>
    internal static readonly string[] SeedKeys = ["seed", "Seed", "sampling_seed"];

    /// <summary>Keys checked for an echoed temperature, in order.</summary>
    internal static readonly string[] TemperatureKeys = ["temperature", "Temperature"];

    /// <summary>Reads an echoed seed from a chat response, or null when none was echoed.</summary>
    internal static int? SeedFromChatResponse(ChatResponse? response)
        => response is null
            ? null
            : SeedFromProperties(response.AdditionalProperties)
                ?? ReadFromRaw(response.RawRepresentation, SeedKeys, ToInt32);

    /// <summary>Reads an echoed temperature from a chat response, or null when none was echoed.</summary>
    internal static double? TemperatureFromChatResponse(ChatResponse? response)
        => response is null
            ? null
            : TemperatureFromProperties(response.AdditionalProperties)
                ?? ReadFromRaw(response.RawRepresentation, TemperatureKeys, ToDouble);

    /// <summary>Reads an echoed seed from an agent's property bag, or null when none was echoed.</summary>
    internal static int? SeedFromProperties(IReadOnlyDictionary<string, object?>? properties)
        => ReadFromProperties(properties, SeedKeys, ToInt32);

    /// <summary>Reads an echoed temperature from an agent's property bag, or null when none was echoed.</summary>
    internal static double? TemperatureFromProperties(IReadOnlyDictionary<string, object?>? properties)
        => ReadFromProperties(properties, TemperatureKeys, ToDouble);

    private static T? ReadFromProperties<T>(
        IReadOnlyDictionary<string, object?>? properties,
        string[] keys,
        Func<object?, T?> convert)
        where T : struct
    {
        if (properties is null)
            return null;

        foreach (var key in keys)
        {
            if (!properties.TryGetValue(key, out var value))
                continue;
            if (convert(value) is { } converted)
                return converted;
        }

        return null;
    }

    private static T? ReadFromRaw<T>(object? raw, string[] names, Func<object?, T?> convert)
        where T : struct
    {
        if (raw is null)
            return null;

        foreach (var name in names)
        {
            try
            {
                var property = raw.GetType().GetProperty(name);
                if (property is null || property.GetIndexParameters().Length > 0)
                    continue;
                if (convert(property.GetValue(raw)) is { } converted)
                    return converted;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Diagnostic metadata only: a provider object that throws on read must not fail the
                // run, and "not available" is a legitimate answer.
            }
        }

        return null;
    }

    private static int? ToInt32(object? value) => value switch
    {
        null => null,
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => f,
        int i => i,
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDouble(out var parsed)
            && double.IsFinite(parsed) => parsed,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) => parsed,
        _ => null
    };
}
