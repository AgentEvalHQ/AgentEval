// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;

namespace AgentEval.Output;

internal static class SchemaValidator
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
