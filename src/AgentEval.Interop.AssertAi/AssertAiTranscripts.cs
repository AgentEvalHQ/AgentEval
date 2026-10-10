// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;

namespace AgentEval.Interop.AssertAi;

/// <summary>
/// Reads the conversations of an ASSERT run (<c>inference_set.jsonl</c>) as AgentEval inputs, so AgentEval's own
/// checks (deterministic tool checks, its calibrated judges) can run on exactly what ASSERT's judge saw and sit beside
/// <see cref="AssertAiVerdictEval"/> in one composite.
/// </summary>
/// <remarks>
/// Only events in ASSERT's target view are read: what the judge sees. The system message becomes
/// <see cref="EvalInput.SystemMessage"/>, the last user message <see cref="EvalInput.Query"/>, the last assistant message
/// <see cref="EvalInput.Response"/>, and every tool-call event a <see cref="ToolCall"/> with its arguments and result.
/// Each input's <see cref="EvalInput.CaseId"/> is <c>type:test_case_id</c>, which <see cref="AssertAiVerdictEval"/>
/// looks up; earlier turns are in <see cref="EvalInput.Metadata"/> under <see cref="TurnsMetadataKey"/>.
/// </remarks>
public static class AssertAiTranscripts
{
    /// <summary>The metadata key holding every (role, text) message of the conversation, in order.</summary>
    public const string TurnsMetadataKey = "assert_ai.turns";

    /// <summary>Reads every conversation of an <c>inference_set.jsonl</c> file.</summary>
    public static IReadOnlyList<(AssertAiCaseKey Key, EvalInput Input)> Read(string inferenceSetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inferenceSetPath);
        return AssertAiJson.ReadJsonLines(inferenceSetPath).Select(r => ToEvalInput(r.Row)).ToList();
    }

    /// <summary>One <c>inference_set.jsonl</c> row as an input.</summary>
    public static (AssertAiCaseKey Key, EvalInput Input) ToEvalInput(JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var key = new AssertAiCaseKey(AssertAiJson.Str(row["type"]) ?? string.Empty, AssertAiJson.Str(row["test_case_id"]) ?? string.Empty);
        string? system = null, lastUser = null, lastAssistant = null;
        var turns = new List<(string Role, string Text)>();
        var calls = new List<ToolCall>();
        foreach (var item in row["events"] as JsonArray ?? [])
        {
            if (item is not JsonObject e || !InTargetView(e["view"]) || e["edit"] is not JsonObject edit)
            {
                continue;
            }

            switch (AssertAiJson.Str(edit["type"]))
            {
                case "set_system_message":
                    system = AssertAiJson.Str(edit["message"]?["content"]);
                    break;
                case "add_message":
                    var role = AssertAiJson.Str(edit["message"]?["role"]) ?? string.Empty;
                    var text = AssertAiJson.Str(edit["message"]?["content"]) ?? string.Empty;
                    if (role == "assistant" && text.Length == 0)
                    {
                        continue;   // ASSERT's placeholder for an endpoint's tool call
                    }

                    turns.Add((role, text));
                    if (role == "user") lastUser = text;
                    if (role == "assistant") lastAssistant = text;
                    break;
                case "tool_call":
                    calls.Add(new ToolCall(
                        AssertAiJson.Str(edit["tool_name"]) ?? "tool",
                        edit["tool_args"] is JsonObject args ? args.ToDictionary(kv => kv.Key, kv => (object)JsonSerializer.SerializeToElement(kv.Value)) : null,
                        AssertAiJson.Str(edit["tool_result"])));
                    break;
            }
        }

        var input = new EvalInput(
            Query: lastUser ?? string.Empty,
            Response: lastAssistant,
            ToolCalls: calls,
            SystemMessage: system,
            Metadata: new Dictionary<string, object> { [TurnsMetadataKey] = turns })
        {
            CaseId = key.ToString(),
        };
        return (key, input);
    }

    private static bool InTargetView(JsonNode? view) => view switch
    {
        JsonArray list => list.Any(v => AssertAiJson.Str(v) == "target"),
        _ => AssertAiJson.Str(view) == "target",
    };
}
