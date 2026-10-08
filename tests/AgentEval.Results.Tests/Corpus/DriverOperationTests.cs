using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Conformance;

namespace AgentEval.Results.Tests.Corpus;

/// <summary>The command-line contract of the document, decision and protocol operations: outputs and input errors.</summary>
public class DriverOperationTests
{
    private static (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Dispatch(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Temp(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), $"aef-driver-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(content));
        return file;
    }

    [Theory]
    [InlineData("document", "run")]
    [InlineData("document", "nope", "x.json")]
    [InlineData("decide")]
    [InlineData("match", "a.json")]
    [InlineData("stream", "a.ndjson")]
    [InlineData("paths")]
    [InlineData("result-id", "r", "c")]
    [InlineData("result-id", "r", "c", "p", "1.5")]
    [InlineData("result-id", "r", "c", "p", "-1")]
    [InlineData("result-id", "r", "c", "p", "x")]
    [InlineData("result-id", "r", "c\u001F", "p")]
    [InlineData("decide", "/no/such/file.json")]
    public void AUsageOrInputError_ExitsWith2_AndWritesNothingToStdout(params string[] args)
    {
        var (code, stdout, stderr) = Run(args);

        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        Assert.NotEqual("", stderr);
    }

    [Fact]
    public void Document_BytesThatAreNotIJson_AreInvalidOnBothSides()
    {
        var file = Temp("""{"schemaVersion": "1.0", "schemaVersion": "1.0"}""");
        try
        {
            var (code, stdout, _) = Run("document", "run", file);

            Assert.Equal(0, code);
            Assert.Equal("{\"writer\":\"invalid\",\"reader\":\"invalid\",\"reads\":{}}\n", stdout);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Decide_RefusesWhatItCannotDecide_WithExitCode0()
    {
        var file = Temp("""{"subjectVersion": "v1", "evaluatedAt": "2026-10-08T00:00:00Z", "lanes": []}""");
        try
        {
            var (code, stdout, _) = Run("decide", file);

            Assert.Equal(0, code);
            Assert.NotNull(JsonNode.Parse(stdout)!["error"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Paths_TheCorpusForm_GivesProblemsPerItem()
    {
        var file = Temp("""[{"name": "a", "paths": ["ok", ".bad"]}, {"name": "b", "paths": []}]""");
        try
        {
            var (code, stdout, _) = Run("paths", file);

            Assert.Equal(0, code);
            Assert.Equal("[{\"name\":\"a\",\"problems\":[[\".bad\",\"path\"]]},{\"name\":\"b\",\"problems\":[]}]\n", stdout);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ResultId_ATrialWrittenAs3Point0_IsTrial3()
    {
        Assert.Equal(Run("result-id", "r", "c", "p", "3").Stdout, Run("result-id", "r", "c", "p", "3.0").Stdout);
        Assert.NotEqual(Run("result-id", "r", "c", "p", "3").Stdout, Run("result-id", "r", "c", "p").Stdout);
    }
}
