using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Interop;

namespace AgentEval.Results.Tests.Interop;

public class EvalPortRunContextTests
{
    [Fact]
    public void Roundtrip_OpenVocabularyAndLargeSequence_PreservesMeaningAndCopies()
    {
        var source = """{"isolation":"custom-session","group":{"group_id":"g","parent_group_id":"parent","role":"custom-role","label":"","sequence":9007199254740993}}""";
        var context = EvalPortRunContext.FromResultSet(Encoding.UTF8.GetBytes(source));
        var restored = EvalPortRunContext.Parse(context.ToBytes());
        Assert.Equal("custom-session", restored.Isolation);
        var group = restored.Group!; group["group_id"] = "changed";
        var target = new JsonObject { ["run_id"] = "run" };
        var exported = restored.ApplyTo(target);
        Assert.Equal("g", (string?)exported["group"]!["group_id"]);
        Assert.Equal("9007199254740993", exported["group"]!["sequence"]!.ToJsonString());
        Assert.False(target.ContainsKey("group"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source), restored.ApplyTo(new())));
    }

    [Theory]
    [InlineData("{\"isolation\":null}")]
    [InlineData("{\"group\":null}")]
    [InlineData("{\"group\":{}}")]
    [InlineData("{\"group\":{\"group_id\":\"\"}}")]
    [InlineData("{\"group\":{\"group_id\":\"g\",\"parent_group_id\":\"g\"}}")]
    [InlineData("{\"group\":{\"group_id\":\"g\",\"sequence\":-1}}")]
    [InlineData("{\"group\":{\"group_id\":\"g\",\"sequence\":1.00000000000000001}}")]
    [InlineData("{\"group\":{\"group_id\":\"g\",\"sequence\":1e-400}}")]
    [InlineData("{\"group\":{\"group_id\":\"g\",\"extra\":true}}")]
    public void Read_InvalidDeclarations_Refuses(string json) =>
        Assert.Throws<AefInputException>(() => EvalPortRunContext.FromResultSet(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("0")]
    [InlineData("2.0")]
    [InlineData("20e-1")]
    [InlineData("1e30")]
    public void Read_IntegralSequence_PreservesExactNumericToken(string number)
    {
        var context = EvalPortRunContext.FromResultSet(Encoding.UTF8.GetBytes("{\"group\":{\"group_id\":\"g\",\"sequence\":" + number + "}}"));
        Assert.Equal(number, context.Group!["sequence"]!.ToJsonString());
    }

    [Fact]
    public void Apply_AbsentOrConflictingContext_DoesNotInventOrOverwrite()
    {
        var absent = EvalPortRunContext.FromResultSet("{}"u8);
        Assert.Empty(absent.ApplyTo(new()));
        var existing = new JsonObject { ["isolation"] = "fresh" };
        Assert.Throws<AefInputException>(() => absent.ApplyTo(existing));
        Assert.Equal("fresh", (string?)existing["isolation"]);
        var empty = EvalPortRunContext.FromResultSet("{\"isolation\":\"\"}"u8);
        Assert.Equal("", empty.Isolation);
    }
}
