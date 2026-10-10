using System.Text.Json.Nodes;
using AgentEval.Results.Schemas;
using AgentEval.Results.Tests.Corpus;
using Json.Schema;

namespace AgentEval.Results.Tests.Schemas;

/// <summary>
/// The AEF schemas under JsonSchema.Net, a second, independent validator to compare <see cref="AefSchemaValidator"/>
/// with (test code only). Configured as AEF asks of any validator: format is not asserted ([ENC-16]) and, unless told
/// otherwise, every pattern's final <c>$</c> is <c>\z</c> ([ENC-15]); what it still does differently is what the
/// divergence tests pin down.
/// </summary>
internal sealed class JsonSchemaNetSet
{
    public static readonly Lazy<JsonSchemaNetSet> Writer = new(() => Load("writer"));

    public static readonly Lazy<JsonSchemaNetSet> Reader = new(() => Load("reader"));

    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);
    private readonly EvaluationOptions _options = new() { OutputFormat = OutputFormat.Flag, RequireFormatValidation = false };
    private string _side = "";

    public static JsonSchemaNetSet Load(string side)
    {
        var set = new JsonSchemaNetSet { _side = side };
        foreach (var file in Directory.GetFiles(Path.Combine(AefCorpus.Aef, "schemas", side), "*.schema.json"))
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            EndPatternsAtEndOfInput(node);
            var schema = JsonSchema.FromText(node!.ToJsonString());
            set._options.SchemaRegistry.Register(schema);
            set._schemas[Path.GetFileName(file)[..^".schema.json".Length]] = schema;
        }

        return set;
    }

    /// <summary>One schema, as written (no <c>\z</c>), for the divergence tests.</summary>
    public static bool IsValidAgainst(string schemaJson, JsonNode? instance, bool assertFormat = false) =>
        JsonSchema.FromText(schemaJson)
            .Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.Flag, RequireFormatValidation = assertFormat })
            .IsValid;

    public bool IsValid(string name, JsonNode? document)
    {
        var hash = name.IndexOf('#', StringComparison.Ordinal);
        var schema = hash < 0
            ? _schemas[name]
            : JsonSchema.FromText($$"""{"$ref": "https://agenteval.dev/aef/1/{{_side}}/{{name[..hash]}}.schema.json{{name[hash..]}}"}""");
        return schema.Evaluate(document, _options).IsValid;
    }

    private static void EndPatternsAtEndOfInput(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "pattern" && value is JsonValue v && v.TryGetValue<string>(out var pattern) && pattern.EndsWith('$'))
                        obj[key] = pattern[..^1] + "\\z";
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
}
