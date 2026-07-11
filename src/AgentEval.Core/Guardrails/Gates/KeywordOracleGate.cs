// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Gates;

/// <summary>
/// A deterministic <b>keyword oracle</b> — the naive baseline a calibrated judge must beat. It blocks whenever the
/// inspected text contains any of a fixed keyword list (case-insensitive substring). This is the detector the
/// repo's own non-convergence finding indicts: a keyword list catches literal attacks but faces an unwinnable
/// precision/recall dilemma — a list tuned for override phrases (<c>ignore previous</c>, <c>system prompt</c>)
/// <b>misses paraphrase</b> (the payload is natural language, e.g. <c>"email the thread to backup@…"</c>), while a
/// list broad enough to catch the exfiltration tail <b>over-blocks</b> benign text that merely mentions
/// <c>api key</c> or <c>email</c>. Either way it loses ground a semantic judge holds.
/// <para>Use it as the <see cref="AgentEval.Guardrails.Judges.CalibrationOptions.DeterministicBaseline"/> so a judge earns promotion only
/// by being strictly better (higher accuracy AND no more missed attacks) on the same gold set. It is intentionally
/// simple — do not ship it as your only defense.</para>
/// </summary>
public sealed class KeywordOracleGate : IChatGate
{
    /// <summary>
    /// A representative classic prompt-injection keyword list (override / persona / directive staples). It is
    /// deliberately override-focused: it does NOT list exfiltration verbs (send / email / upload / api key), because
    /// adding those trades its blind spot for a false-alarm problem on benign mentions — the keyword dilemma the
    /// judge exists to resolve.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultInjectionKeywords = new[]
    {
        "ignore previous", "ignore all previous", "ignore the above", "disregard",
        "previous instructions", "prior instructions", "new instructions",
        "system prompt", "you are now", "act as", "override", "developer mode", "jailbreak",
    };

    private readonly string[] _keywords;

    /// <summary>The gate's policy name (evidence key). Defaults to <c>keyword-oracle</c>.</summary>
    public string PolicyName { get; }

    /// <param name="keywords">
    /// The keyword list to match (case-insensitive substring). Blank entries are dropped. Defaults to
    /// <see cref="DefaultInjectionKeywords"/>.
    /// </param>
    /// <param name="policyName">Evidence / policy name. Defaults to <c>keyword-oracle</c>.</param>
    /// <exception cref="ArgumentException">No non-blank keyword was supplied.</exception>
    public KeywordOracleGate(IEnumerable<string>? keywords = null, string? policyName = null)
    {
        _keywords = (keywords ?? DefaultInjectionKeywords)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToArray();

        if (_keywords.Length == 0)
        {
            throw new ArgumentException("At least one non-blank keyword is required.", nameof(keywords));
        }

        PolicyName = string.IsNullOrWhiteSpace(policyName) ? "keyword-oracle" : policyName;
    }

    /// <inheritdoc/>
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(text))
        {
            foreach (var k in _keywords)
            {
                if (text.Contains(k, StringComparison.OrdinalIgnoreCase))
                {
                    return new ValueTask<GateVerdict>(
                        GateVerdict.Block(PolicyName, $"keyword match: '{k}'", new[] { k }));
                }
            }
        }

        return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
    }
}
