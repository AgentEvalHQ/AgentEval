// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace AgentEval.Guardrails.Gates;

/// <summary>The outcome of a bounded PII scan.</summary>
public enum PiiScanStatus
{
    /// <summary>Every configured pattern completed and none matched.</summary>
    Clean,

    /// <summary>At least one configured PII pattern matched and every pattern completed.</summary>
    Match,

    /// <summary>At least one bounded pattern timed out, so the complete input could not be classified.</summary>
    Inconclusive,
}

/// <summary>
/// Result of <see cref="PiiScanner.Scan"/>. <see cref="DetectedKinds"/> contains category names only, never
/// matched values. <see cref="RedactedText"/> masks every match that completed before any timeout.
/// </summary>
public sealed record PiiScanResult(
    PiiScanStatus Status,
    IReadOnlyList<string> DetectedKinds,
    string RedactedText);

/// <summary>
/// Shared, bounded PII scanner used by both chat gates and tool-usage contracts. Unlike the legacy
/// <see cref="RegexPiiGate"/> behavior, the scanner explicitly reports a regex timeout as
/// <see cref="PiiScanStatus.Inconclusive"/> so security-sensitive callers can fail closed.
/// </summary>
public static class PiiScanner
{
    private static readonly TimeSpan PiiTimeout = GateRegexTimeouts.Standard;

    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("Email", new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled, PiiTimeout)),
        ("Phone_US", new Regex(@"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b", RegexOptions.Compiled, PiiTimeout)),
        ("SSN", new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled, PiiTimeout)),
        ("CreditCard", new Regex(@"\b\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b", RegexOptions.Compiled, PiiTimeout)),
        ("IP_Address", new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled, PiiTimeout)),
    };

    /// <summary>
    /// Scans <paramref name="text"/> with the shared bounded pattern set. A timeout is never reported as clean.
    /// </summary>
    public static PiiScanResult Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return new PiiScanResult(PiiScanStatus.Clean, Array.Empty<string>(), text);
        }

        var matchedNames = new List<string>();
        var redacted = text;
        var inconclusive = false;

        foreach (var (name, pattern) in Patterns)
        {
            try
            {
                if (!pattern.IsMatch(text))
                {
                    continue;
                }

                matchedNames.Add(name);
                redacted = pattern.Replace(redacted, match => new string('█', match.Length));
            }
            catch (RegexMatchTimeoutException)
            {
                inconclusive = true;
            }
        }

        var status = inconclusive
            ? PiiScanStatus.Inconclusive
            : matchedNames.Count > 0
                ? PiiScanStatus.Match
                : PiiScanStatus.Clean;

        return new PiiScanResult(
            status,
            new ReadOnlyCollection<string>(matchedNames.ToArray()),
            redacted);
    }
}
