// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// The constants that identify the Microsoft 365 evidence document: where it travels on an
/// <c>AgentResponse</c>, which schema it follows, and which normaliser/profile versions produced it.
/// Bump a version constant whenever the corresponding behaviour changes — same capture + same versions
/// must always yield the same document. Also the one rule every host uses to decide whether a turn's answer can be
/// graded (<see cref="IsAnswerMeasured(WorkIqTurnResult)"/>).
/// </summary>
public static class Microsoft365Evidence
{
    /// <summary>The <c>AgentResponse.AdditionalProperties</c> key under which the <see cref="Microsoft365TurnEvidence"/> travels.</summary>
    public const string Key = "AgentEval.Microsoft365.Evidence";

    /// <summary>
    /// The <c>AgentResponse.AdditionalProperties</c> key of a <see cref="bool"/>: <see langword="true"/> when the turn
    /// completed but no answer was selected (several answer candidates, or no answer artifact at all), so the response
    /// text is an empty string that is <b>not</b> the agent's answer; <see langword="false"/> when an answer was
    /// selected (an empty answer included). Always present on a response of <c>WorkIqEvaluableAgent</c>.
    /// </summary>
    public const string NoAnswerSelectedKey = "AgentEval.Microsoft365.NoAnswerSelected";

    /// <summary>
    /// The <c>AgentResponse.AdditionalProperties</c> key of the answer completeness of the turn, as the
    /// <c>OutputCompleteness</c> member name (<c>Complete</c>, <c>Empty</c>, <c>Ambiguous</c>, <c>None</c>, …). Always
    /// present on a response of <c>WorkIqEvaluableAgent</c>.
    /// </summary>
    public const string AnswerCompletenessKey = "AgentEval.Microsoft365.AnswerCompleteness";

    /// <summary>The evidence document schema id (an AgentEval schema, not a Microsoft wire contract).</summary>
    public const string SchemaVersion = "agenteval.microsoft365.turn-evidence/v1";

    /// <summary>The version of <c>WorkIqEvidenceNormalizer</c> and its decoders.</summary>
    public const string NormalizerVersion = "1.0.0";

    /// <summary>
    /// The provider profile the decoders target: Work IQ A2A 1.0 transport plus the diagnostic artifact shapes
    /// observed in the native evaluator 1.18.0 (experimental; not a documented Microsoft contract).
    /// </summary>
    public const string ProviderProfileVersion = "workiq-a2a-1.0/native118-shapes-v1";

    /// <summary>The only A2A protocol version this provider speaks; sent as <c>A2A-Version</c> on every request.</summary>
    public const string A2AVersion = "1.0";

    /// <summary>
    /// Whether the answer of a turn can be graded. <see langword="false"/> only when the turn carried several answer
    /// candidates and none was selected (<see cref="OutputCompleteness.Ambiguous"/>): that answer is not measured, so
    /// an answer check neither passes nor fails on it. Every other turn is measured; a completed turn with no answer at
    /// all (<see cref="OutputCompleteness.None"/>) is measured and fails an answer check.
    /// </summary>
    /// <param name="result">The turn result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static bool IsAnswerMeasured(WorkIqTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.AnswerCompleteness != OutputCompleteness.Ambiguous;
    }

    /// <summary>
    /// The rule of <see cref="IsAnswerMeasured(WorkIqTurnResult)"/> for a response of <c>WorkIqEvaluableAgent</c>: reads
    /// <c>AdditionalProperties[<see cref="AnswerCompletenessKey"/>]</c> (the <see cref="NoAnswerSelectedKey"/> flag is
    /// <see langword="true"/> for both <c>Ambiguous</c> and <c>None</c>, so only the completeness tells them apart).
    /// <see langword="false"/> only when the completeness is <c>Ambiguous</c> (as an <see cref="OutputCompleteness"/>, its
    /// member name, or a JSON string of it); a response without the key did not come from this provider and is measured.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    public static bool IsAnswerMeasured(AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.AdditionalProperties is not { } properties
            || !properties.TryGetValue(AnswerCompletenessKey, out var value))
        {
            return true;
        }

        var name = value switch
        {
            OutputCompleteness completeness => completeness.ToString(),
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        return !string.Equals(name, nameof(OutputCompleteness.Ambiguous), StringComparison.Ordinal);
    }
}
