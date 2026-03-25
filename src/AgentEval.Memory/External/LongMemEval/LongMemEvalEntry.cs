// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// A single question entry from the LongMemEval dataset.
/// Supports Oracle, S (small ~115K tokens), and M (medium ~500 sessions) formats.
/// </summary>
public class LongMemEvalEntry
{
    [JsonPropertyName("question_id")]
    public string QuestionId { get; set; } = "";

    [JsonPropertyName("question_type")]
    public string QuestionType { get; set; } = "";

    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [JsonPropertyName("answer")]
    public JsonElement AnswerRaw { get; set; }

    /// <summary>Answer as string, handling int/bool/array values in the dataset.</summary>
    [JsonIgnore]
    public string Answer => AnswerRaw.ValueKind switch
    {
        JsonValueKind.String => AnswerRaw.GetString() ?? "",
        JsonValueKind.Number => AnswerRaw.GetRawText(),
        _ => AnswerRaw.GetRawText()
    };

    /// <summary>Whether this is an abstention question (identified by _abs suffix).</summary>
    [JsonIgnore]
    public bool IsAbstention => QuestionId.Contains("_abs");

    [JsonPropertyName("question_date")]
    public string? QuestionDate { get; set; }

    [JsonPropertyName("haystack_sessions")]
    public List<List<LongMemEvalTurn>>? HaystackSessions { get; set; }

    [JsonPropertyName("haystack_dates")]
    public List<string>? HaystackDates { get; set; }

    [JsonPropertyName("haystack_session_ids")]
    public List<string>? HaystackSessionIds { get; set; }

    [JsonPropertyName("answer_session_ids")]
    public List<string>? AnswerSessionIds { get; set; }
}

/// <summary>
/// A single turn (message) within a LongMemEval conversation session.
/// </summary>
public class LongMemEvalTurn
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("has_answer")]
    public bool? HasAnswer { get; set; }
}
