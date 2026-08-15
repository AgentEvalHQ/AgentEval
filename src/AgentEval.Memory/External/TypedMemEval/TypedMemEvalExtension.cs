// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>One input to a gold arithmetic derivation.</summary>
public sealed record TypedMemEvalDerivationInput(
    int SessionIndex,
    double Value,
    int? FromSessionIndex = null)
{
    /// <summary>
    /// Every gold session this input depends on. A duration input spans a pair of sessions — the
    /// event it started at and the one it ended at — and reporting only the end would leave half of
    /// a duration question's gold out of its own component list.
    /// </summary>
    public IEnumerable<int> SessionIndices =>
        FromSessionIndex is { } from ? [from, SessionIndex] : [SessionIndex];
}

/// <summary>
/// The recorded gold derivation for an Arithmetic question: what was combined, how, and to what.
/// </summary>
/// <remarks>
/// Recording this is what lets the judge score the <i>arithmetic</i> rather than the phrasing, and
/// what makes a failure attributable — "wrong sum, missing exactly the un-retrieved input" is only
/// visible when the inputs are known individually.
/// </remarks>
public sealed record TypedMemEvalDerivation(
    string Operation,
    IReadOnlyList<TypedMemEvalDerivationInput> Inputs,
    double Value,
    string Unit);

/// <summary>One labelled gold component of a multi-component question.</summary>
public sealed record TypedMemEvalGoldComponent(string Kind, int SessionIndex);

/// <summary>
/// The family-owned block a TypedMemEval corpus carries per question.
/// </summary>
/// <remarks>
/// <b>This is an answer key.</b> <see cref="Derivation"/> holds the gold value verbatim, and
/// <see cref="GoldComponents"/> and <see cref="Arm"/> say which sessions matter and which side of a
/// pivot a question sits on. It is read on a path the agent never touches:
/// <see cref="LongMemEval.LongMemEvalEntry"/> has no member for it, so the entry the history
/// formatter reads cannot carry it even by accident. The prompt-leak guard test asserts that
/// property rather than assuming it.
/// </remarks>
public sealed class TypedMemEvalExtension
{
    /// <summary>The vertical this question belongs to; must match its corpus.</summary>
    public required string Vertical { get; init; }

    /// <summary>The diagnostic stratum, e.g. <c>due-later-reminder</c> or <c>list-order</c>.</summary>
    public required string Shape { get; init; }

    /// <summary>The time-grounded probe question this was carried from, when it was.</summary>
    public string? SeededFrom { get; init; }

    /// <summary>Links the two arms of a pair.</summary>
    public string? PairId { get; init; }

    /// <summary>Which arm of its pair: <c>before</c>, <c>after</c>, <c>control</c>, <c>invalidated</c>.</summary>
    public string? Arm { get; init; }

    /// <summary>WorkingMemory ladder position.</summary>
    public int? DistanceSessions { get; init; }

    /// <summary>Arithmetic only.</summary>
    public TypedMemEvalDerivation? Derivation { get; init; }

    /// <summary>Forgetting only: the statement and invalidation sessions.</summary>
    public IReadOnlyList<TypedMemEvalGoldComponent>? GoldComponents { get; init; }

    /// <summary>Arithmetic counts only: the stated counting predicate.</summary>
    public string? CountPredicate { get; init; }

    /// <summary>
    /// Gold session indices for this question, read from <c>answer_session_ids</c>. Used for
    /// coverage rather than the components list, so a vertical with no component labelling still
    /// gets a realised-coverage number.
    /// </summary>
    public required IReadOnlyList<int> GoldSessionIndices { get; init; }

    /// <summary>Session identifiers in haystack order, for matching adapter-reported evidence.</summary>
    public required IReadOnlyList<string> SessionIds { get; init; }
}

/// <summary>Reads the family extension blocks out of a corpus's raw JSON.</summary>
public static class TypedMemEvalExtensions
{
    /// <summary>
    /// Parses every question's extension block, keyed by question id.
    /// </summary>
    /// <param name="corpusJson">The corpus text as shipped.</param>
    /// <exception cref="InvalidOperationException">
    /// When a question has no extension block. A family corpus without one is malformed — the
    /// shape, pairing, and gold components are what the typed report is built from, and guessing
    /// them would produce a report that describes something other than the corpus.
    /// </exception>
    public static IReadOnlyDictionary<string, TypedMemEvalExtension> Parse(string corpusJson)
    {
        ArgumentNullException.ThrowIfNull(corpusJson);

        using var document = JsonDocument.Parse(corpusJson);
        var result = new Dictionary<string, TypedMemEvalExtension>(StringComparer.Ordinal);

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var questionId = entry.GetProperty("question_id").GetString()
                ?? throw new InvalidOperationException("TypedMemEval corpus entry has no question_id.");

            if (!entry.TryGetProperty("typedmemeval", out var block) ||
                block.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"TypedMemEval corpus entry '{questionId}' has no 'typedmemeval' block. The typed " +
                    $"report is built from it, so a missing block is a malformed corpus rather than a " +
                    $"question with defaults.");
            }

            var sessionIds = ReadStrings(entry, "haystack_session_ids");
            var answerIds = ReadStrings(entry, "answer_session_ids");
            var goldIndices = answerIds
                .Select(id => sessionIds.IndexOf(id))
                .Where(index => index >= 0)
                .ToArray();

            result[questionId] = new TypedMemEvalExtension
            {
                Vertical = ReadString(block, "vertical")
                    ?? throw new InvalidOperationException($"'{questionId}' has no vertical."),
                Shape = ReadString(block, "shape")
                    ?? throw new InvalidOperationException($"'{questionId}' has no shape."),
                SeededFrom = ReadString(block, "seeded_from"),
                PairId = ReadString(block, "pair_id"),
                Arm = ReadString(block, "arm"),
                DistanceSessions = ReadInt(block, "distance_sessions"),
                CountPredicate = ReadString(block, "count_predicate"),
                Derivation = ReadDerivation(block),
                GoldComponents = ReadComponents(block),
                GoldSessionIndices = goldIndices,
                SessionIds = sessionIds
            };
        }

        return result;
    }

    private static List<string> ReadStrings(JsonElement element, string name)
        => element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static TypedMemEvalDerivation? ReadDerivation(JsonElement block)
    {
        if (!block.TryGetProperty("derivation", out var derivation) ||
            derivation.ValueKind != JsonValueKind.Object)
            return null;

        var inputs = derivation.TryGetProperty("inputs", out var array) &&
                     array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Select(input => new TypedMemEvalDerivationInput(
                    input.GetProperty("session_index").GetInt32(),
                    input.GetProperty("value").GetDouble(),
                    input.TryGetProperty("from_session_index", out var from) &&
                    from.ValueKind == JsonValueKind.Number
                        ? from.GetInt32()
                        : null))
                .ToArray()
            : [];

        return new TypedMemEvalDerivation(
            ReadString(derivation, "operation") ?? "unknown",
            inputs,
            derivation.TryGetProperty("value", out var value) ? value.GetDouble() : double.NaN,
            ReadString(derivation, "unit") ?? string.Empty);
    }

    private static IReadOnlyList<TypedMemEvalGoldComponent>? ReadComponents(JsonElement block)
        => block.TryGetProperty("gold_components", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Select(c => new TypedMemEvalGoldComponent(
                    ReadString(c, "kind") ?? "component",
                    c.TryGetProperty("session_index", out var index) ? index.GetInt32() : -1))
                .ToArray()
            : null;
}
