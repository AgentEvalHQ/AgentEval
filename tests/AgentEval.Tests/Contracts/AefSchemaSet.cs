// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// One set of AEF 1.0 schemas (writer or reader), registered together so their relative $refs resolve, with every
/// pattern ending at the end of the input ([ENC-15]).
/// </summary>
internal sealed class AefSchemaSet
{
    public static readonly string Root = Path.Combine(RepoRoot(), "contracts", "aef", "1");

    public static readonly Lazy<AefSchemaSet> Writer = new(() => Load(Path.Combine(Root, "schemas", "writer")));

    public static readonly Lazy<AefSchemaSet> Reader = new(() => Load(Path.Combine(Root, "schemas", "reader")));

    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);
    // Format assertion off: in AEF the patterns are the rule and format is an annotation ([ENC-16]), so the corpus is
    // checked the way a validator that ignores format checks it.
    private readonly EvaluationOptions _options = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = false };
    private string _base = "";

    public static AefSchemaSet Load(string dir)
    {
        var set = new AefSchemaSet { _base = dir.Contains("reader", StringComparison.Ordinal) ? "reader" : "writer" };
        foreach (var file in Directory.GetFiles(dir, "*.schema.json"))
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            EndPatternsAtEndOfInput(node);
            var schema = JsonSchema.FromText(node!.ToJsonString());
            set._options.SchemaRegistry.Register(schema);
            set._schemas[Path.GetFileName(file)[..^".schema.json".Length]] = schema;
        }

        return set;
    }

    /// <summary>
    /// A pattern's final '$' as '\z' ([ENC-15], spec 02): '$' is the end of the input, as in ECMA-262 and RE2. .NET's '$'
    /// also matches before a final '\n', so without this a value such as "r-1\n" would pass "^[!-~]{1,128}$". An escaped
    /// '\$' is a dollar sign and stays.
    /// </summary>
    internal static string AtEndOfInput(string pattern)
    {
        var backslashes = pattern.Length - 1 - pattern.AsSpan(0, Math.Max(0, pattern.Length - 1)).TrimEnd('\\').Length;
        return pattern.EndsWith('$') && backslashes % 2 == 0 ? pattern[..^1] + "\\z" : pattern;
    }

    // Every pattern is compiled with ENC-15's '$', whatever JsonSchema.Net does with '$' natively.
    private static void EndPatternsAtEndOfInput(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "pattern" && value is JsonValue v && v.TryGetValue<string>(out var pattern))
                        obj[key] = AtEndOfInput(pattern);
                    else
                        EndPatternsAtEndOfInput(value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                    EndPatternsAtEndOfInput(item);
                break;
        }
    }

    /// <summary>Validates against a schema by name ("run"), or a subschema of one ("decision#/$defs/input").</summary>
    public bool IsValid(string name, JsonNode? document, out string errors)
    {
        var hash = name.IndexOf('#', StringComparison.Ordinal);
        var schema = hash < 0
            ? _schemas[name]
            : JsonSchema.FromText($$"""{"$ref": "https://agenteval.dev/aef/1/{{_base}}/{{name[..hash]}}.schema.json{{name[hash..]}}"}""");
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
