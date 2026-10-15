// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Interop;

/// <summary>
/// EvalPort run-level isolation and group declarations, preserved without inferring
/// execution isolation, complete membership, or relationships between separate runs.
/// This validates only context fields, not a complete EvalPort ResultSet.
/// </summary>
public sealed class EvalPortRunContext
{
    /// <summary>The extension filename relative to the AEF ext directory.</summary>
    public const string ExtensionFile = "evalport-context.json";
    /// <summary>Maximum context document size.</summary>
    public const int MaxBytes = 64 * 1024;
    private readonly JsonObject _fields;
    private EvalPortRunContext(JsonObject fields) => _fields = fields;

    /// <summary>Null means undeclared; open and empty strings are preserved as declared.</summary>
    public string? Isolation => _fields["isolation"]?.GetValue<string>();
    /// <summary>A detached group object, or null when membership is undeclared.</summary>
    public JsonObject? Group => _fields["group"]?.DeepClone().AsObject();

    /// <summary>
    /// Extracts only isolation/group from strict bounded JSON. Other ResultSet fields
    /// are outside this component's validation scope.
    /// </summary>
    public static EvalPortRunContext FromResultSet(ReadOnlySpan<byte> bytes)
    {
        var result = AefJsonReader.ParseDocument(bytes);
        var fields = new JsonObject();
        foreach (var key in new[] { "isolation", "group" })
            if (result.ContainsKey(key)) fields[key] = result[key]?.DeepClone();
        return ReadFields(fields);
    }

    /// <summary>Reads the sealed-extension format; parsing alone does not verify its seal.</summary>
    public static EvalPortRunContext Parse(ReadOnlySpan<byte> bytes)
    {
        var root = AefJsonReader.ParseDocument(bytes, MaxBytes);
        Require(root["schemaVersion"] is JsonValue v && v.TryGetValue<string>(out var version) && version == "1.0", "extension schemaVersion 1.0");
        Require(root.All(p => p.Key is "schemaVersion" or "isolation" or "group"), "known extension fields");
        root.Remove("schemaVersion");
        return ReadFields(root);
    }

    /// <summary>Serializes the extension without changing sequence-number precision.</summary>
    public byte[] ToBytes()
    {
        var root = (JsonObject)_fields.DeepClone(); root["schemaVersion"] = "1.0";
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        Require(bytes.Length <= MaxBytes, "context at most 64 KiB");
        return bytes;
    }

    /// <summary>Writes the extension while the supplied run writer is still open; normal sealing covers it.</summary>
    public void WriteTo(AefRunWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.PutExtFile(ExtensionFile, ToBytes());
    }

    /// <summary>
    /// Returns a detached ResultSet with this context. Conflicting existing context
    /// is refused; neither argument is mutated. Absence is not filled with defaults.
    /// </summary>
    public JsonObject ApplyTo(JsonObject resultSet)
    {
        ArgumentNullException.ThrowIfNull(resultSet);
        foreach (var key in new[] { "isolation", "group" })
            if (resultSet.ContainsKey(key))
                Require(_fields.ContainsKey(key) && JsonNode.DeepEquals(resultSet[key], _fields[key]), "non-conflicting context");
        var result = (JsonObject)resultSet.DeepClone();
        foreach (var (key, value) in _fields) result[key] = value?.DeepClone();
        return result;
    }

    private static EvalPortRunContext ReadFields(JsonObject fields)
    {
        if (fields.ContainsKey("isolation")) String(fields["isolation"], false);
        if (fields.ContainsKey("group"))
        {
            Require(fields["group"] is JsonObject, "group object");
            var group = fields["group"]!.AsObject();
            Require(group.All(p => p.Key is "group_id" or "parent_group_id" or "role" or "label" or "sequence"), "known group fields");
            var id = String(group["group_id"], true);
            if (group.ContainsKey("parent_group_id")) Require(String(group["parent_group_id"], true) != id, "parent different from group_id");
            foreach (var key in new[] { "role", "label" }) if (group.ContainsKey(key)) String(group[key], false);
            if (group.ContainsKey("sequence")) Require(IsNonnegativeInteger(group["sequence"]), "nonnegative integer sequence");
        }
        var context = new EvalPortRunContext(fields);
        _ = context.ToBytes();
        return context;
    }

    private static string String(JsonNode? node, bool nonempty)
    {
        Require(node is JsonValue value && value.TryGetValue<string>(out var text) && (!nonempty || text.Length > 0), "string context field");
        return node!.GetValue<string>();
    }

    // Check exact JSON decimal integrality, never a rounded binary64 value.
    private static bool IsNonnegativeInteger(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
        var raw = node.ToJsonString();
        var parts = raw.Split(['e', 'E']);
        var mantissa = parts[0]; var dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('-');
        if (digits.All(c => c == '0')) return true;
        if (mantissa[0] == '-') return false;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return false;
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var scale = (long)(dot < 0 ? 0 : mantissa.Length - dot - 1) - exponent;
        return scale <= 0 || (scale <= digits.Length && digits.AsSpan(digits.Length - (int)scale).IndexOfAnyExcept('0') < 0);
    }

    private static void Require(bool condition, string expected)
    {
        if (!condition) throw new AefInputException($"Expected: {expected}. Actual: incompatible EvalPort run context. " +
            "Suggestions: preserve valid run-level declarations. Because: context must not be inferred or silently changed.");
    }
}
