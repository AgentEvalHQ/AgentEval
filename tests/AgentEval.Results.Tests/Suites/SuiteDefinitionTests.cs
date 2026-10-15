using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Suites;

namespace AgentEval.Results.Tests.Suites;

public class SuiteDefinitionTests
{
    private static JsonObject Definition() => new()
    {
        ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:quality",
        ["definitionVersion"] = "1", ["frozen"] = true,
        ["inputs"] = new JsonObject { ["rubric"] = SuiteDefinitionManifest.Hash("prompt v1"u8) },
        ["definition"] = new JsonObject { ["threshold"] = 0.8, ["weights"] = new JsonArray(1, 2), ["aggregation"] = "all" },
        ["caseIds"] = new JsonArray("b", "a"),
    };
    private static SuiteDefinitionManifest Read(JsonObject source) => new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes(source.ToJsonString()));

    [Fact]
    public void Read_KeyAndRosterOrderDifferences_ProduceSameDigestAndDetachedSnapshot()
    {
        var source = Definition();
        var first = Read(source);
        // Independently generated with Python's sorted, indented UTF-8 JSON for this numeric-safe vector.
        Assert.Equal("sha256:4fde3e35d7fa14bc40d93b4d48d74c46319d083d0d9f7f5f2c503c86d38038ed", first.Digest);
        var reordered = new JsonObject(source.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
        reordered["caseIds"] = new JsonArray("a", "b");
        var second = Read(reordered);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(new[] { "a", "b" }, first.CaseIds);
        Assert.Equal(first.Digest, first.SuiteContentDigest);
        source["definition"]!["threshold"] = 0.1;
        first.ToJson()["definition"]!["threshold"] = 0.2;
        var bytes = first.ToBytes(); bytes[0] = 0;
        Assert.Equal(first.Digest, Read(first.ToJson()).Digest);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("threshold")]
    [InlineData("weights")]
    [InlineData("aggregation")]
    [InlineData("roster")]
    [InlineData("version")]
    public void Read_DeclaredDefinitionChanges_ChangeIdentity(string change)
    {
        var source = Definition();
        var original = Read(source).Digest;
        switch (change)
        {
            case "prompt": source["inputs"]!["rubric"] = SuiteDefinitionManifest.Hash("prompt v2"u8); break;
            case "threshold": source["definition"]!["threshold"] = 0.9; break;
            case "weights": source["definition"]!["weights"] = new JsonArray(2, 1); break;
            case "aggregation": source["definition"]!["aggregation"] = "any"; break;
            case "roster": source["caseIds"]!.AsArray().Add("c"); break;
            case "version": source["definitionVersion"] = "2"; break;
        }
        Assert.NotEqual(original, Read(source).Digest);
    }

    [Fact]
    public void Read_DynamicRecipe_DoesNotClaimFrozenSuiteContent()
    {
        var source = Definition();
        source["frozen"] = false;
        source["caseIds"] = new JsonArray();
        var definition = Read(source);
        Assert.Null(definition.SuiteContentDigest);
        Assert.StartsWith("sha256:", definition.Digest);
    }

    [Theory]
    [InlineData("duplicate-case")]
    [InlineData("empty-frozen-roster")]
    [InlineData("missing-definition")]
    [InlineData("unknown-field")]
    [InlineData("invalid-input-digest")]
    [InlineData("control-case")]
    public void Read_IncompleteOrAmbiguousDefinition_Refuses(string kind)
    {
        var source = Definition();
        switch (kind)
        {
            case "duplicate-case": source["caseIds"] = new JsonArray("a", "a"); break;
            case "empty-frozen-roster": source["caseIds"] = new JsonArray(); break;
            case "missing-definition": source.Remove("definition"); break;
            case "unknown-field": source["observedScore"] = 0.9; break;
            case "invalid-input-digest": source["inputs"]!["rubric"] = "v1"; break;
            case "control-case": source["caseIds"] = new JsonArray("a\nb"); break;
        }
        Assert.Throws<FormatException>(() => Read(source));
    }

    [Fact]
    public void Hash_InputByteDifferences_AreNotSilentlyNormalized() =>
        Assert.NotEqual(SuiteDefinitionManifest.Hash("a\nb"u8), SuiteDefinitionManifest.Hash("a\r\nb"u8));

    [Fact]
    public void Read_EquivalentNumberSpellings_UseTheSameCanonicalBinary64Identity()
    {
        var source = Definition();
        var text = source.ToJsonString().Replace("0.8", "8e-1").Replace("[1,2]", "[1.0,2e0]");
        Assert.Equal(Read(source).Digest, new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes(text)).Digest);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"}")]
    [InlineData("{\"value\":1e400}")]
    public void Read_InvalidOrAmbiguousJson_Refuses(string source) =>
        Assert.Throws<FormatException>(() => new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes(source)));

    [Fact]
    public void Read_OversizedSource_RefusesBeforeParsing() =>
        Assert.Throws<FormatException>(() => new SuiteDefinitionReader().Read(new byte[SuiteDefinitionReader.MaxBytes + 1]));
}
