// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Evals;

/// <summary>
/// Glass Box Part 2 (P2.A1): the bridge that lets a Glass Box <see cref="AgentTrace"/> ride along on an
/// <see cref="EvalInput"/> so trace-aware evaluators can read what actually happened at the chat/tool boundary.
/// <para>
/// The trace is carried as an <see cref="EvalInput.Metadata"/> entry under
/// <see cref="EvalInput.TraceMetadataKey"/> (a string key, because the Abstractions assembly that owns
/// <see cref="EvalInput"/> cannot reference the Core <see cref="AgentTrace"/> type). These extensions live in
/// <c>AgentEval.Core</c> — the assembly that owns the trace type — and share <see cref="EvalInput"/>'s
/// namespace so any code already using <see cref="EvalInput"/> gets them without an extra <c>using</c>.
/// </para>
/// <para>
/// NOTE: qualify <c>AgentEval.Tracing.AgentTrace</c> explicitly — a second, unrelated
/// <c>AgentEval.Abstractions.Output.AgentTrace</c> record exists (the output-store artifact shape).
/// </para>
/// <para>
/// <b>Where it is attached.</b> <c>agenteval bench agentic --trace</c> attaches the run's trace; other callers attach
/// it explicitly at their write site. The Glass Box checks read it via <see cref="GetTrace"/>, and <see cref="WithTrace"/>
/// also projects the trace's tool calls and tool definitions onto <see cref="EvalInput.ToolCalls"/> /
/// <see cref="EvalInput.ToolDefinitions"/> for the checks that read those (#203 review, B5).
/// </para>
/// </summary>
public static class EvalInputTraceAccessor
{
    /// <summary>
    /// Reads the Glass Box trace previously attached via <see cref="WithTrace"/>, or <c>null</c> when no
    /// trace is present (or a value of a different type was stored under the key).
    /// </summary>
    public static AgentTrace? GetTrace(this EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Metadata is not null
            && input.Metadata.TryGetValue(EvalInput.TraceMetadataKey, out var value))
        {
            return value as AgentTrace;
        }

