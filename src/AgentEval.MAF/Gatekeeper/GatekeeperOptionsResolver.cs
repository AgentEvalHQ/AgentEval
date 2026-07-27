// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Construction-time validation and defensive snapshotting for nullable Phase-3 defaults.</summary>
internal static class GatekeeperOptionsResolver
{
    internal const int DefaultContainmentRetryThreshold = 5;
    internal const int MaxContainmentRetryThreshold = 1_000;
    internal const int MaxCamouflagedMessageCount = 32;
    internal const int MaxCamouflagedMessageLength = 512;

    private static readonly string[] ReservedDisclosureTokens =
    [
        "_gatekeeper",
        "gatekeeper",
        "reference",
        "target",
        "referenceId",
        "policy",
        "threshold",
        "attempt",
        "bypass",
        "disposition",
        "quota",
        "escalate",
        "transient",
        "containment",
        "blocked",
        "denied",
        "guardrail",
        "security",
    ];

    internal static ResolvedGatekeeperOptions Resolve(GatekeeperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var containmentConfigured = options.ContainmentStore is not null;
        var targetsConfigured = options.ContainmentTargets is not null;
        if (containmentConfigured != targetsConfigured)
        {
            throw new InvalidOperationException(
                "GatekeeperOptions.ContainmentStore and ContainmentTargets must be configured together.");
        }

        if (options.AdditionalContainmentTargets is not null && !containmentConfigured)
        {
            throw new InvalidOperationException(
                "GatekeeperOptions.AdditionalContainmentTargets requires ContainmentStore and ContainmentTargets.");
        }

        var threshold = options.ContainmentRetryThreshold ?? DefaultContainmentRetryThreshold;
        if (threshold is < 1 or > MaxContainmentRetryThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ContainmentRetryThreshold),
                $"GatekeeperOptions.ContainmentRetryThreshold must be in the inclusive range " +
                $"1..{MaxContainmentRetryThreshold}.");
        }

        var style = options.RefusalStyle ?? GatekeeperRefusalStyle.Structured;
        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RefusalStyle),
                "GatekeeperOptions.RefusalStyle contains an undefined value.");
        }

        var configuredMessages = options.CamouflagedRefusalMessages;
        if (style == GatekeeperRefusalStyle.Structured)
        {
            if (configuredMessages is not null && ReadMessageCount(configuredMessages) > 0)
            {
                throw new InvalidOperationException(
                    "GatekeeperOptions.CamouflagedRefusalMessages can be configured only when RefusalStyle is " +
                    "Camouflaged. Clear the pool or select Camouflaged.");
            }

            return new ResolvedGatekeeperOptions(
                threshold,
                style,
                Array.AsReadOnly(Array.Empty<string>()),
                options.ContainmentStore,
                options.ContainmentTargets,
                options.AdditionalContainmentTargets);
        }

        if (configuredMessages is null)
        {
            throw new InvalidOperationException(
                "GatekeeperOptions.RefusalStyle is Camouflaged, but CamouflagedRefusalMessages is null. " +
                "Configure a validated caller-owned message pool.");
        }

        var messageCount = ReadMessageCount(configuredMessages);

        if (messageCount is < 1 or > MaxCamouflagedMessageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.CamouflagedRefusalMessages),
                $"GatekeeperOptions.CamouflagedRefusalMessages must contain 1.." +
                $"{MaxCamouflagedMessageCount} entries in Camouflaged mode.");
        }

        var copy = new string[messageCount];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < messageCount; index++)
        {
            string? message;
            try
            {
                message = configuredMessages[index];
            }
            catch
            {
                throw InvalidMessage(index, "could not be read during construction");
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw InvalidMessage(index, "must not be null, empty, or whitespace");
            }

            if (message.Length > MaxCamouflagedMessageLength)
            {
                throw InvalidMessage(index, $"must not exceed {MaxCamouflagedMessageLength} characters");
            }

            if (!string.Equals(message, message.Trim(), StringComparison.Ordinal))
            {
                throw InvalidMessage(index, "must already be trimmed");
            }

            if (!message.IsNormalized())
            {
                throw InvalidMessage(index, "must already be Unicode NFC-normalized");
            }

            if (message.Any(character =>
                    char.IsControl(character)
                    || char.GetUnicodeCategory(character) is
                        System.Globalization.UnicodeCategory.LineSeparator or
                        System.Globalization.UnicodeCategory.ParagraphSeparator))
            {
                throw InvalidMessage(index, "must not contain control or line-separator characters");
            }

            if (message.Contains('{')
                || message.Contains('}'))
            {
                throw InvalidMessage(index, "must not contain placeholder braces");
            }

            if (ReservedDisclosureTokens.Any(token =>
                    message.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                throw InvalidMessage(index, "contains a reserved security-disclosure token");
            }

            if (!unique.Add(message))
            {
                throw InvalidMessage(index, "duplicates an earlier message");
            }

            copy[index] = message;
        }

        return new ResolvedGatekeeperOptions(
            threshold,
            style,
            Array.AsReadOnly(copy),
            options.ContainmentStore,
            options.ContainmentTargets,
            options.AdditionalContainmentTargets);
    }

    private static int ReadMessageCount(IReadOnlyList<string> messages)
    {
        try
        {
            return messages.Count;
        }
        catch
        {
            throw new InvalidOperationException(
                "GatekeeperOptions.CamouflagedRefusalMessages could not be read during construction.");
        }
    }

    private static InvalidOperationException InvalidMessage(int index, string rule)
        => new(
            $"GatekeeperOptions.CamouflagedRefusalMessages[{index}] {rule}. " +
            "The configured value is intentionally omitted.");
}
