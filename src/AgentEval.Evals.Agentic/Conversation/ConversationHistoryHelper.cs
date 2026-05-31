// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Conversation;

/// <summary>
/// Shared helpers for evaluators that consume conversation history from
/// <see cref="EvalInput.Metadata"/>. Centralises the metadata-key contract,
/// the extraction shape (with <c>IEnumerable</c>/<c>IReadOnlyList</c> tolerance),
/// and the transcript formatting used in judge prompts.
/// </summary>
/// <remarks>
/// Used by memory, multi-turn, and self-correction evaluators. Replaces the
/// duplicated <c>GetHistory</c> + <c>FormatHistory</c> private helpers that previously
/// lived in 5 evaluator files.
/// </remarks>
public static class ConversationHistoryHelper
{
    /// <summary>
    /// Metadata key carrying the conversation history as
    /// <c>IReadOnlyList&lt;ConversationTurn&gt;</c> (or any <c>IEnumerable&lt;ConversationTurn&gt;</c>).
    /// </summary>
    public const string MetadataKey = "conversation_history";

    /// <summary>
    /// Metadata key carrying a single <see cref="ConversationTurn"/> representing the
    /// user's correction message + the agent's correction response. Consumed by
    /// <c>SelfCorrectionQualityEval</c>.
    /// </summary>
    public const string CorrectionTurnMetadataKey = "correction_turn";

    /// <summary>
    /// Returns the conversation history from <paramref name="input"/>'s metadata, or
    /// <c>null</c> if the key is absent or the value cannot be coerced to a list of turns.
    /// </summary>
    public static IReadOnlyList<ConversationTurn>? TryGetHistory(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Metadata is null) return null;
        if (!input.Metadata.TryGetValue(MetadataKey, out var raw)) return null;

        return raw switch
        {
            IReadOnlyList<ConversationTurn> list => list,
            IEnumerable<ConversationTurn> en     => en.ToList(),
            _                                    => null,
        };
    }

    /// <summary>
    /// Returns the correction turn from <paramref name="input"/>'s metadata, or <c>null</c>
    /// when the key is absent or the value isn't a <see cref="ConversationTurn"/>.
    /// </summary>
    public static ConversationTurn? TryGetCorrectionTurn(EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Metadata is null) return null;
        return input.Metadata.TryGetValue(CorrectionTurnMetadataKey, out var raw)
            ? raw as ConversationTurn
            : null;
    }

    /// <summary>
    /// Formats a conversation history as a labelled transcript suitable for embedding
    /// in a judge prompt. Each turn is emitted as <c>[turn N] role: content</c>.
    /// </summary>
    public static string FormatTranscript(IReadOnlyList<ConversationTurn> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var sb = new StringBuilder("=== Conversation history ===");
        for (int i = 0; i < history.Count; i++)
        {
            var t = history[i];
            sb.Append($"\n[turn {i + 1}] {t.Role}: {t.Content}");
        }
        sb.Append("\n=== End of history ===");
        return sb.ToString();
    }

    /// <summary>
    /// Formats a single previous turn as a small context block — used by
    /// <c>TurnCoherenceEval</c> which only needs the immediately-preceding turn.
    /// </summary>
    public static string FormatPreviousTurn(ConversationTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return $"=== Previous turn ===\n[{turn.Role}]: {turn.Content}\n=== End of previous turn ===";
    }

    /// <summary>
    /// Returns a copy of <paramref name="input"/> whose Query is prefixed with the given
    /// <paramref name="transcript"/>, preserving every other field via the record <c>with</c>
    /// expression. The conversational evaluators previously rebuilt EvalInput with only
    /// Query/Response/Metadata, silently dropping SystemMessage/ToolCalls/ToolDefinitions/
    /// Context/GroundTruth/ExpectedActions (BUG-57).
    /// </summary>
    public static EvalInput WithEnrichedQuery(EvalInput input, string transcript)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input with { Query = $"{transcript}\n\nCurrent user query: {input.Query}" };
    }
}
