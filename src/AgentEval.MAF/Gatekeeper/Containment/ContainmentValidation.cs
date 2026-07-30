// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.MAF.Gatekeeper;

internal static class ContainmentValidation
{
    internal const int MaxTenantLength = 128;
    internal const int MaxIdentifierLength = 256;
    internal const int MaxReasonCodeLength = 128;
    internal const int MaxEvidenceReferenceLength = 128;
    internal const int MaxActorLength = 256;
    internal const int MaxETagLength = 128;
    internal const int MaxNonceLength = 128;
    internal const int MaxAlgorithmLength = 64;
    internal const int MaxKeyIdLength = 128;
    internal const int MaxSignatureLength = 4_096;

    internal static string Identity(string value, string parameterName, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsWellFormedUtf16(value))
        {
            throw Invalid(parameterName, "must contain well-formed UTF-16");
        }

        if (value.Any(IsForbiddenCharacter))
        {
            throw Invalid(parameterName, "must not contain control or line-separator characters");
        }

        var normalized = value.Trim().Normalize();
        if (normalized.Length is < 1 || normalized.Length > maxLength)
        {
            throw Invalid(parameterName, $"must contain 1..{maxLength} characters after normalization");
        }

        if (normalized.Any(IsForbiddenCharacter))
        {
            throw Invalid(parameterName, "must not contain control or line-separator characters");
        }

        return normalized;
    }

    internal static string Token(
        string value,
        string parameterName,
        int maxLength,
        int minLength = 1)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length < minLength || value.Length > maxLength)
        {
            throw Invalid(parameterName, $"must contain {minLength}..{maxLength} ASCII token characters");
        }

        if (!value.All(IsTokenCharacter))
        {
            throw Invalid(parameterName, "must contain only ASCII letters, digits, '.', '_', ':', or '-'");
        }

        return value;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Invalid(parameterName, "must use UTC offset zero");
        }

        return value;
    }

    internal static ArgumentException Invalid(string parameterName, string rule)
        => new($"Containment configuration field '{parameterName}' {rule}. The rejected value is omitted.", parameterName);

    private static bool IsTokenCharacter(char value)
        => value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '.' or '_' or ':' or '-';

    private static bool IsForbiddenCharacter(char value)
        => char.IsControl(value)
            || char.GetUnicodeCategory(value) is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index])
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }
}
