namespace AgentEval.Results.Tests;

/// <summary>The order of §3.9: paths by UTF-8 bytes, line paths of one NDJSON file by line number, then codes.</summary>
public class AefProblemOrderTests
{
    private static string[] Sorted(params string[] paths) => [.. paths.Order(AefProblemOrder.Paths)];

    [Fact]
    public void LinePathsOfOneNdjsonFile_GoByLineNumber()
    {
        Assert.Equal(
            new[] { "results.ndjson", "results.ndjson:2", "results.ndjson:9", "results.ndjson:10", "results.ndjson:100", "run.json" },
            Sorted("results.ndjson:10", "run.json", "results.ndjson:9", "results.ndjson", "results.ndjson:100", "results.ndjson:2"));
        Assert.Equal(new[] { "logs.otlp.jsonl:9", "logs.otlp.jsonl:10" }, Sorted("logs.otlp.jsonl:10", "logs.otlp.jsonl:9"));
    }

    [Fact]
    public void OtherPathsWithAColon_GoByTheirBytes()
    {
        Assert.Equal(new[] { "job", "run:10", "run:9" }, Sorted("run:9", "run:10", "job"));
        Assert.Equal(new[] { "event:10", "event:9" }, Sorted("event:9", "event:10"));
    }

    [Fact]
    public void LinePathsOfTwoFiles_GoByTheirBytes()
    {
        Assert.Equal(new[] { "evidence.ndjson:10", "results.ndjson:9" }, Sorted("results.ndjson:9", "evidence.ndjson:10"));
    }

    [Fact]
    public void Utf8ByteOrder_IsNotUtf16Order()
    {
        // U+FF5E sorts before U+1F600 in UTF-8 (and code point) order, after it in UTF-16 unit order.
        Assert.True(AefProblemOrder.CompareUtf8("\uFF5E", "\U0001F600") < 0);
        Assert.True(string.CompareOrdinal("\uFF5E", "\U0001F600") > 0);
        Assert.True(AefProblemOrder.CompareUtf8("B", "a") < 0);
        Assert.True(AefProblemOrder.CompareUtf8("ab", "abc") < 0);
        Assert.Equal(0, AefProblemOrder.CompareUtf8("x", "x"));
    }

    [Fact]
    public void ProblemsGoByPathThenByCode()
    {
        var sorted = AefProblemOrder.Sort(
        [
            new("results.ndjson:10", "evidence"),
            new("results.ndjson:9", "result-id"),
            new("results.ndjson:10", "annotator"),
            new(".", "limit"),
        ]);

        Assert.Equal(
            new AefProblem[] { new(".", "limit"), new("results.ndjson:9", "result-id"), new("results.ndjson:10", "annotator"), new("results.ndjson:10", "evidence") },
            sorted);
    }
}
