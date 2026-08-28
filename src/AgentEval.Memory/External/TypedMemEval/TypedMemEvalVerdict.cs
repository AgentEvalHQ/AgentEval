// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// The JSON contract the TypedMemEval judge emits, and the parser that turns a provider response
/// back into a typed outcome.
/// </summary>
/// <remarks>
/// <para>
/// The outcome lives in its own field with a closed value set, and reasoning lives in a different
/// field. Keeping them apart is the whole point, and the lesson is paid for: under a free-text
/// protocol the verdict was recovered from prose, so reasoning that merely contained a negation
/// could veto a correct answer. A five-way outcome makes that failure mode strictly worse, which
/// is why this family has no free-text path at all.
/// </para>
/// <para>
/// Three schemas rather than one with optional fields: providers that enforce strict structured
/// output require every declared property to be required, so a single schema with sometimes-absent
/// fields would be rejected by exactly the providers whose enforcement is worth having.
/// </para>
/// </remarks>
internal static class TypedMemEvalVerdict
{
    internal const string OutcomeCorrect = "correct";
    internal const string OutcomeWrong = "wrong";
    internal const string OutcomeAbstained = "abstained";
    internal const string OutcomeMissed = "missed";
    internal const string OutcomePremature = "premature";

    internal const string SchemaName = "typedmemeval_judge_verdict";
    internal const int MaximumReasoningLength = 4096;

    /// <summary>Which contract a question's judge call uses.</summary>
    internal enum Kind
    {
        /// <summary>Outcome and reasoning only.</summary>
        Base,

        /// <summary>Adds whether the stale value was asserted as current (Forgetting).</summary>
        Forgetting,

        /// <summary>Adds the observed pairwise-order tally (Episodic list-order).</summary>
        ListOrder,

        /// <summary>
        /// Adds which branch of the two-clock discriminator the judge took (Bitemporal).
        /// </summary>
        /// <remarks>
        /// The outcome alone cannot say WHY it was chosen, so a template that suppresses a label
        /// outright looks identical to one that discriminates correctly — which is exactly how the
        /// first Bitemporal body scored 24/24 while being overfitted. Emitting the branch makes the
        /// intermediate step assertable at no extra call.
        /// </remarks>
        Bitemporal,

        /// <summary>Adds which branch of the ordering-versus-presence discriminator was taken (Temporal).</summary>
        Temporal
    }

    private const string OutcomeProperty = """
            "outcome": {
              "type": "string",
              "enum": ["correct", "wrong", "abstained", "missed", "premature"],
              "description": "The single outcome that describes the response, per the stated rules."
            },
            "reasoning": {
              "type": "string",
              "description": "One or two sentences. Never put the outcome here; it is read from the outcome field alone."
            }
        """;

