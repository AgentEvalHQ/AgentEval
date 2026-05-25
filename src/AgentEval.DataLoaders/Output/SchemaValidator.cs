// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;

namespace AgentEval.Output;

/// <summary>
/// Loads and applies the embedded JSON-Schema resources that ship with AgentEval.DataLoaders.
/// Promoted from <c>internal</c> to <c>public</c> in v1.1 / plan-13 T1.6 so the CLI's
/// <c>agenteval doctor</c> command can re-validate persisted files at read time, not only
/// during the write path inside <see cref="IOutputStore"/> implementations.
/// </summary>
/// <remarks>
/// Two surfaces:
/// <list type="bullet">
///   <item><see cref="ValidateOrThrow{T}"/> — write-path: serialise an in-memory value to JSON
///         and reject the write when the result violates the schema.</item>
///   <item><see cref="ValidateFile(string, string)"/> — read-path: parse a file on disk and
///         return a structured outcome the caller can render without throwing on first error
///         (so <c>doctor</c> can aggregate multiple findings before exiting).</item>
/// </list>
/// </remarks>
public static class SchemaValidator
{
    private static readonly Dictionary<string, JsonSchema> s_cache = new();
    private static readonly object s_lock = new();

    private static readonly JsonSerializerOptions s_serOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Validates <paramref name="value"/> against the embedded JSON Schema resource identified by <paramref name="schemaResourceName"/>,
    /// throwing <see cref="InvalidOperationException"/> when validation fails.</summary>
    public static void ValidateOrThrow<T>(T value, string schemaResourceName)
    {
        var schema = LoadSchema(schemaResourceName);
        var json = JsonSerializer.Serialize(value, s_serOpts);
        var node = JsonNode.Parse(json);
        var opts = new Json.Schema.EvaluationOptions
        {
            OutputFormat = Json.Schema.OutputFormat.List
        };
        var result = schema.Evaluate(node, opts);
        if (!result.IsValid)
        {
            var errors = string.Join("; ", result.Details
                .Where(d => !d.IsValid)
                .SelectMany(d => d.Errors?.Select(e => $"{d.EvaluationPath} {e.Key}={e.Value}") ?? Array.Empty<string>()));
            throw new InvalidOperationException($"Schema validation failed for {schemaResourceName}: {errors}");
        }
    }

    /// <summary>
    /// Validates a JSON file at <paramref name="filePath"/> against the embedded schema
    /// resource <paramref name="schemaResourceName"/>. Returns the outcome — never throws.
    /// Used by <c>agenteval doctor</c> to aggregate multiple findings before exiting.
    /// </summary>
    /// <returns>
    /// A tuple of (<c>IsValid</c>, <c>ErrorMessage</c>). <c>ErrorMessage</c> is non-null
    /// only when <c>IsValid</c> is false. When the file is missing or parse fails before
    /// schema evaluation, returns an explanatory error message.
    /// </returns>
    public static (bool IsValid, string? ErrorMessage) ValidateFile(string filePath, string schemaResourceName)
    {
        if (!File.Exists(filePath))
            return (false, $"file not found: {filePath}");

        JsonNode? node;
        try
        {
            using var stream = File.OpenRead(filePath);
            node = JsonNode.Parse(stream);
        }
        catch (Exception ex)
        {
            return (false, $"parse error: {ex.GetType().Name}: {ex.Message}");
        }

        if (node is null)
            return (false, "parse error: file is empty or returned null root node");

        try
        {
            var schema = LoadSchema(schemaResourceName);
            var opts = new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List };
            var result = schema.Evaluate(node, opts);
            if (result.IsValid) return (true, null);

            var errors = string.Join("; ", result.Details
                .Where(d => !d.IsValid)
                .SelectMany(d => d.Errors?.Select(e => $"{d.EvaluationPath} {e.Key}={e.Value}") ?? Array.Empty<string>()));
            return (false, $"schema {schemaResourceName}: {errors}");
        }
        catch (Exception ex)
        {
            return (false, $"schema-load error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static JsonSchema LoadSchema(string resourceName)
    {
        lock (s_lock)
        {
            if (s_cache.TryGetValue(resourceName, out var cached)) return cached;
            var asm = typeof(SchemaValidator).Assembly;
            var fullName = asm.GetManifestResourceNames()
                .Single(n => n.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase));
            using var stream = asm.GetManifestResourceStream(fullName)!;
            using var reader = new StreamReader(stream);
            var schema = JsonSchema.FromText(reader.ReadToEnd());
            s_cache[resourceName] = schema;
            return schema;
        }
    }
}
