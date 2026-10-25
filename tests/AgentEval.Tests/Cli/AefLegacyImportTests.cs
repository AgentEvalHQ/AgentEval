// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Output;
using AgentEval.Results;
using AgentEval.Results.Integrity;

namespace AgentEval.Tests.Cli;

[Collection("ConsoleTests")]
public sealed class AefLegacyImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aef-legacy-import-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task Import_ExistingStoreRun_ConvertsWithoutChangingOriginal(bool completed, bool seal, bool json)
    {
        var (workspace, source, runId) = await CreateRun(completed);
        var before = Hashes(workspace);
        var output = Path.Combine(_root, "imported");

        var result = await Invoke(["import", "store-v1"], source, output, seal, json);

        Assert.Equal(ExitCodes.Success, result.Exit);
        AssertCustody(result.Output, output, runId, completed, seal, json);
        Assert.Equal(before, Hashes(workspace));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Export_ExistingSpelling_PreservesConversionAndLabelsCustody(bool seal, bool json)
    {
        var (_, source, runId) = await CreateRun(true);
        var output = Path.Combine(_root, "exported");

        var result = await Invoke(["export"], source, output, seal, json);

        Assert.Equal(ExitCodes.Success, result.Exit);
        AssertCustody(result.Output, output, runId, true, seal, json);
    }

    [Fact]
    public async Task Import_WorkspaceWithRunSelection_UsesExistingConverter()
    {
        var (workspace, _, runId) = await CreateRun(true);
        var output = Path.Combine(_root, "selected");
        var result = await Invoke(["import", "store-v1"], workspace, output, true, true, runId);
        AssertCustody(result.Output, output, runId, true, true, true);
    }

    private static void AssertCustody(string text, string output, string runId, bool completed, bool seal, bool json)
    {
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "run.json")))!;
        Assert.Equal(runId, (string?)header["runId"]);
        Assert.Equal(completed ? "completed" : "aborted", (string?)header["status"]);
        Assert.StartsWith("agenteval store v1", (string)header["imported"]!["from"]!, StringComparison.Ordinal);
        Assert.Contains("execution.targetMode", header["imported"]!["asserted"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("mocked", (string?)header["execution"]!["targetMode"]);
        Assert.Equal(seal ? AefOutcome.Intact : AefOutcome.Unsealed, AefRunVerifier.Verify(output).Outcome);
        if (seal)
        {
            var stamp = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "seal.json")))!;
            Assert.Equal("ingest", (string?)stamp["predicate"]!["sealedBy"]);
        }
        else
            Assert.False(File.Exists(Path.Combine(output, "seal.json")));

        var lines = File.ReadAllLines(Path.Combine(output, "results.ndjson"));
        Assert.Equal(2, lines.Length); // observed scenario and assertion survive even an interrupted run
        var notes = json ? string.Join("\n", JsonNode.Parse(text)!["notes"]!.AsArray().Select(x => (string)x!)) : text;
        Assert.Contains("imported: not sealed when it ran", notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("original", notes, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int Exit, string Output)> Invoke(string[] command, string source, string output, bool seal, bool json, string? runId = null)
    {
        var root = new RootCommand();
        root.Add(AefCommand.Create());
        List<string> args = ["aef", .. command, source, output];
        if (!seal) args.Add("--no-seal");
        if (json) args.Add("--json");
        if (runId is not null) args.AddRange(["--run", runId]);
        var parsed = root.Parse(args.ToArray());
        Assert.Empty(parsed.Errors);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await parsed.InvokeAsync();
            Assert.True(exit == ExitCodes.Success, stderr.ToString());
            return (exit, stdout.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private async Task<(string Workspace, string Source, string RunId)> CreateRun(bool completed)
    {
        var workspace = Path.Combine(_root, ".agenteval");
        var store = new FileSystemOutputStore(workspace);
        await store.InitializeSolutionAsync("legacy-import-test");
        var subject = new SubjectIdentity(SubjectKind.Agent, "legacy");
        var manifest = await store.StartRunAsync(subject, new RunContext("Cli.Tests", "tests", "AefLegacyImportTests", null, null, "benchmark"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult("observed", "Observed", "input", "output", true, 1,
            new Dictionary<string, double>(), [AssertionResult.Pass("observed assertion")], TimeSpan.FromMilliseconds(5), 0));
        if (completed)
            await store.CompleteRunAsync(manifest, new RunSummary("1.0", manifest.Run.RunId, "PASS", new RunStats(1, 1, 0, 0), new Dictionary<string, double>()));
        return (workspace, store.ResolveRunDirectory(subject, manifest.Run.RunId), manifest.Run.RunId);
    }

    private static string[] Hashes(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
