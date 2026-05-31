// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

namespace AgentEval.Assertions;

/// <summary>
/// Type-aware comparison of a recorded tool-call argument value (often a
/// <see cref="JsonElement"/>) against a caller-supplied expected value.
/// <para>
/// Replaces the previous <c>JsonElement.GetRawText().Trim('"')</c> + ordinal string compare
/// (BUG-21), which mangled string values that themselves began/ended with a quote and diverged
/// from CLR <c>ToString()</c> for non-string kinds (JSON <c>true</c> vs CLR <c>True</c>,
/// JSON <c>1.0</c> vs CLR <c>1</c>), so correct boolean/numeric arguments compared unequal.
/// </para>
/// </summary>
internal static class ToolArgumentComparison
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="actualValue"/> matches
    /// <paramref name="expectedValue"/>: numbers compare numerically (1 == 1.0), booleans by
    /// value (JSON <c>true</c> == CLR <c>true</c>), everything else by its unquoted string form.
    /// </summary>
    public static bool Matches(object? actualValue, object? expectedValue)
    {
        // Genuine numbers compare numerically so JSON 1.0 equals CLR int 1. We only take this
        // path when BOTH sides are real numeric kinds — two strings stay string-compared.
        if (TryAsNumber(actualValue, out var a) && TryAsNumber(expectedValue, out var e))
            return a.Equals(e);

        // Genuine booleans compare by value so JSON true/false matches CLR True/False.
        if (TryAsBool(actualValue, out var ab) && TryAsBool(expectedValue, out var eb))
            return ab == eb;

        return string.Equals(Canonical(actualValue), Canonical(expectedValue), StringComparison.Ordinal);
    }

    /// <summary>
    /// The unquoted string form of an argument value, used for display and string equality.
    /// For a <see cref="JsonElement"/> string this is <see cref="JsonElement.GetString"/>
    /// (no surrounding quotes, no <c>Trim('"')</c> mangling of quote-bearing values).
    /// </summary>
    public static string? Canonical(object? value) => value switch
    {
        null => null,
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => je.GetRawText(),
        },
        _ => value.ToString(),
    };

    private static bool TryAsNumber(object? value, out double result)
    {
        switch (value)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Number:
                return je.TryGetDouble(out result);
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryAsBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                result = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
