// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// The JSON contract the judge is asked to emit under <see cref="JudgeVerdictProtocol.StructuredJson"/>,
/// and the parser that turns a provider response back into a typed outcome.
/// </summary>
/// <remarks>
/// The verdict lives in its own field with a closed value set, and reasoning lives in a different field.
/// Keeping them apart is the entire point: under the free-text protocol the verdict was recovered from
/// prose, so reasoning that merely contained the word "no" could veto a "yes".
/// </remarks>
internal static class LongMemEvalStructuredVerdict
{
    /// <summary>Verdict value meaning the response is correct.</summary>
    internal const string VerdictYes = "yes";

    /// <summary>Verdict value meaning the response is incorrect.</summary>
    internal const string VerdictNo = "no";

    /// <summary>
    /// Verdict value meaning the judge evaluated the question and could not decide. This is a judgment,
    /// not a parse failure, and is reported with its own failure code so the two stay separable.
    /// </summary>
    internal const string VerdictCannotDetermine = "cannot-determine";

    /// <summary>Name reported to providers that label the schema (OpenAI structured outputs).</summary>
    internal const string SchemaName = "longmemeval_judge_verdict";

    /// <summary>Bound on retained reasoning, mirroring the 4096-character raw-response bound.</summary>
    internal const int MaximumReasoningLength = 4096;

    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "verdict": {
              "type": "string",
              "enum": ["yes", "no", "cannot-determine"],
              "description": "Whether the model response is correct. Use cannot-determine only when the question genuinely cannot be decided from the inputs."
            },
            "reasoning": {
              "type": "string",
              "description": "Brief justification. Never put the verdict here; it is read from the verdict field alone."
            }
          },
          "required": ["verdict", "reasoning"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonElement s_schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    /// <summary>The JSON Schema describing a well-formed verdict object.</summary>
    internal static JsonElement Schema => s_schema;

    /// <summary>
    /// The instruction appended to a judge prompt so the model emits the contract. Sent regardless of
    /// whether the provider honours a response-format constraint, so an unconstrained provider still has
    /// a chance to comply.
    /// </summary>
    internal static string PromptSuffix { get; } =
        $$"""

        Reply with a single JSON object and nothing else:
        {"verdict": "{{VerdictYes}}" | "{{VerdictNo}}" | "{{VerdictCannotDetermine}}", "reasoning": "<one or two sentences>"}

        The "verdict" field must be exactly one of those three values. Put no explanation in the "verdict"
        field and no verdict in the "reasoning" field. Use "{{VerdictCannotDetermine}}" only when the inputs
        genuinely do not allow a decision — it is not a way to avoid a hard call.
        """;

    /// <summary>
    /// Parses a structured judge response. Returns <see cref="JudgeOutcomeStatus.Invalid"/> for anything
    /// unusable rather than throwing, returning a silent <see cref="JudgeOutcomeStatus.No"/>, or guessing
    /// from surrounding prose.
    /// </summary>
    /// <param name="raw">Raw provider text.</param>
    /// <param name="finishReason">Provider finish reason, when supplied.</param>
    /// <returns>
    /// The typed status, the extracted reasoning when one was present, and a bounded failure code
    /// describing why a non-binary status was produced.
    /// </returns>
    internal static (JudgeOutcomeStatus Status, string? Reasoning, string? FailureCode) Parse(
        string? raw,
        string? finishReason = null)
    {
        // A truncated or filtered response cannot be trusted even if it happens to parse — the verdict
        // field may have been cut mid-token. Checked before parsing, matching the free-text path.
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            return (JudgeOutcomeStatus.Invalid, null, "invalid_finish_reason");
        if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
            return (JudgeOutcomeStatus.Invalid, null, "content_filtered");

        var text = raw?.Trim().TrimStart('\uFEFF').Trim();
        if (string.IsNullOrWhiteSpace(text))
            return (JudgeOutcomeStatus.Empty, null, "empty_response");

        var json = ExtractJsonObject(text);
        if (json is null)
            return (JudgeOutcomeStatus.Invalid, null, "structured_no_json");

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (JudgeOutcomeStatus.Invalid, null, "structured_malformed_json");
        }

        using (document)
        {
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (JudgeOutcomeStatus.Invalid, null, "structured_not_object");

            if (!root.TryGetProperty("verdict", out var verdictProperty))
                return (JudgeOutcomeStatus.Invalid, null, "structured_missing_verdict");

            // A non-string verdict (number, bool, object) is malformed. Do not coerce it — coercion is
            // how a guess gets laundered into a verdict.
            if (verdictProperty.ValueKind != JsonValueKind.String)
                return (JudgeOutcomeStatus.Invalid, null, "structured_verdict_not_string");

            var reasoning = ReadReasoning(root);
            var verdict = verdictProperty.GetString()?.Trim();

            // Closed set, matched exactly apart from case and surrounding whitespace. Anything else is
            // Invalid rather than mapped to the nearest neighbour.
            if (string.Equals(verdict, VerdictYes, StringComparison.OrdinalIgnoreCase))
                return (JudgeOutcomeStatus.Yes, reasoning, null);
            if (string.Equals(verdict, VerdictNo, StringComparison.OrdinalIgnoreCase))
                return (JudgeOutcomeStatus.No, reasoning, null);
            if (string.Equals(verdict, VerdictCannotDetermine, StringComparison.OrdinalIgnoreCase))
                return (JudgeOutcomeStatus.Invalid, reasoning, "judge_cannot_determine");

            return (JudgeOutcomeStatus.Invalid, reasoning, "structured_verdict_out_of_enum");
        }
    }

    private static string? ReadReasoning(JsonElement root)
    {
        if (!root.TryGetProperty("reasoning", out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        var reasoning = property.GetString()?.Trim();
        if (string.IsNullOrEmpty(reasoning))
            return null;

        return reasoning.Length > MaximumReasoningLength
            ? reasoning[..MaximumReasoningLength]
            : reasoning;
    }

    /// <summary>
    /// Recovers a JSON object from a response that may be fenced in markdown or padded with prose.
    /// Returns null when no balanced object is present.
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        var candidate = StripCodeFence(text);

        var start = candidate.IndexOf('{');
        if (start < 0)
            return null;

        // Scan for the matching close brace rather than taking LastIndexOf('}'), so trailing prose after
        // the object does not drag unbalanced text into the parse. String-aware so a brace inside the
        // reasoning text cannot end the object early.
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < candidate.Length; i++)
        {
            var c = candidate[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
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
