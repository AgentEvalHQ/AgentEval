// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
    /// <inheritdoc />
    public string PolicyName => "pii-detection";

    /// <inheritdoc />
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
        }

        var scan = PiiScanner.Scan(text);

        // Preserve this gate's compatibility behavior: a timeout without a completed match is advisory-clean.
        // Contract predicates consume the same scanner but map Inconclusive to Block.
        if (scan.DetectedKinds.Count == 0)
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
        }

        // Block carries the redacted text too, so the client can mask under the Redact policy.
        var verdict = GateVerdict.Block(
            PolicyName,
            $"PII detected: {string.Join(", ", scan.DetectedKinds)}",
            scan.DetectedKinds,
            redactedText: scan.RedactedText);
        return new ValueTask<GateVerdict>(verdict);
    }
}
