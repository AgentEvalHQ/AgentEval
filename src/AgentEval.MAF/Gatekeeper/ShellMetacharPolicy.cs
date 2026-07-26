// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Bounded, decode-aware shell metacharacter screening for explicitly selected syntax dialects.</summary>
internal static class ShellMetacharPolicy
{
    private const string PowerShellDenied = ";&|<>$`'\"(){}@#";
    private const string PosixShDenied = ";&|<>$`\\'\"(){}*?[]~#";
    private const string CmdDenied = "&|<>^%!\"()";

    internal static bool IsSafe(object value, ShellDialect dialect)
    {
        if (!Enum.IsDefined(dialect) || !TryReadText(value, out var text))
        {
            return false;
        }

        var canonical = ArgumentCanonicalizer.CanonicalizeWithStatus(text);
        if (!canonical.IsComplete || canonical.Projections.Count == 0)
        {
            return false;
        }

        var denied = dialect switch
        {
            ShellDialect.PowerShell => PowerShellDenied,
            ShellDialect.PosixSh => PosixShDenied,
            ShellDialect.Cmd => CmdDenied,
            _ => string.Empty,
        };

        foreach (var projection in canonical.Projections)
        {
            string normalized;
            try
            {
                normalized = projection.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return false;
            }

            foreach (var character in normalized)
            {
                var category = char.GetUnicodeCategory(character);
                if (char.IsControl(character) ||
                    category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ||
                    denied.Contains(character))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryReadText(object value, out string text)
    {
        try
        {
            switch (value)
            {
                case string direct:
                    text = direct;
                    break;
                case JsonElement { ValueKind: JsonValueKind.String } element:
                    text = element.GetString()!;
                    break;
                default:
                    text = string.Empty;
                    return false;
            }
        }
        catch (Exception exception) when
            (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            text = string.Empty;
            return false;
        }

        return !string.IsNullOrWhiteSpace(text) && text.Length <= ArgumentCanonicalizer.DefaultMaxLength;
    }
}
