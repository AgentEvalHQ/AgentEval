// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Skills;

/// <summary>
/// Value-based, key-agnostic extraction of a skill tool-call argument (skill name / resource name /
/// script name). Tries the known argument key first (verified against the live MAF 1.13.0 assembly —
/// see <see cref="SkillToolNames"/>), then falls back to scanning argument values so a future MAF
/// version renaming the parameter degrades gracefully instead of breaking silently. This is the same
/// name-agnostic-value-matching mitigation the design doc specifies for the assertions and metric.
/// </summary>
internal static class SkillArgumentExtraction
{
    public static string? ExtractSkillName(IDictionary<string, object?>? arguments) =>
        ExtractValue(arguments, SkillToolNames.SkillNameArg);

    public static string? ExtractResourceName(IDictionary<string, object?>? arguments) =>
        ExtractValue(arguments, SkillToolNames.ResourceNameArg);

    public static string? ExtractScriptName(IDictionary<string, object?>? arguments) =>
        ExtractValue(arguments, SkillToolNames.ScriptNameArg);

    /// <summary>
    /// Returns the string value of <paramref name="preferredKey"/> if present. Otherwise, falls back
    /// to the sole string-valued argument in the dictionary IF there is exactly one (an unambiguous
    /// fallback) — never guesses among multiple candidates, which would risk silently attributing the
    /// wrong value.
    /// </summary>
    public static string? ExtractValue(IDictionary<string, object?>? arguments, string preferredKey)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        if (arguments.TryGetValue(preferredKey, out var known))
        {
            var direct = AsString(known);
            if (direct is not null)
                return direct;
        }

        var stringValues = arguments.Values.Select(AsString).Where(s => s is not null).ToList();
        return stringValues.Count == 1 ? stringValues[0] : null;
    }

    /// <summary>Returns <see langword="true"/> when the value-extracted argument equals <paramref name="expected"/> (ordinal, case-insensitive).</summary>
    public static bool ValueMatches(IDictionary<string, object?>? arguments, string preferredKey, string expected)
    {
        var actual = ExtractValue(arguments, preferredKey);
        return actual is not null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
    };
}