    private static readonly JsonElement s_base = ParseSchema($$"""
        {
          "type": "object",
          "properties": {
        {{OutcomeProperty}}
          },
          "required": ["outcome", "reasoning"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement s_forgetting = ParseSchema($$"""
        {
          "type": "object",
          "properties": {
        {{OutcomeProperty}},
            "stale_value_asserted": {
              "type": "boolean",
              "description": "True only when the response states the superseded value as if it were still current."
            },
            "claimed_no_longer_known": {
              "type": "boolean",
              "description": "True when the response asserts the fact is no longer known, valid, or held."
            }
          },
          "required": ["outcome", "reasoning", "stale_value_asserted", "claimed_no_longer_known"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement s_listOrder = ParseSchema($$"""
        {
          "type": "object",
          "properties": {
        {{OutcomeProperty}},
            "ordered_pairs_correct": {
              "type": "integer",
              "description": "Pairs of mentioned items placed in the correct relative order."
            },
            "ordered_pairs_total": {
              "type": "integer",
              "description": "Pairs of mentioned items considered. Zero when the response mentions fewer than two items."
            }
          },
          "required": ["outcome", "reasoning", "ordered_pairs_correct", "ordered_pairs_total"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement s_bitemporal = ParseSchema($$"""
        {
          "type": "object",
          "properties": {
        {{OutcomeProperty}},
            "question_asks": {
              "type": "string",
              "enum": ["value", "occurrence"],
              "description": "Which the question asks for: the VALUE the record held at the as-of instant, or WHETHER a correction had been made by then. Report the branch you actually took."
            }
          },
          "required": ["outcome", "reasoning", "question_asks"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement s_temporal = ParseSchema($$"""
        {
          "type": "object",
          "properties": {
        {{OutcomeProperty}},
            "question_asks": {
              "type": "string",
              "enum": ["ordering", "presence"],
              "description": "Which failure the answer commits to: ORDERING, accepting the events and misplacing them, or PRESENCE, denying the record holds them. Report the branch you actually took."
            }
          },
          "required": ["outcome", "reasoning", "question_asks"],
          "additionalProperties": false
        }
        """);

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static JsonElement Schema(Kind kind) => kind switch
    {
        Kind.Forgetting => s_forgetting,
        Kind.ListOrder => s_listOrder,
        Kind.Bitemporal => s_bitemporal,
        Kind.Temporal => s_temporal,
        _ => s_base
    };

    /// <summary>The parsed shape of one judge response.</summary>
    internal readonly record struct Parsed(
        TypedMemEvalOutcome? Outcome,
        string? Reasoning,
        bool StaleValueAsserted,
        bool ClaimedNoLongerKnown,
        int? OrderedPairsCorrect,
        int? OrderedPairsTotal,
        string? QuestionAsks,
        string? FailureCode);

    /// <summary>
    /// Parses a structured judge response. Returns a null outcome for anything unusable rather
    /// than throwing, silently returning <see cref="TypedMemEvalOutcome.Wrong"/>, or guessing from
    /// surrounding prose — a judge that guesses is a judge whose numbers cannot be defended.
    /// </summary>
    internal static Parsed Parse(string? raw, string? finishReason = null)
    {
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            return Fail("invalid_finish_reason");
        if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
            return Fail("content_filtered");

        var text = raw?.Trim().TrimStart('﻿').Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Fail("empty_response");

        var json = JsonObjectExtractor.Extract(text);
        if (json is null)
            return Fail("structured_no_json");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Fail("structured_malformed_json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail("structured_not_object");
            if (!root.TryGetProperty("outcome", out var outcomeProperty))
                return Fail("structured_missing_outcome");
            if (outcomeProperty.ValueKind != JsonValueKind.String)
                return Fail("structured_outcome_not_string");

            var reasoning = ReadReasoning(root);
            var outcome = outcomeProperty.GetString()?.Trim().ToLowerInvariant() switch
            {
                OutcomeCorrect => TypedMemEvalOutcome.Correct,
                OutcomeWrong => TypedMemEvalOutcome.Wrong,
                OutcomeAbstained => TypedMemEvalOutcome.Abstained,
                OutcomeMissed => TypedMemEvalOutcome.Missed,
                OutcomePremature => TypedMemEvalOutcome.Premature,
                _ => (TypedMemEvalOutcome?)null
            };

            if (outcome is null)
                return new Parsed(null, reasoning, false, false, null, null, null, "structured_outcome_out_of_enum");

            return new Parsed(
                outcome,
                reasoning,
                root.TryGetProperty("stale_value_asserted", out var stale) &&
                    stale.ValueKind == JsonValueKind.True,
                root.TryGetProperty("claimed_no_longer_known", out var forgotten) &&
                    forgotten.ValueKind == JsonValueKind.True,
                ReadInt(root, "ordered_pairs_correct"),
                ReadInt(root, "ordered_pairs_total"),
                ReadQuestionAsks(root),
                null);
        }
    }

    private static Parsed Fail(string code) => new(null, null, false, false, null, null, null, code);

    /// <summary>
    /// The discriminator branch, lower-cased, or null when the judge did not emit one. Never
    /// inferred from the outcome: the whole point is that it is an INDEPENDENT observation of the
    /// step the outcome was derived from, so guessing it would defeat the assertion it exists for.
    /// </summary>
    private static string? ReadQuestionAsks(JsonElement root)
        => root.TryGetProperty("question_asks", out var value) &&
           value.ValueKind == JsonValueKind.String &&
           value.GetString() is { Length: > 0 } text
            ? text.Trim().ToLowerInvariant()
            : null;

    private static int? ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string? ReadReasoning(JsonElement root)
    {
        if (!root.TryGetProperty("reasoning", out var property) ||
            property.ValueKind != JsonValueKind.String)
            return null;

        var reasoning = property.GetString()?.Trim();
        if (string.IsNullOrEmpty(reasoning))
            return null;

        return reasoning.Length > MaximumReasoningLength
            ? reasoning[..MaximumReasoningLength]
            : reasoning;
    }
}

/// <summary>
/// Recovers a JSON object from a response that may be fenced in markdown or padded with prose.
/// </summary>
internal static class JsonObjectExtractor
{
    /// <summary>Returns the first balanced JSON object in the text, or null when there is none.</summary>
    internal static string? Extract(string text)
    {
        var candidate = StripCodeFence(text);
        var start = candidate.IndexOf('{');
        if (start < 0)
            return null;

        // Scans for the matching close brace rather than taking the last one, so trailing prose
        // after the object cannot drag unbalanced text into the parse. String-aware, so a brace
        // inside the reasoning text cannot end the object early.
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < candidate.Length; i++)
        {
            var c = candidate[i];
            if (escaped) { escaped = false; continue; }
            if (inString)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return candidate[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    private static string StripCodeFence(string text)
    {
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence < 0)
            return text;

        var afterFence = fence + 3;
        if (text.AsSpan(afterFence).StartsWith("json", StringComparison.OrdinalIgnoreCase))
            afterFence += 4;

        var close = text.IndexOf("```", afterFence, StringComparison.Ordinal);
        return close > afterFence ? text[afterFence..close] : text[afterFence..];
    }
}
