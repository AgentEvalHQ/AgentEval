// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Strict mailbox parsing and IDN-aware exact/subdomain matching for recipient contracts.</summary>
internal static class RecipientDomainPolicy
{
    internal const int MaxAllowedDomains = 256;
    internal const int MaxDomainChars = 256;
    internal const int MaxRecipients = 256;
    private const int MaxRecipientTextChars = ArgumentCanonicalizer.DefaultMaxLength;

    internal static IReadOnlyList<string> NormalizeAllowedDomains(
        IEnumerable<string> allowedDomains,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(allowedDomains);

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputCount = 0;
        foreach (var domain in allowedDomains)
        {
            inputCount++;
            if (inputCount > MaxAllowedDomains)
            {
                throw new ArgumentException(
                    $"at most {MaxAllowedDomains} allowed domains may be configured.",
                    parameterName);
            }

            if (!TryNormalizeDomain(domain, out var asciiDomain))
            {
                throw new ArgumentException(
                    "allowed domains must be bare DNS names with valid IDN labels.",
                    parameterName);
            }

            normalized.Add(asciiDomain);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("at least one allowed domain is required.", parameterName);
        }

        return new ReadOnlyCollection<string>(normalized
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(domain => domain, StringComparer.Ordinal)
            .ToArray());
    }

    internal static bool AllowsAll(object value, IReadOnlySet<string> allowedDomains)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(allowedDomains);

        try
        {
            var accumulator = new RecipientAccumulator(allowedDomains);
            var complete = value switch
            {
                string text => accumulator.AddMailboxList(text),
                JsonElement element => AddJsonElement(element, accumulator),
                IEnumerable<string> strings => AddStrings(strings, accumulator),
                _ => false,
            };

            return complete && accumulator.RecipientCount > 0;
        }
        catch (Exception exception) when
            (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool AddStrings(IEnumerable<string> values, RecipientAccumulator accumulator)
    {
        var itemCount = 0;
        foreach (var value in values)
        {
            itemCount++;
            if (itemCount > MaxRecipients || !accumulator.AddMailboxList(value))
            {
                return false;
            }
        }

        return itemCount > 0;
    }

    private static bool AddJsonElement(JsonElement element, RecipientAccumulator accumulator)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return accumulator.AddMailboxList(element.GetString());
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > MaxRecipients)
        {
            return false;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !accumulator.AddMailboxList(item.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryNormalizeDomain(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxDomainChars ||
            value.Any(char.IsControl))
        {
            return false;
        }

        var domain = value.Trim();
        if (domain.EndsWith(".", StringComparison.Ordinal))
        {
            domain = domain[..^1];
        }

        if (domain.Length == 0 ||
            domain.EndsWith(".", StringComparison.Ordinal) ||
            domain.Contains("..", StringComparison.Ordinal) ||
            domain.IndexOfAny(['@', '/', '\\', ':', '[', ']', '*']) >= 0 ||
            IPAddress.TryParse(domain, out _))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(domain);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (ascii.Length is < 1 or > 253 || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
        {
            return false;
        }

        foreach (var label in ascii.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }
        }

        normalized = ascii.ToLowerInvariant();
        return true;
    }

    private sealed class RecipientAccumulator(IReadOnlySet<string> allowedDomains)
    {
        private int _textChars;

        internal int RecipientCount { get; private set; }

        internal bool AddMailboxList(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
            {
                return false;
            }

            if (text.Length > MaxRecipientTextChars - _textChars)
            {
                return false;
            }

            _textChars += text.Length;
            var mailboxes = new MailAddressCollection();
            try
            {
                mailboxes.Add(text);
            }
            catch (FormatException)
            {
                return false;
            }

            if (mailboxes.Count == 0 || mailboxes.Count > MaxRecipients - RecipientCount)
            {
                return false;
            }

            foreach (MailAddress mailbox in mailboxes)
            {
                if (string.IsNullOrWhiteSpace(mailbox.User) ||
                    !TryNormalizeDomain(mailbox.Host, out var domain) ||
                    !HostAllowList.IsAllowed(domain, allowedDomains))
                {
                    return false;
                }
            }

            RecipientCount += mailboxes.Count;
            return true;
        }
    }
}
