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
    public void Job_AStartFromWhichTheClockWouldLeaveEnc8sYears_ExitsWith2_AndWritesNothing()
    {
        // The critic's round-8 probe: --at 9999-12-31T23:59:00Z with a suite of 60 s once made the driver throw (exit 134)
        // and leave files. It is an input error, found before anything is written ([CONF-3], spec 09 §9.3).
        var jobs = Path.Combine(AefCorpus.Conformance, "jobs");
        var plan = Temp("""
            {"schemaVersion": "1.0", "planId": "plan-clock", "subject": {"ref": "agent:a/b", "version": "1"},
             "suites": [{"ref": "suite:s/a", "version": "1"}], "limits": {"maxUsd": 5}, "contentCapture": "off",
             "targetMode": "scripted", "isolation": "process", "provider": "local"}
            """);
        var target = Temp("""
            {"suites": [{"ref": "suite:s/a", "version": "1", "content": "a",
              "cases": [{"caseId": "a-1", "state": "passed", "usd": 0, "usdBound": 0, "seconds": 60, "secondsBound": 60}]}],
             "closeSeconds": 1, "priceTable": "p"}
            """);
        var output = Path.Combine(Path.GetTempPath(), $"aef-driver-job-{Guid.NewGuid():N}");
        try
        {
            var (code, stdout, stderr) = Run("job", plan, Path.Combine(jobs, "runner.json"), target, output, "--at", "9999-12-31T23:59:00Z");

            Assert.Equal((2, ""), (code, stdout));
            Assert.Contains("--at", stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));

            (code, stdout, _) = Run("job", plan, Path.Combine(jobs, "runner.json"), target, output, "--at", "9999-12-31T23:58:00Z");
            Assert.Equal((0, "{\"events\":6}\n"), (code, stdout));   // accepted, estimated, the case and its spend, announced, sealed
        }
        finally
        {
            File.Delete(plan);
            File.Delete(target);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public void ResultId_ATrialWrittenAs3Point0_IsTrial3()
    {
        Assert.Equal(Run("result-id", "r", "c", "p", "3").Stdout, Run("result-id", "r", "c", "p", "3.0").Stdout);
        Assert.NotEqual(Run("result-id", "r", "c", "p", "3").Stdout, Run("result-id", "r", "c", "p").Stdout);
    }
}
