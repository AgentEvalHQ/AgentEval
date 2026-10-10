using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Schemas;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Schemas;

/// <summary>
/// <see cref="AefSchemaValidator"/> against JsonSchema.Net, an independent validator: on every corpus document they
/// agree, except where AEF asks for something JsonSchema.Net does not do. Each such divergence has its own test below,
/// and the corpus comparison lists the documents where one shows.
/// </summary>
public class JsonSchemaNetComparisonTests
{
    // The corpus documents on which the two validators disagree, and why (one divergence test below each). Nothing
    // else may differ, and each of these that the corpus holds must differ.
    private static readonly Dictionary<string, string> KnownDivergences = new(StringComparer.Ordinal)
    {
        // [ENC-8]: 2026-02-31 matches the timestamp pattern; only AEF's date check refuses it.
        ["runs/time-does-not-exist/run/run.json run writer"] = "ENC-8",
        ["runs/time-does-not-exist/run/run.json run reader"] = "ENC-8",
        ["decision-vectors/60-input-time-does-not-exist.json#input decision#/$defs/input writer"] = "ENC-8",
        ["decision-vectors/60-input-time-does-not-exist.json#input decision#/$defs/input reader"] = "ENC-8",

        // [ENC-4]: 129 code points over a maxLength of 128, but 65 graphemes.
        ["documents/length-combining-over-max/document.json run writer"] = "ENC-4 lengths",
        ["documents/length-combining-over-max/document.json run reader"] = "ENC-4 lengths",

        // [ENC-4]: 1.00000000000000000001 is above a maximum of 1 as a decimal, and is exactly 1 as binary64.
        ["documents/number-above-max-as-decimal-equal-as-binary64/document.json run writer"] = "ENC-4 numbers",
        ["documents/number-above-max-as-decimal-equal-as-binary64/document.json run reader"] = "ENC-4 numbers",
    };

    [Fact]
    public void OnEveryCorpusDocument_TheValidatorsAgree_ExceptTheKnownDivergences()
    {
        var differences = new List<string>();
        var unexpected = new List<string>();
        var beyondDecimal = 0;
        foreach (var (id, schema, document) in CorpusDocuments.All)
        {
            foreach (var (side, ours, theirs) in new[]
                     {
                         ("writer", AefSchemas.Writer, JsonSchemaNetSet.Writer.Value),
                         ("reader", AefSchemas.Reader, JsonSchemaNetSet.Reader.Value),
                     })
            {
                var mine = ours.IsValid(schema, document);
                bool other;
                try
                {
                    other = theirs.IsValid(schema, document);
                }
                catch (FormatException)
                {
                    // JsonSchema.Net reads a number as a decimal, and cannot read one beyond its range (a score of 1.5e308,
                    // a sum of 1e200, [SUM-5]): such a document is not compared (Divergence_ANumberBeyondDecimal below).
                    beyondDecimal++;
                    continue;
                }

                if (mine != other)
                {
                    var key = $"{id} {schema} {side}";
                    differences.Add(key);
                    if (!KnownDivergences.ContainsKey(key))
                    {
                        unexpected.Add($"{key}: AefSchemaValidator says {(mine ? "valid" : "invalid")}, JsonSchema.Net the opposite. {ours.Validate(schema, document)}");
                    }
                }
            }
        }

        Assert.True(CorpusDocuments.All.Count > 500, $"only {CorpusDocuments.All.Count} corpus documents found");
        Assert.True(beyondDecimal < 20, $"{beyondDecimal} documents JsonSchema.Net cannot read: more than the few that hold numbers beyond a decimal");
        Assert.True(unexpected.Count == 0, string.Join("\n", unexpected));

        // A known divergence whose document is in the corpus still shows (the list documents the corpus, not hopes).
        var held = CorpusDocuments.All.Select(d => $"{d.Id} {d.Schema}").ToHashSet(StringComparer.Ordinal);
        foreach (var key in KnownDivergences.Keys.Where(k => held.Contains(k[..k.LastIndexOf(' ')])))
        {
            Assert.Contains(key, differences);
        }

        Assert.Contains("runs/time-does-not-exist/run/run.json run writer", differences);
    }

    [Fact]
    public void Divergence_ADateThatDoesNotExist_ENC8()
    {
        var time = JsonValue.Create("2026-02-31T00:00:00Z");

        Assert.True(JsonSchemaNetSet.Writer.Value.IsValid("common#/$defs/timestamp", time));    // format is not asserted ([ENC-16])
        Assert.False(AefSchemas.Writer.IsValid("common#/$defs/timestamp", time));
    }

    [Fact]
    public void Divergence_DollarBeforeAFinalNewline_ENC15()
    {
        const string Schema = """{"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "string", "pattern": "^[!-~]{1,128}$"}""";
        var value = JsonValue.Create("v1\n");

        Assert.True(JsonSchemaNetSet.IsValidAgainst(Schema, value));   // .NET's $ also matches before a final \n
        Assert.False(Ours(Schema, value));
    }

    [Fact]
    public void Divergence_StringLengthInGraphemesNotCodePoints()
    {
        const string Schema = """{"$schema": "https://json-schema.org/draft/2020-12/schema", "maxLength": 1}""";
        var value = JsonValue.Create("e\u0301");   // one grapheme, two code points

        Assert.True(JsonSchemaNetSet.IsValidAgainst(Schema, value));
        Assert.False(Ours(Schema, value));
    }

    [Fact]
    public void Divergence_NumbersComparedAsDecimalNotBinary64_ENC4()
    {
        const string Maximum = """{"$schema": "https://json-schema.org/draft/2020-12/schema", "maximum": 1}""";
        const string Integer = """{"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "integer"}""";
        var value = AefJsonReader.ParseValue("1.0000000000000001"u8);   // the binary64 value 1

        Assert.False(JsonSchemaNetSet.IsValidAgainst(Maximum, value));
        Assert.True(Ours(Maximum, value));
        Assert.False(JsonSchemaNetSet.IsValidAgainst(Integer, value));
        Assert.True(Ours(Integer, value));
    }

    [Fact]
    public void Divergence_ANumberBeyondDecimal_IsOneJsonSchemaNetCannotRead_ENC4()
    {
        // Whether a number is an integer JsonSchema.Net asks of a decimal, which 1e200, a binary64 value, is beyond.
        const string Integer = """{"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "integer"}""";
        var value = AefJsonReader.ParseValue("1e200"u8);

        Assert.Throws<FormatException>(() => JsonSchemaNetSet.IsValidAgainst(Integer, value));
        Assert.True(Ours(Integer, value));
    }

    private static bool Ours(string schema, JsonNode? value) =>
        AefSchemaValidator.Load([new("t.schema.json", Encoding.UTF8.GetBytes(schema))]).IsValid("t", value);
}
