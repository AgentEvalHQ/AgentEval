using System.CommandLine;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Results.Adapters.EvalPort;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class EvalPortCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-evalport-cli-" + Guid.NewGuid().ToString("N"));
    public EvalPortCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Input()
    {
        var path = Path.Combine(_root, "input.json");
        File.WriteAllText(path, """
            {"version":"1.0.0-rc.5","run_id":"run-cli","suite_id":"suite-cli","suite_version":"1",
             "provider":{"model":"test-model"},"started_at":"2026-10-01T00:00:00Z","completed_at":"2026-10-01T00:00:01Z",
             "results":[{"test_case_id":"case","passed":true,"actual_output":"captured",
               "grader_results":[{"grader_id":"grade","type":"code","score":1,"passed":true}]}]}
            """);
        return path;
    }

    [Fact]
    public async Task Commands_ImportExport_RoundtripAndReportLosses()
    {
        var input = Input(); var run = Path.Combine(_root, "run"); var output = Path.Combine(_root, "export.json");
        var command = AefCommand.Create(); var previous = Console.Out;
        using var stdout = new StringWriter(); Console.SetOut(stdout);
        try
        {
            var import = command.Parse(["import", "evalport", input, run, "--target-mode", "mocked", "--content-capture", "on", "--json"]);
            Assert.Empty(import.Errors); Assert.Equal(0, await import.InvokeAsync());
            Assert.Equal(run, (string?)JsonNode.Parse(stdout.ToString())!["directory"]);
            stdout.GetStringBuilder().Clear();
            var export = command.Parse(["export-evalport", run, output, "--json"]);
            Assert.Empty(export.Errors); Assert.Equal(0, await export.InvokeAsync());
            Assert.NotEmpty(JsonNode.Parse(stdout.ToString())!["notes"]!.AsArray());
            var result = EvalPortResultSet.Parse(File.ReadAllBytes(output)).ToJson();
            Assert.Equal("captured", (string?)result["results"]![0]!["actual_output"]);
            Assert.Empty(Directory.GetFileSystemEntries(_root, ".ae-evalport-*"));
        }
        finally { Console.SetOut(previous); }
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("subject")]
    public void Import_InvalidInput_PreservesDestinationAndCleansStaging(string scenario)
    {
        var input = Input(); var output = Path.Combine(_root, "run");
        if (scenario == "existing") { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "keep"), "original"); }
        if (scenario == "oversized") File.WriteAllBytes(input, new byte[4 * 1024 * 1024 + 1]);
        if (scenario == "malformed") File.WriteAllText(input, "{}");
        Assert.Equal(2, AefCommand.RunImportEvalPort(input, output, "mocked", scenario == "subject" ? "agent:test" : null,
            null, null, "off", false, null, true, TextWriter.Null, TextWriter.Null));
        Assert.Equal(scenario == "existing", Directory.Exists(output));
        if (scenario == "existing") Assert.Equal("original", File.ReadAllText(Path.Combine(output, "keep")));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".ae-evalport-*"));
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("existing")]
    [InlineData("pin")]
    public void Export_InvalidDestinationOrPin_DoesNotModifyEvidence(string scenario)
    {
        var run = Path.Combine(_root, "run");
        Assert.Equal(0, AefCommand.RunImportEvalPort(Input(), run, "mocked", null, null, null, "off", false, null, true, TextWriter.Null, TextWriter.Null));
        var output = scenario == "inside" ? Path.Combine(run, "export.json") : Path.Combine(_root, "export.json");
        if (scenario == "existing") File.WriteAllText(output, "original");
        Assert.Equal(2, AefCommand.RunExportEvalPort(run, output, scenario == "pin" ? new string('a', 64) : null,
            null, false, null, true, TextWriter.Null, TextWriter.Null));
        Assert.Equal(scenario == "existing", File.Exists(output));
        if (scenario == "existing") Assert.Equal("original", File.ReadAllText(output));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".ae-evalport-*"));
    }

    [Fact]
    public void Commands_Cancelled_DoNotPublish()
    {
        var ct = new CancellationToken(true); var output = Path.Combine(_root, "output");
        Assert.Throws<OperationCanceledException>(() => AefCommand.RunImportEvalPort(Input(), output, "mocked", null, null, null, "off", false, null, true, TextWriter.Null, TextWriter.Null, ct));
        Assert.Throws<OperationCanceledException>(() => AefCommand.RunExportEvalPort(_root, output, null, null, false, null, true, TextWriter.Null, TextWriter.Null, ct));
        Assert.False(Path.Exists(output));
    }
}
