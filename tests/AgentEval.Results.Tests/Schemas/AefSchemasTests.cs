using System.Text.Json.Nodes;
using AgentEval.Results.Schemas;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Schemas;

/// <summary>The embedded writer and reader sets: the same bytes as contracts/aef/1/schemas, and both compile.</summary>
public class AefSchemasTests
{
    [Theory]
    [InlineData("writer")]
    [InlineData("reader")]
    public void TheEmbeddedSchemas_AreTheBytesOfTheSchemaFiles(string side)
    {
        var dir = Path.Combine(AefCorpus.Aef, "schemas", side);
        var files = Directory.GetFiles(dir, "*.schema.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var embedded = AefSchemas.EmbeddedFiles(side);

        Assert.Equal(16, files.Count);
        Assert.Equal(files, embedded.Select(e => e.Key).Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in embedded)
        {
            Assert.True(File.ReadAllBytes(Path.Combine(dir, name)).AsSpan().SequenceEqual(bytes), $"{side}/{name} differs from the file");
        }
    }

    [Fact]
    public void BothSets_Compile_WithTheSameNames()
    {
        Assert.Equal(16, AefSchemas.Writer.Names.Count);
        Assert.Equal(AefSchemas.Writer.Names, AefSchemas.Reader.Names);
        Assert.Contains("run", AefSchemas.Names);
        Assert.True(AefSchemas.Writer.Has(AefSchemaValidator.TimestampDefinition.Replace(".schema.json", "", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("1.0", true, true)]
    [InlineData("1.7", false, true)]     // a later minor: the reader accepts it ([VER-3])
    [InlineData("2.0", false, false)]    // another major: refused ([VER-4])
    [InlineData("1.0\n", false, false)]
    public void SchemaVersion_WriterAndReader(string version, bool writer, bool reader)
    {
        var node = JsonValue.Create(version);

        Assert.Equal(writer, AefSchemas.Writer.IsValid("common#/$defs/schemaVersion", node));
        Assert.Equal(reader, AefSchemas.Reader.IsValid("common#/$defs/schemaVersion", node));
    }

    [Fact]
    public void TheTrustPolicy_IsClosedForTheReaderToo()
    {
        // [SIG-4], [VER-9] (round 4): its reader schema is its writer schema; a member this version does not know refuses it.
        var unknown = JsonNode.Parse("""{"keys": [], "note": 1}""");
        Assert.False(AefSchemas.Writer.IsValid("trust-policy", unknown));
        Assert.False(AefSchemas.Reader.IsValid("trust-policy", unknown));
        Assert.True(AefSchemas.Reader.IsValid("trust-policy", JsonNode.Parse("""{"keys": []}""")));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z", true)]
    [InlineData("9999-12-31T23:59:59.999999999Z", true)]
    [InlineData("0000-01-01T00:00:00Z", false)]   // RFC 3339 allows it; AEF does not ([ENC-8], round 4)
    [InlineData("0000-12-31T23:59:59Z", false)]
    public void ATime_IsInTheYears0001To9999(string time, bool valid)
    {
        Assert.Equal(valid, AefSchemas.Writer.IsValid("common#/$defs/timestamp", JsonValue.Create(time)));
        Assert.Equal(valid, AefSchemas.Reader.IsValid("common#/$defs/timestamp", JsonValue.Create(time)));
        Assert.Equal(valid, Record.Exception(() => AefTime.Parse(time)) is null);
    }

    [Fact]
    public void TheClosedEnums_StayClosedForTheReader()
    {
        Assert.False(AefSchemas.Reader.IsValid("common#/$defs/state", JsonValue.Create("deferred")));   // [VER-9]
        Assert.True(AefSchemas.Reader.IsValid("common#/$defs/severity", JsonValue.Create("catastrophic")));
        Assert.False(AefSchemas.Writer.IsValid("common#/$defs/severity", JsonValue.Create("catastrophic")));
    }
}
