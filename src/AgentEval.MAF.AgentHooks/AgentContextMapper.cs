// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.MAF.Gatekeeper;
using AgentHooks;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.AgentHooks;

/// <summary>
/// Translates an AGENT-HOOKS-0.1 <see cref="AgentContext"/> into the <see cref="GatedToolCall"/> that
/// Gatekeeper's tool gates inspect.
/// </summary>
/// <remarks>
/// <para>Field names follow AGENT-HOOKS-0.1 §4: <c>tool_call = { id, name, args }</c> (required at
/// <c>pre_tool_call</c>), <c>agent = { id, framework, name?, version? }</c>, and an OPTIONAL
/// <c>messages = [ { role, content } ]</c>.</para>
///
/// <para><b>Known fidelity limits, stated rather than hidden.</b> Two of Gatekeeper's gates recompute from
/// the conversation: <c>ReferentialIntegrityGate</c> (an id must have been surfaced by a user turn or a
/// trusted tool result) and <c>TaintTrackingGate</c> (source-result tokens flowing to sink args). The spec
/// makes <c>messages</c> optional, so a host that omits it leaves those gates with nothing to correlate
/// against. They then find nothing — which is a COVERAGE GAP, not a clean pass. Callers must not read an
/// allow from a context without <c>messages</c> as evidence that those gates were satisfied; see
/// <see cref="HasConversation"/>.</para>
///
/// <para><c>Iteration</c>, <c>FunctionCallIndex</c>, <c>FunctionCount</c> and <c>IsStreaming</c> have no
/// counterpart in the spec context. They are set to first-call, non-streaming defaults. Gates that key on
/// them (none of the shipped 17 do so decisively) would behave as if every call were the first of its
/// iteration.</para>
/// </remarks>
public static class AgentContextMapper
{
    /// <summary>
    /// Builds a <see cref="GatedToolCall"/> from a <c>pre_tool_call</c> context, or <see langword="null"/>
    /// if the context carries no readable <c>tool_call.name</c>.
    /// </summary>
    public static GatedToolCall? ToGatedToolCall(AgentContext context)
    {
        // No null guard: AgentHooks.AgentContext is a struct, so a ThrowIfNull here could never fire.
        var toolCall = context.Json["tool_call"] as JsonObject;
        var name = (toolCall?["name"] as JsonValue)?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new GatedToolCall(
            FunctionName: name,
            Arguments: ToArguments(toolCall?["args"] as JsonObject),
            AgentName: AgentName(context),
            Iteration: 0,
            FunctionCallIndex: 0,
            FunctionCount: 1,
            IsStreaming: false,
            Messages: ToMessages(context.Json["messages"] as JsonArray));
    }

    /// <summary>
    /// Whether the context carried a non-empty <c>messages</c> array. When false, conversation-correlating
    /// gates could not have found anything, and their silence is a coverage gap rather than a pass.
    /// </summary>
    public static bool HasConversation(AgentContext context)
    {
        // No null guard: AgentContext is a struct (see ToGatedToolCall).
        return context.Json["messages"] is JsonArray { Count: > 0 };
    }

    /// <summary>Converts a JSON args object into the dictionary shape gates expect.</summary>
    private static IReadOnlyDictionary<string, object?>? ToArguments(JsonObject? args)
    {
        if (args is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            result[key] = ToClrValue(value);
        }

        return result;
    }

    /// <summary>
    /// Scalars become CLR primitives so string-matching gates see the literal value; objects and arrays are
    /// kept as <see cref="JsonNode"/>, which round-trips through <c>JsonSerializer</c> unchanged so that
    /// argument-serializing gates (e.g. <c>ArgumentPatternGate</c>) match against faithful JSON.
    /// </summary>
    /// <remarks>
    /// Dispatches on <see cref="JsonNode.GetValueKind()"/> — the JSON kind — NOT on
    /// <c>JsonValue.TryGetValue&lt;T&gt;</c>. <c>TryGetValue&lt;T&gt;</c> keys on the node's CLR <em>storage</em>
    /// type, which varies with how the node was constructed (primitive-backed vs <c>JsonElement</c>-backed),
    /// so a string built one way converts and the same string built another way silently falls through to the
    /// default arm. That produced a real defect: gate arguments arrived as <see cref="JsonNode"/> instead of
    /// <see cref="string"/>, so every value-matching gate compared against a node reference rather than the
    /// literal — matching nothing, and reading as a clean allow. Fails-open by omission is exactly the class of
    /// bug this adapter must not have, so the predicate is now the JSON kind, which is construction-agnostic.
    /// </remarks>
    private static object? ToClrValue(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        switch (node.GetValueKind())
        {
            case JsonValueKind.String:
                return node.GetValue<string>();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return node.GetValue<bool>();
            case JsonValueKind.Number:
                // Read the canonical JSON number text rather than TryGetValue<T>, for the same
                // storage-type-independence reason. Integral values become long so gates comparing against
                // whole numbers do not see a stray trailing ".0".
                var number = node.ToJsonString();
                // The (object) cast is load-bearing: without it the conditional unifies to the best common
                // type (double), so an integral value would silently box as a double and 42 would arrive as
                // 42.0. Caught by ArgumentScalars_ArriveAsClrPrimitives_NotJsonNodes.
                return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? (object)l
                    : double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            default:
                // Objects and arrays keep their JsonNode form; they round-trip through JsonSerializer
                // unchanged so argument-serializing gates still match against faithful JSON.
                return node;
        }
    }

    /// <summary>Maps the optional spec <c>messages</c> array onto MAF chat messages.</summary>
    private static IReadOnlyList<ChatMessage>? ToMessages(JsonArray? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var result = new List<ChatMessage>(messages.Count);
        foreach (var node in messages)
        {
            if (node is not JsonObject message)
            {
                continue;
            }

            var role = ToClrValue(message["role"]) as string ?? "user";
            // Same storage-type-independence rule as ToClrValue: dispatch on JSON kind, not CLR backing.
            // §4 allows content to be "string | object"; non-strings are serialized so nothing is lost.
            var content = ToClrValue(message["content"]) switch
            {
                string s => s,
                null => string.Empty,
                JsonNode other => other.ToJsonString(),
                var scalar => scalar.ToString() ?? string.Empty
            };

            result.Add(new ChatMessage(ToChatRole(role), content));
        }

        return result.Count == 0 ? null : result;
    }

    private static ChatRole ToChatRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User
    };

    /// <summary>Serializes rewritten gate arguments back into a JSON object for a spec transform.</summary>
    internal static JsonObject? ToJsonObject(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            result[key] = value switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString())
            };
        }

        return result;
    }

    private static string? AgentName(AgentContext context)
    {
        var agent = context.Json["agent"] as JsonObject;
        return (agent?["name"] as JsonValue)?.GetValue<string>()
            ?? (agent?["id"] as JsonValue)?.GetValue<string>();
    }
}
