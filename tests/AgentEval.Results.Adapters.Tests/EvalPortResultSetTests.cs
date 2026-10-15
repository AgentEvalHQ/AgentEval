using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;

namespace AgentEval.Results.Adapters.Tests;

public class EvalPortResultSetTests
{
    private const string Source = """
        {"version":"1.0.0-rc.5","suite_id":"suite","run_id":"run","started_at":"2026-10-01T00:00:00Z",
         "isolation":"custom","group":{"group_id":"sweep","sequence":9007199254740993},
         "results":[{"test_case_id":"case","attempt":2,"passed":false,
         "grader_results":[{"grader_id":"judge","type":"llm","score":null,"passed":false}]}]}
        """;
    private static JsonObject Document() => JsonNode.Parse(Source)!.AsObject();
    private static EvalPortResultSet Read(JsonObject document) => EvalPortResultSet.Parse(Encoding.UTF8.GetBytes(document.ToJsonString()));

    [Fact]
    public void Parse_ValidContextAndNullScore_PreservesDetachedSource()
    {
        var source = Document(); var parsed = Read(source);
        source["run_id"] = "changed"; var copy = parsed.ToJson(); copy["run_id"] = "also-changed";
        Assert.Equal("run", parsed.RunId); Assert.Equal("suite", parsed.SuiteId);
        Assert.Equal("9007199254740993", parsed.Context.Group!["sequence"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(Document(), parsed.ToJson()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("duplicate-grader")]
    [InlineData("null-pass")]
    [InlineData("unmeasured-pass")]
    [InlineData("score-range")]
    [InlineData("fraction")]
    [InlineData("overflow")]
    [InlineData("major")]
    public void Parse_InvalidResultSet_Refuses(string mode)
    {
        var source = Document(); var result = source["results"]![0]!; var grader = result["grader_results"]![0]!;
        switch (mode)
        {
            case "missing": source.Remove("suite_id"); break;
            case "unknown": result["verdict"] = "unverified"; break;
            case "duplicate": source["results"]!.AsArray().Add(result.DeepClone()); break;
            case "duplicate-grader": result["grader_results"]!.AsArray().Add(grader.DeepClone()); break;
            case "null-pass": grader["passed"] = true; break;
            case "unmeasured-pass": result["passed"] = true; break;
            case "score-range": grader["score"] = 2; break;
            case "fraction": result["attempt"] = JsonNode.Parse("1.00000000000000001"); break;
            case "overflow": result["attempt"] = JsonNode.Parse("9223372036854775808"); break;
            case "major": source["version"] = "2.0.0"; break;
        }
        Assert.Throws<AefInputException>(() => Read(source));
    }

    [Fact]
    public void Parse_ImplicitAttemptAndExplicitOne_RefusesCollision()
    {
        var source = Document(); var result = source["results"]![0]!.AsObject(); result.Remove("attempt");
        var another = result.DeepClone(); another["attempt"] = 1;
        source["results"]!.AsArray().Add(another);
        Assert.Throws<AefInputException>(() => Read(source));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("20e-1")]
    [InlineData("9223372036854775807")]
    public void Parse_ExactIntegralAttempt_AcceptsWithoutRounding(string number)
    {
        var source = Document(); source["results"]![0]!["attempt"] = JsonNode.Parse(number);
        Assert.Equal(number, Read(source).ToJson()["results"]![0]!["attempt"]!.ToJsonString());
    }
}