        return null;
    }

    /// <summary>
    /// Returns a copy of <paramref name="input"/> with <paramref name="trace"/> attached under
    /// <see cref="EvalInput.TraceMetadataKey"/>. Copy-on-write: the original <see cref="EvalInput.Metadata"/>
    /// dictionary is never mutated (records are values); an existing trace under the key is replaced.
    /// This is the canonical write site — no framework path constructs an <see cref="EvalInput"/> with a
    /// trace already in scope, so callers (Phase-A evaluator harnesses, samples, traced test runs) attach it here.
    /// </summary>
    public static EvalInput WithTrace(this EvalInput input, AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(trace);

        // Preserve the source dictionary's key comparer when copying — a caller may use a case-insensitive
        // Metadata dictionary, and dropping the comparer would silently change key-lookup semantics on the copy.
        var metadata = input.Metadata switch
        {
            null => new Dictionary<string, object>(),
            Dictionary<string, object> dict => new Dictionary<string, object>(dict, dict.Comparer),
            _ => new Dictionary<string, object>(input.Metadata),
        };
        metadata[EvalInput.TraceMetadataKey] = trace;

        // The run's tool data comes with its trace, so the checks that read EvalInput.ToolCalls / ToolDefinitions see
        // what the agent was offered and did (#203 review, B5): before, only the trace-reading Glass Box checks could,
        // and unsafe_tool_use / tool_input_accuracy never ran on a traced run. Explicit values win; a trace that did not
        // capture tool data leaves them null ("not captured"), never an empty list ("none").
        return input with
        {
            Metadata = metadata,
            ToolCalls = input.ToolCalls ?? ToolCallsFrom(trace),
            ToolDefinitions = input.ToolDefinitions ?? ToolDefinitionsFrom(trace),
        };
    }

    /// <summary>
    /// The tool calls a trace recorded: the executed ones (<see cref="TraceEntryScope.ToolExecution"/>) when the trace
    /// has that layer — each with its recorded outcome (<see cref="ToolCall.Succeeded"/>, <see cref="ToolCall.Error"/>) —
    /// else the calls the model requested on its chat responses, with NO outcome: nothing observed them run, and a
    /// requested call's <c>Succeeded</c> is the type's default, not an observation. An EMPTY list means the trace recorded
    /// a complete chat layer — every request with its response — and no tool call happened; <see langword="null"/> means
    /// the trace did not capture tool calls: no chat-turn layer, or a request without its response, so nothing here can
    /// say whether (or which) calls were made.
    /// </summary>
    public static IReadOnlyList<ToolCall>? ToolCallsFrom(AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        static IEnumerable<TraceToolCall> Calls(IEnumerable<TraceEntry> entries) => entries
            .Where(e => e.ToolCalls is { Count: > 0 })
            .SelectMany(e => e.ToolCalls!);

        var executed = Calls(trace.Entries.Where(e => e.EffectiveScope == TraceEntryScope.ToolExecution))
            .Select(c => new ToolCall(c.Name, JsonObjectOrNull(c.Arguments), c.Result) { Succeeded = c.Succeeded, Error = c.Error })
            .ToList();
        if (executed.Count > 0)
            return executed;

        // Without the execution layer, the chat layer is the record — and only a COMPLETE one: every request the model
        // was sent must have its response (or error) under the same index, the pairing key capture writes. A request
        // without one (a cancelled stream, in-workflow capture that records no responses) says nothing about the calls
        // the model made in that turn, so the trace did not capture them: null, not "none" — and not the calls the other
        // turns happened to record, which could leave out the very call a check is looking for (#203 review, B6c-2).
        var chat = trace.Entries.Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn).ToList();
        var answered = chat.Where(e => e.Type == TraceEntryType.Response).Select(e => e.Index).ToHashSet();
        var requests = chat.Where(e => e.Type == TraceEntryType.Request).ToList();
        if (requests.Count == 0 || !requests.All(e => answered.Contains(e.Index)))
            return null;

        return Calls(chat.Where(e => e.Type == TraceEntryType.Response))
            .Select(c => new ToolCall(c.Name, JsonObjectOrNull(c.Arguments), c.Result))
            .ToList();
    }

    /// <summary>
    /// The tools a trace says the model was offered: the union, by name, of every chat request's definitions. Capture
    /// writes a definition in full once and then a name-only stub (no schema) on later requests (except under AuditGrade),
    /// so for each name the occurrence that HAS a schema is used, wherever it sits. An EMPTY list means the chat layer was
    /// recorded and no tool was offered; <see langword="null"/> means no chat-turn layer was captured.
    /// </summary>
    public static IReadOnlyList<ToolDefinition>? ToolDefinitionsFrom(AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var requests = trace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Request)
            .ToList();
        if (requests.Count == 0)
            return null;

        return requests
            .Where(e => e.ToolDefinitions is { Count: > 0 })
            .SelectMany(e => e.ToolDefinitions!)
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.ParametersSchema)) ?? g.First())
            .Select(d => new ToolDefinition(d.Name, d.Description, JsonObjectOrNull(d.ParametersSchema)))
            .ToList();
    }

    // A JSON object as plain CLR values (Dictionary / List / string / double / bool), the shapes the tool checks read.
    // Anything else — null, blank, malformed, not an object — is null: the check then knows it has no arguments/schema.
    private static IReadOnlyDictionary<string, object>? JsonObjectOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                ? (IReadOnlyDictionary<string, object>)ToClr(doc.RootElement)!
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static object? ToClr(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => e.EnumerateObject()
            .ToDictionary(p => p.Name, p => ToClr(p.Value)!, StringComparer.Ordinal),
        System.Text.Json.JsonValueKind.Array => e.EnumerateArray().Select(ToClr).ToList(),
        System.Text.Json.JsonValueKind.String => e.GetString(),
        System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null,
    };
}
