// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Output;
using AgentEval.Results.Integrity;
using AgentEval.Tests.Output;
using AgentEval.Tests.TestHelpers;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class NativeWriteDefaultsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Perf_DefaultAndFallback_PreserveNativeAndReportsWithOnlyActualStoreLinks(bool legacy)
    {
        using var temp = TempWorkspace.Create("NativeWrite");
        var workspace = Path.Combine(temp.Path, ".agenteval");
        await new FileSystemOutputStore(workspace).InitializeSolutionAsync("native-write");
        var exit = await NativeVerbIntegrationTests.WithCapture(false,
            () => BenchPerfCommand.RunAsync("cost", "subject", "workload", temp.Path, new OfflineAgent()), legacy);
        Assert.True(exit is 0 or 9 or 10 or 11);
        var native = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(Path.Combine(workspace, "aef"), "run.json", SearchOption.AllDirectories)))!;
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(native).Outcome);
        var manifests = Directory.GetFiles(workspace, "manifest.json", SearchOption.AllDirectories);
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(native, "run.json")))!;
        var link = header["ext"]?["agenteval.store-v1"];
        if (legacy)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Assert.Single(manifests)))!;
            Assert.Equal((string?)manifest["run"]!["runId"], (string?)link?["runId"]);
        }
        else
        {
            Assert.Empty(manifests);
            Assert.Null(link);
            Assert.False(Directory.Exists(Path.Combine(workspace, "subjects")));
            Assert.Single(Directory.GetFiles(Path.Combine(workspace, "reports"), "report.json", SearchOption.AllDirectories));
        }
        Assert.False(AefCliOptions.LegacyOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eval_OutputDirectory_WritesNativeByDefaultAndOldDirectoryOnlyWithFallback(bool legacy)
    {
        using var temp = TempWorkspace.Create("EvalOutput");
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var options = Options(temp.Path, endpoint.Endpoint);
        Assert.Equal(0, await NativeVerbIntegrationTests.WithCapture(false, () => EvalCommand.ExecuteAsync(options, CancellationToken.None), legacy));
        var destination = options.OutputDir!.FullName;
        Assert.Equal(legacy ? 1 : 0, Directory.GetFiles(destination, "results.jsonl", SearchOption.AllDirectories).Length);
        if (!legacy)
        {
            var native = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(destination, "seal.json", SearchOption.AllDirectories)))!;
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(native).Outcome);
        }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("conflict")]
    [InlineData("missing")]
    public async Task Eval_ConflictingOutputControls_RejectsBeforeProviderCall(string mode)
    {
        using var temp = TempWorkspace.Create("EvalInvalidOutput");
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var options = Options(temp.Path, endpoint.Endpoint, mode);
        Assert.Equal(2, await EvalCommand.ExecuteAsync(options, CancellationToken.None));
        Assert.Empty(endpoint.Requests);
    }

    private static EvalOptions Options(string root, string endpoint, string? mode = null)
    {
        var dataset = Path.Combine(root, "dataset.yaml");
        File.WriteAllText(dataset, "- id: question\n  input: hello\n  expected: Hi\n");
        return new() { Dataset = new(dataset), Endpoint = endpoint, Model = "local-model", ApiKey = "local-test",
            Format = "directory", OutputDir = mode == "missing" ? null : new(Path.Combine(root, "output")), Quiet = true,
            NoAef = mode == "disabled", Aef = mode == "conflict" ? new(Path.Combine(root, "other")) : null };
    }

    [Fact]
    public async Task Eval_ExplicitOutputDirectoryCannotBePublished_ReturnsRuntimeFailure()
    {
        using var temp = TempWorkspace.Create("EvalBlockedOutput");
        await using var endpoint = await LocalChatEndpoint.StartAsync();
        var options = Options(temp.Path, endpoint.Endpoint);
        File.WriteAllText(options.OutputDir!.FullName, "occupied");
        Assert.Equal(3, await EvalCommand.ExecuteAsync(options, CancellationToken.None));
        Assert.Equal("occupied", File.ReadAllText(options.OutputDir.FullName));
        Assert.Empty(Directory.GetFiles(temp.Path, "results.jsonl", SearchOption.AllDirectories));
    }

    private sealed class OfflineAgent : IEvaluableAgent
    {
        public string Name => "offline";
        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken); // Bound the real throughput loop, as the existing parity fixture does.
            return new AgentResponse { Text = "observed answer" };
        }
    }
}
