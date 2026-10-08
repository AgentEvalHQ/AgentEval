// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.RedTeamTargets;
using AgentEval.RedTeam.Gatekeeper.MemorySecurity;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>agenteval redteam --attacks memory-poisoning</c>: the memory-security corpus, which had no CLI path, through the
/// real redteam command. Free: <c>--scripted</c>, or the endpoint path with the test seam standing in for the model.
/// </summary>
[Collection("ConsoleTests")]
public sealed class MemoryPoisoningRedTeamDriverTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agenteval-memory-poisoning-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private static Dictionary<string, IRedTeamTargetOptions?> Scripted() =>
        new(StringComparer.OrdinalIgnoreCase) { ["gatekeeper-demo"] = new GatekeeperDemoTargetOptions(Scripted: true) };

    private FileInfo Out(string extension) => new(Path.Combine(_dir.FullName, $"report-{Guid.NewGuid():N}.{extension}"));

    [Fact]
    public async Task Scripted_RunsEveryCase_AndExitsOneWhenASecurityCheckFails()
    {
        var output = Out("json");
        var opts = new RedTeamOptions
        {
            Attacks = "memory-poisoning", Intensity = "moderate", Format = "json", Output = output, Quiet = true, TargetOptions = Scripted(),
        };

        var exit = await RedTeamCommand.ExecuteAsync(opts, default);

        // The worst-case model persists low-trust poison the default policy admits: containment fails.
        Assert.Equal(ExitCodes.TestFailure, exit);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output.FullName));
        var root = doc.RootElement;
        Assert.Equal("SCRIPTED", root.GetProperty("mode").GetString());
        Assert.Equal(16, root.GetProperty("cases").GetArrayLength());
        Assert.Equal(5, root.GetProperty("checks").GetArrayLength());
        Assert.Equal("fail", root.GetProperty("verdict").GetString());
        Assert.Equal(64, root.GetProperty("scriptsFingerprint").GetString()!.Length);

        // Every case says SCRIPTED, and an outcome not measured is an explicit null, not a missing key.
        var scope = root.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("scenarioId").GetString() == "MS-SCOPE-001");
        Assert.All(root.GetProperty("cases").EnumerateArray(), c => Assert.Equal("SCRIPTED", c.GetProperty("mode").GetString()));
        Assert.Equal("harness", scope.GetProperty("plantedBy").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, scope.GetProperty("observation").GetProperty("poisonPersisted").ValueKind);
    }

    [Fact]
    public async Task EndpointPath_UsesTheNamedModel_AndSaysLive()
    {
        var output = Out("md");
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:1/v1", Model = "fake-model", Attacks = "MemoryPoisoning",
            Intensity = "moderate", Format = "markdown", Output = output, Quiet = true,
        };

        await RedTeamCommand.ExecuteAsync(opts, default, memoryModelOverride: MemoryPoisoningHarness.CreateScriptedModel());

        var report = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("# Memory poisoning (LIVE)", report, StringComparison.Ordinal);
        Assert.Contains("- Model: fake-model", report, StringComparison.Ordinal);
        Assert.Contains("| LIVE | MS-SCOPE-001 | 1 | harness |", report, StringComparison.Ordinal);
        Assert.DoesNotContain("SCRIPTED", report, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> UsageErrors() => new()
    {
        { "two attacks", "runs on its own" },
        { "--sut", "does not take --sut" },
        { "--transform", "does not take --transform" },
        { "sarif", "--format 'sarif'" },
        { "regression", "--fail-on 'regression'" },
        { "trials", "--memory-trials" },
        { "no model", "needs a model" },
        { "--system-prompt", "does not take --system-prompt" },
        { "--intensity", "does not take --intensity" },
        { "--benign-controls", "does not take --benign-controls" },
        { "--pack list", "does not take --pack" },
        { "timeout", "--timeout-per-probe must be" },
    };

    private static RedTeamOptions Options(string change)
    {
        var o = new RedTeamOptions
        {
            Endpoint = "http://localhost:1/v1", Model = "fake-model", Attacks = "memory-poisoning", Intensity = "moderate", Format = "json",
        };
        return change switch
        {
            "two attacks" => o.With(attacks: "memory-poisoning,PromptInjection"),
            "--sut" => o.With(sut: "gatekeeper-demo"),
            "--transform" => o.With(transform: "base64"),
            "sarif" => o.With(format: "sarif"),
            "regression" => o.With(failOn: "regression"),
            "trials" => o.With(trials: 0),
            "no model" => o.With(endpoint: null),
            "--system-prompt" => o.With(systemPrompt: "You are helpful."),
            "--intensity" => o.With(intensity: "quick"),
            "--benign-controls" => o.With(benignControls: true),
            "--pack list" => o.With(pack: "list"),
            "timeout" => o.With(timeout: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task AnOptionItDoesNotTake_IsAUsageError_BeforeAnyModelCall(string change, string message)
    {
        var model = new CountingClient();
        var opts = Options(change);
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await RedTeamCommand.ExecuteAsync(opts, default, memoryModelOverride: model);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains(message, stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task MemoryTrials_OnAnotherAttack_IsAUsageError()
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = await RedTeamCommand.ExecuteAsync(
                new RedTeamOptions { Endpoint = "http://localhost:1/v1", Model = "m", Attacks = "PromptInjection", Intensity = "moderate", Format = "json", MemoryTrials = 3 },
                default);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--memory-trials applies to --attacks memory-poisoning only", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatNeverCallsATool_ReportsItsCasesNotMeasured_AndStillFailsOnTheHarnessCases()
    {
        var output = Out("json");
        var opts = new RedTeamOptions
        {
            Endpoint = "http://localhost:1/v1", Model = "fake-model", Attacks = "memory-poisoning",
            Intensity = "moderate", Format = "json", Output = output, Quiet = true,
        };

        var exit = await RedTeamCommand.ExecuteAsync(opts, default, memoryModelOverride: new CountingClient());

        // Its silence is not containment: the cases it decided are not measured. The cases the harness planted fail with
        // the default options whatever the model does (a tampered record is recalled), and a failure is reported as one.
        Assert.Equal(ExitCodes.TestFailure, exit);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output.FullName));
        var direct = doc.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("scenarioId").GetString() == "MS-DIRECT-001");
        Assert.Equal(JsonValueKind.Null, direct.GetProperty("observation").GetProperty("poisonPersisted").ValueKind);
        Assert.Contains(direct.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith("not measured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommandLine_ScriptedNeedsNoEndpoint()
    {
        var output = Out("json");

        var exit = await RedTeamCommand.Create()
            .Parse(["--attacks", "memory-poisoning", "--scripted", "--format", "json", "--quiet", "-o", output.FullName])
            .InvokeAsync();

        Assert.Equal(ExitCodes.TestFailure, exit);
        Assert.True(output.Exists);
    }

    private sealed class CountingClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

internal static class RedTeamOptionsTestExtensions
{
    /// <summary>A copy with the named fields changed (RedTeamOptions has required members, so no `with`).</summary>
    public static RedTeamOptions With(
        this RedTeamOptions o, string? attacks = "", string? sut = "", string? transform = "", string? format = null,
        string? failOn = null, int? trials = null, string? endpoint = "", string? systemPrompt = null, string? intensity = null,
        bool benignControls = false, string? pack = null, double? timeout = null) => new()
    {
        Endpoint = endpoint == "" ? o.Endpoint : endpoint,
        Model = o.Model,
        Attacks = attacks == "" ? o.Attacks : attacks,
        Sut = sut == "" ? o.Sut : sut,
        Transform = transform == "" ? o.Transform : transform,
        Intensity = intensity ?? o.Intensity,
        Format = format ?? o.Format,
        FailOn = failOn ?? o.FailOn,
        MemoryTrials = trials ?? o.MemoryTrials,
        SystemPrompt = systemPrompt ?? o.SystemPrompt,
        BenignControls = benignControls || o.BenignControls,
        Pack = pack ?? o.Pack,
        TimeoutPerProbeSeconds = timeout ?? o.TimeoutPerProbeSeconds,
    };
}
