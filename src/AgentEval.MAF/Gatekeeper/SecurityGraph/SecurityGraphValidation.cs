// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

internal static class SecurityGraphValidation
{
    internal static string Identity(
        string value,
        string parameterName,
        int maxLength)
    {
        var normalized = ContainmentValidation.Identity(
            value,
            parameterName,
            maxLength);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            throw ContainmentValidation.Invalid(
                parameterName,
                "must already be trimmed and Unicode-normalized");
        }

        return normalized;
    }

    internal static string SessionDigest(
        string value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 43 ||
            !value.All(character =>
                character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-' or '_'))
        {
            throw ContainmentValidation.Invalid(
                parameterName,
                "must be a 32-byte base64url digest");
        }

        try
        {
            var bytes = Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/') + "=");
            var canonical = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            if (bytes.Length != 32 ||
                !string.Equals(value, canonical, StringComparison.Ordinal))
            {
                throw ContainmentValidation.Invalid(
                    parameterName,
                    "must be a 32-byte base64url digest");
            }
        }
        catch (FormatException)
        {
            throw ContainmentValidation.Invalid(
                parameterName,
                "must be a 32-byte base64url digest");
        }

        return value;
    }
}
