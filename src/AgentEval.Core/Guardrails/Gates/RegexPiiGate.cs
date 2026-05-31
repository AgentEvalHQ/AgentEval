// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace AgentEval.Guardrails.Gates;

/// <summary>
/// An <see cref="IChatGate"/> that detects PII using the same five patterns as the Red Team
/// <c>PIIDetectionEvaluator</c> (Email, US phone, SSN, credit card, IP). Each regex is compiled with a
/// bounded <c>MatchTimeout</c> to prevent ReDoS (the repo enforces bounded regex evaluation). Pre-flight it
/// <see cref="GateAction.Block"/>s on any match; post-flight under Redact it returns the response with each
/// match masked by <c>█</c>.
/// </summary>
public sealed class RegexPiiGate : IChatGate
{
    private static readonly TimeSpan PiiTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("Email", new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled, PiiTimeout)),
        ("Phone_US", new Regex(@"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b", RegexOptions.Compiled, PiiTimeout)),
        ("SSN", new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled, PiiTimeout)),
        ("CreditCard", new Regex(@"\b\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b", RegexOptions.Compiled, PiiTimeout)),
        ("IP_Address", new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled, PiiTimeout)),
    };

    /// <inheritdoc />
    public string PolicyName => "pii-detection";

    /// <inheritdoc />
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
        }

        var matchedNames = new List<string>();
        var redacted = text;
        foreach (var (name, pattern) in Patterns)
        {
            try
            {
                if (!pattern.IsMatch(text))
                {
                    continue;
                }

                matchedNames.Add(name);
                // Mask each match with block characters of the matched length (for the Redact path).
                redacted = pattern.Replace(redacted, m => new string('█', m.Length));
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat a timeout as no-match for this pattern (ReDoS guard); other patterns still run.
            }
        }

        if (matchedNames.Count == 0)
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
        }

        // Block carries the redacted text too, so the client can mask under the Redact policy.
        var verdict = GateVerdict.Block(PolicyName, $"PII detected: {string.Join(", ", matchedNames)}", matchedNames, redactedText: redacted);
        return new ValueTask<GateVerdict>(verdict);
    }
}
