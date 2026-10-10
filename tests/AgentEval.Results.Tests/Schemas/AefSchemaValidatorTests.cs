using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Tests.Schemas;

/// <summary>
/// <see cref="AefSchemaValidator"/> on small schemas: the keywords AEF uses, the ones it refuses, and the choices AEF
/// pins down (binary64 numbers, code point lengths, [ENC-14]/[ENC-15] patterns, [ENC-8] dates).
/// </summary>
public class AefSchemaValidatorTests
{
    private const string Dialect = "\"$schema\": \"https://json-schema.org/draft/2020-12/schema\"";

    internal static AefSchemaValidator Set(params (string File, string Json)[] files) =>
        AefSchemaValidator.Load(files.Select(f => new KeyValuePair<string, byte[]>(f.File, Encoding.UTF8.GetBytes(f.Json))));

    internal static AefSchemaValidator One(string body) => Set(("t.schema.json", $"{{{Dialect}, {body}}}"));

    internal static JsonNode? Json(string text) => AefJsonReader.ParseValue(Encoding.UTF8.GetBytes(text));

    private static bool Valid(AefSchemaValidator validator, string instance, string name = "t") =>
        validator.IsValid(name, Json(instance));

    [Theory]
    [InlineData("\"else\": {}")]
    [InlineData("\"$comment\": \"x\"")]
    [InlineData("\"patternProperties\": {}")]
    [InlineData("\"prefixItems\": []")]
    [InlineData("\"unevaluatedProperties\": false")]
    [InlineData("\"dependentRequired\": {}")]
    [InlineData("\"propertyNames\": {}")]
    [InlineData("\"contains\": {}")]
    [InlineData("\"multipleOf\": 2")]
    [InlineData("\"default\": 1")]
    [InlineData("\"examples\": []")]
    [InlineData("\"$anchor\": \"a\"")]
    [InlineData("\"properties\": {\"a\": {\"else\": {}}}")]                 // nested
    [InlineData("\"$defs\": {\"d\": {\"maxContains\": 1}}")]                // in a definition
    [InlineData("\"anyOf\": [{\"type\": \"string\", \"contentMediaType\": \"x\"}]")]
    public void AKeywordAefDoesNotUse_IsRefusedWhenLoading(string body)
    {
        Assert.Throws<AefSchemaException>(() => One(body));
    }

    [Fact]
    public void AnotherDialect_IsRefused()
    {
        Assert.Throws<AefSchemaException>(() => Set(("t.schema.json", """{"$schema": "http://json-schema.org/draft-07/schema#"}""")));
        Assert.Throws<AefSchemaException>(() => Set(("t.schema.json", "{}")));
    }

    [Fact]
    public void AnIdBelowTheRoot_IsRefused()
    {
        Assert.Throws<AefSchemaException>(() => One("\"properties\": {\"a\": {\"$id\": \"https://x/y\"}}"));
    }

    [Theory]
    [InlineData("https://agenteval.dev/aef/1/writer/t.schema.json#/$defs/a")]   // a URI: never fetched ([ENC-12])
    [InlineData("../t.schema.json#/$defs/a")]
    [InlineData("#a")]                                                          // an anchor, not a pointer
    [InlineData("#/$defs/missing")]
    [InlineData("other.schema.json#/$defs/a")]
    public void ARefThatIsNotAPointerIntoTheSet_IsRefused(string reference)
    {
        Assert.Throws<AefSchemaException>(() => One($"\"$defs\": {{\"a\": {{\"type\": \"string\"}}}}, \"$ref\": \"{reference}\""));
    }

    [Fact]
    public void Refs_ResolveWithinAFileAndAcrossTheSet_AndApplyBesideTheirSiblings()
    {
        var set = Set(
            ("common.schema.json", "{" + Dialect + """, "$defs": {"id": {"type": "string", "pattern": "^[a-z]+$"}}}"""),
            ("t.schema.json", "{" + Dialect + """
                , "type": "object",
                 "properties": {"a": {"$ref": "common.schema.json#/$defs/id", "maxLength": 3}, "b": {"$ref": "#/properties/a"}}}
                """));

        Assert.True(Valid(set, """{"a": "abc", "b": "xyz"}"""));
        Assert.False(Valid(set, """{"a": "abcd"}"""));    // the sibling maxLength
        Assert.False(Valid(set, """{"a": "ABC"}"""));     // the referenced pattern
        Assert.False(Valid(set, """{"b": "1"}"""));       // through a ref to a ref
        Assert.True(set.IsValid("common#/$defs/id", Json("\"x\"")));
    }

