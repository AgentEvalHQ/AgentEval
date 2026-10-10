using AgentEval.Results.Conformance;

namespace AgentEval.Results.Tests.Corpus;

public class DriverTests
{
    [Fact]
    public void AnUnknownOperation_IsAUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Assert.Equal(2, Program.Dispatch(["no-such-operation"], stdout, stderr));
        Assert.Equal("", stdout.ToString());
        Assert.Contains("unknown operation", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCorpusIsFound()
    {
        Assert.True(File.Exists(Path.Combine(AefCorpus.Conformance, "index.json")));
    }
}
