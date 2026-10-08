// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentEval.Tests.Contracts;

/// <summary>One set of AEF v2 schemas (writer or reader), registered together so their relative $refs resolve.</summary>
internal sealed class AefSchemaSet
{
    public static readonly string V2 = Path.Combine(RepoRoot(), "contracts", "aef", "v2");

    public static readonly Lazy<AefSchemaSet> Writer = new(() => Load(Path.Combine(V2, "schemas", "writer")));

    public static readonly Lazy<AefSchemaSet> Reader = new(() => Load(Path.Combine(V2, "schemas", "reader")));

    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);
    // Format assertion off: in AEF the patterns are the rule and format is an annotation, so the corpus is checked the
    // way a validator that ignores format checks it.
    private readonly EvaluationOptions _options = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = false };
    private string _base = "";

    public static AefSchemaSet Load(string dir)
    {
        var set = new AefSchemaSet { _base = dir.Contains("reader", StringComparison.Ordinal) ? "reader" : "writer" };
        foreach (var file in Directory.GetFiles(dir, "*.schema.json"))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file));
            set._options.SchemaRegistry.Register(schema);
            set._schemas[Path.GetFileName(file)[..^".schema.json".Length]] = schema;
        }

        return set;
    }

    /// <summary>Validates against a schema by name ("run"), or a subschema of one ("decision#/$defs/input").</summary>
    public bool IsValid(string name, JsonNode? document, out string errors)
    {
        var hash = name.IndexOf('#', StringComparison.Ordinal);
        var schema = hash < 0
            ? _schemas[name]
            : JsonSchema.FromText($$"""{"$ref": "https://agenteval.dev/aef/v2/{{_base}}/{{name[..hash]}}.schema.json{{name[hash..]}}"}""");
        var result = schema.Evaluate(document, _options);
        errors = result.IsValid
            ? ""
            : string.Join("; ", (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} {e.Key}: {e.Value}")).Take(8));
        return result.IsValid;
    }

    /// <summary>The repository root (the folder that holds contracts/aef).</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contracts", "aef")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("contracts/aef was not found above the test directory.");
    }
}