    [Theory]
    [InlineData("^(?=a)b$")]          // lookahead
    [InlineData("^(?!a)b$")]
    [InlineData("^(?<=a)b$")]         // lookbehind
    [InlineData("^(?<n>a)$")]         // a named group
    [InlineData("^(?i)a$")]           // an inline option
    [InlineData("^(?>a+)$")]          // atomic
    [InlineData("^(a)\\1$")]          // a backreference
    [InlineData("^\\d+$")]            // Unicode digits in .NET, ASCII in ECMA-262
    [InlineData("^\\w+$")]
    [InlineData("^\\s$")]
    [InlineData("^a\\b$")]
    [InlineData("^\\p{L}$")]
    [InlineData("^a.b$")]             // '.' and line terminators
    [InlineData("^a*+$")]             // possessive
    [InlineData("^a$|^b$")]           // '$' before the end
    [InlineData("^[a-z-[aeiou]]$")]   // .NET class subtraction
    [InlineData("^[]a]$")]
    [InlineData("^[a-z$")]
    public void APatternOutsideThePortableSubset_IsRefused(string pattern)
    {
        Assert.Throws<AefSchemaException>(() => AefSchemaValidator.CompilePattern(pattern));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("abc\n", false)]   // $ is the end of the input ([ENC-15])
    [InlineData("ab\nc", false)]
    [InlineData("", false)]
    public void AFinalDollar_MatchesOnlyAtTheEndOfTheInput(string text, bool valid)
    {
        var set = One("\"type\": \"string\", \"pattern\": \"^[a-z]+$\"");

        Assert.Equal(valid, set.IsValid("t", JsonValue.Create(text)));
    }

    [Fact]
    public void AnEscapedDollar_IsADollarSign()
    {
        var set = One("\"pattern\": \"^a\\\\$\"");    // ^a\$: an a, then a dollar sign; no end anchor

        Assert.True(set.IsValid("t", JsonValue.Create("a$")));
        Assert.True(set.IsValid("t", JsonValue.Create("a$b")));
        Assert.False(set.IsValid("t", JsonValue.Create("a")));
    }

    [Fact]
    public void APattern_MatchesAnywhere_UnlessAnchored()
    {
        var set = One("\"pattern\": \"b\"");

        Assert.True(set.IsValid("t", JsonValue.Create("abc")));
        Assert.True(set.IsValid("t", JsonValue.Create(5)));   // pattern applies to strings only
    }

    [Theory]
    [InlineData("\"\\ud834\\udd1e\"", 1, true)]   // one code point, two UTF-16 units
    [InlineData("\"e\\u0301\"", 1, false)]         // two code points, one grapheme
    [InlineData("\"e\\u0301\"", 2, true)]
    [InlineData("\"abc\"", 2, false)]
    public void MaxLength_CountsCodePoints(string instance, int max, bool valid)
    {
        Assert.Equal(valid, Valid(One($"\"maxLength\": {max}"), instance));
    }

