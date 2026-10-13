using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class SuiteCompatibilityCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-compare-suites-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Write(string mode)
    {
        var frozen = mode != "recipe";
        var definition = new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes($$"""
            {"schemaVersion":"1.0","kind":"probe","suiteRef":"redteam:test","definitionVersion":"1",
             "frozen":{{frozen.ToString().ToLowerInvariant()}},"inputs":{},
             "definition":{"threshold":{{(mode == "changed" ? 90 : 80)}}},"caseIds":["a"]}
            """));
        return AefProbeRunWriter.Write(_root, [new() { CaseId = "a", State = AefState.Passed }],
            new AefProbeRunOptions
            {
                SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, SuiteRef = "redteam:test",
                Definition = mode == "legacy" ? null : definition, Seal = mode != "unsealed",
                Stimulus = frozen ? AefStimulus.Suite : AefStimulus.Generated,
            }, Start, Start.AddSeconds(1)).Directory;
    }

    [Theory]
    [InlineData("same", 0, "same-definition")]
    [InlineData("changed", 13, "definition-mismatch")]
    [InlineData("recipe", 13, "generated-content")]
    [InlineData("legacy", 13, "definition-required")]
    public void RunCompareSuites_NativeEvidence_ReportsCompatibilityAndExactInputs(string mode, int expected, string reason)
    {
        var baseline = Write("same"); var candidate = Write(mode);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var code = AefCommand.RunCompareSuites(baseline, candidate, null, null, true, stdout, stderr);
        Assert.Equal(expected, code);
        Assert.Empty(stderr.ToString());
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(reason, (string?)report["reason"]);
        Assert.Equal(SuiteCompatibility.Policy, (string?)report["policy"]);
        Assert.Equal(64, ((string)report["baseline"]!["runHash"]!).Length);
        Assert.Equal(64, ((string)report["candidate"]!["runHash"]!).Length);
    }

    [Theory]
    [InlineData("unsealed")]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("pin")]
    [InlineData("oversized")]
    public void RunCompareSuites_InvalidEvidence_UsageErrorWithoutJsonReport(string mode)
    {
        var baseline = Write("same"); var candidate = Write(mode);
        if (mode == "tampered") File.AppendAllText(Path.Combine(candidate, "run.json"), " ");
        if (mode == "missing") candidate = Path.Combine(_root, "absent");
        if (mode == "oversized") File.WriteAllBytes(Path.Combine(candidate, "ext", SuiteDefinitionManifest.ExtensionFile), new byte[SuiteDefinitionReader.MaxBytes + 1]);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var code = AefCommand.RunCompareSuites(baseline, candidate, null, mode == "pin" ? new string('0', 64) : null, true, stdout, stderr);
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Empty(stdout.ToString());
        Assert.NotEmpty(stderr.ToString());
    }

    [Fact]
    public async Task Command_ParsedArgumentsAndPins_ReachStrictComparison()
    {
        var directory = Write("same");
        var hash = new SuiteIdentityReader().Read(directory).RunHash;
        var original = Console.Out;
        using var stdout = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            var parsed = AefCommand.Create().Parse(["compare-suites", directory, directory, "--baseline-hash", hash, "--candidate-hash", hash, "--json"]);
            Assert.Empty(parsed.Errors);
            Assert.Equal(0, await parsed.InvokeAsync());
        }
        finally { Console.SetOut(original); }
        Assert.True((bool)JsonNode.Parse(stdout.ToString())!["compatible"]!);
    }
}