    [Fact]
    public void MinLength_CountsCodePoints()
    {
        var set = One("\"minLength\": 2");

        Assert.False(Valid(set, "\"\\ud834\\udd1e\""));
        Assert.True(Valid(set, "\"\\ud834\\udd1e\\ud834\\udd1e\""));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0000000000000001", true)]    // reads as the binary64 value 1 ([ENC-4])
    [InlineData("1.000000000000001", false)]
    [InlineData("0.5", true)]
    public void Bounds_CompareAsBinary64(string instance, bool valid)
    {
        Assert.Equal(valid, Valid(One("\"maximum\": 1"), instance));
    }

    [Fact]
    public void ExclusiveBounds_CompareAsBinary64()
    {
        var set = One("\"exclusiveMinimum\": 0, \"exclusiveMaximum\": 1");

        Assert.False(Valid(set, "0"));
        Assert.False(Valid(set, "1.0000000000000001"));
        Assert.True(Valid(set, "0.99"));
        Assert.False(Valid(set, "1e-400"));   // 0 as binary64
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("2.0", true)]
    [InlineData("2e0", true)]
    [InlineData("-0", true)]
    [InlineData("2.5", false)]
    [InlineData("1.0000000000000001", true)]       // integral as binary64
    [InlineData("9007199254740993", true)]         // reads as 2^53: integral; a bound refuses it ([ENC-4])
    [InlineData("\"2\"", false)]
    public void Integer_IsANumberWithNoFractionalPart(string instance, bool valid)
    {
        Assert.Equal(valid, Valid(One("\"type\": \"integer\""), instance));
    }

    [Fact]
    public void TheIntegerBound_RefusesAValueBeyond2Pow53()
    {
        var set = One("\"type\": \"integer\", \"maximum\": 9007199254740991");

        Assert.True(Valid(set, "9007199254740991"));
        Assert.False(Valid(set, "9007199254740993"));
    }

    [Theory]
    [InlineData("[1, 1.0]", false)]
    [InlineData("[{\"a\": 1, \"b\": [2]}, {\"b\": [2.0], \"a\": 1}]", false)]
    [InlineData("[1, \"1\"]", true)]
    [InlineData("[[1, 2], [2, 1]]", true)]
    [InlineData("[null, false, 0, \"\", [], {}]", true)]
    public void UniqueItems_UsesJsonEquality(string instance, bool valid)
    {
        Assert.Equal(valid, Valid(One("\"uniqueItems\": true"), instance));
    }

    [Fact]
    public void ConstAndEnum_UseJsonEquality()
    {
        Assert.True(Valid(One("\"const\": 1"), "1.0"));
        Assert.False(Valid(One("\"const\": 1"), "true"));
        Assert.True(Valid(One("\"enum\": [\"a\", 2, null]"), "null"));
        Assert.False(Valid(One("\"enum\": [\"a\", 2, null]"), "\"A\""));
        Assert.True(Valid(One("\"const\": {\"a\": [1]}"), "{\"a\": [1.0]}"));
    }

    [Fact]
    public void Types_AndTypeLists()
    {
        var set = One("\"type\": [\"string\", \"null\"]");

        Assert.True(Valid(set, "null"));
        Assert.True(Valid(set, "\"x\""));
        Assert.False(Valid(set, "1"));
        Assert.True(Valid(One("\"type\": \"number\""), "2"));
        Assert.False(Valid(One("\"type\": \"object\""), "[]"));
        Assert.False(Valid(One("\"type\": \"boolean\""), "0"));
    }

    [Fact]
    public void ObjectKeywords()
    {
        var set = One("""
            "type": "object", "required": ["a"], "minProperties": 2,
            "properties": {"a": {"type": "integer"}}, "additionalProperties": {"type": "string"}
            """);

        Assert.True(Valid(set, """{"a": 1, "b": "x"}"""));
        Assert.False(Valid(set, """{"a": 1}"""));             // minProperties
        Assert.False(Valid(set, """{"b": "x", "c": "y"}"""));  // required
        Assert.False(Valid(set, """{"a": 1, "b": 2}"""));      // additionalProperties
        Assert.False(Valid(One("\"additionalProperties\": false, \"properties\": {\"a\": true}"), """{"a": 1, "b": 2}"""));
        Assert.True(Valid(One("\"additionalProperties\": false, \"properties\": {\"a\": true}"), """{"a": 1}"""));
    }

    [Fact]
    public void ArrayKeywords()
    {
        var set = One("\"minItems\": 1, \"maxItems\": 2, \"items\": {\"type\": \"string\"}");

        Assert.False(Valid(set, "[]"));
        Assert.True(Valid(set, "[\"a\", \"b\"]"));
        Assert.False(Valid(set, "[\"a\", \"b\", \"c\"]"));
        Assert.False(Valid(set, "[1]"));
    }

    [Fact]
    public void Combinators()
    {
        var oneOf = One("\"oneOf\": [{\"type\": \"integer\"}, {\"minimum\": 2}]");
        Assert.True(Valid(oneOf, "1"));        // only the first
        Assert.True(Valid(oneOf, "2.5"));      // only the second
        Assert.False(Valid(oneOf, "3"));       // both

        var anyOf = One("\"anyOf\": [{\"type\": \"null\"}, {\"type\": \"string\"}]");
        Assert.True(Valid(anyOf, "\"x\""));
        Assert.False(Valid(anyOf, "1"));

        var allOf = One("\"allOf\": [{\"type\": \"string\"}, {\"maxLength\": 1}]");
        Assert.False(Valid(allOf, "\"xy\""));

        var not = One("\"not\": {\"const\": \"latest\"}");
        Assert.False(Valid(not, "\"latest\""));
        Assert.True(Valid(not, "\"v1\""));

        var conditional = One("""
            "if": {"properties": {"status": {"const": "completed"}}, "required": ["status"]},
            "then": {"required": ["endedAt"]}
            """);
        Assert.False(Valid(conditional, """{"status": "completed"}"""));
        Assert.True(Valid(conditional, """{"status": "completed", "endedAt": 1}"""));
        Assert.True(Valid(conditional, """{"status": "running"}"""));
    }

    [Fact]
    public void BooleanSchemas()
    {
        Assert.False(Valid(One("\"properties\": {\"a\": false}"), """{"a": 1}"""));
        Assert.True(Valid(One("\"properties\": {\"a\": false}"), """{"b": 1}"""));
    }

    [Theory]
    [InlineData("2026-10-08T12:00:00Z", true)]
    [InlineData("2026-10-08T12:00:00.123456789Z", true)]
    [InlineData("2024-02-29T00:00:00Z", true)]     // a leap day
    [InlineData("2026-02-29T00:00:00Z", false)]    // not a leap year: never rolled over to March 1
    [InlineData("2026-02-31T00:00:00Z", false)]
    [InlineData("2026-04-31T00:00:00Z", false)]
    [InlineData("1900-02-29T00:00:00Z", false)]
    [InlineData("2000-02-29T00:00:00Z", true)]
    [InlineData("2026-10-08T12:00:00Z\n", false)]
    [InlineData("2026-10-08T12:00:00+00:00", false)]
    [InlineData("2026-10-08T12:00:00.1234567890Z", false)]
    public void ATimestamp_MustMatchItsPattern_AndExist(string time, bool valid)
    {
        Assert.Equal(valid, AefSchemas.Writer.IsValid("common#/$defs/timestamp", JsonValue.Create(time)));
        Assert.Equal(valid, AefSchemas.Reader.IsValid("common#/$defs/timestamp", JsonValue.Create(time)));
    }

    [Fact]
    public void AnUnknownName_IsAnArgumentError()
    {
        Assert.Throws<ArgumentException>(() => AefSchemas.Writer.IsValid("no-such", new JsonObject()));
        Assert.Throws<ArgumentException>(() => AefSchemas.Writer.IsValid("run#/properties/nothing", new JsonObject()));
        Assert.Throws<ArgumentException>(() => AefSchemas.Writer.IsValid("Run", new JsonObject()));
        Assert.False(AefSchemas.Writer.Has("run#nope"));
        Assert.True(AefSchemas.Writer.Has("decision#/$defs/input"));
    }

    [Fact]
    public void Validate_SaysWhereTheFirstFailureIs()
    {
        var why = AefSchemas.Writer.Validate("run", Json("""{"schemaVersion": "1.0", "runId": "bad id"}"""));

        Assert.NotNull(why);
        Assert.Null(AefSchemas.Writer.Validate("common#/$defs/id", JsonValue.Create("r-1")));
        Assert.StartsWith("/ref: type", AefSchemas.Writer.Validate("run#/properties/subject", Json("""{"ref": 1, "kind": "agent"}""")));
    }

    [Fact]
    public void InstancesHeldAsClrValues_AreValidatedLikeParsedOnes()
    {
        var set = One("\"type\": \"object\", \"properties\": {\"n\": {\"type\": \"integer\", \"maximum\": 3}, \"s\": {\"maxLength\": 1}}");

        Assert.True(set.IsValid("t", new JsonObject { ["n"] = 3, ["s"] = "x" }));
        Assert.False(set.IsValid("t", new JsonObject { ["n"] = 4L }));
        Assert.False(set.IsValid("t", new JsonObject { ["n"] = 2.5m }));
        Assert.True(set.IsValid("t", new JsonObject { ["n"] = 2.0f }));
    }
}
