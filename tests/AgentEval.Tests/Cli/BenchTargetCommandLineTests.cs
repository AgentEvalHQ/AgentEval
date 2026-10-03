// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// The command line of every <c>bench</c> family that grades an agent: no target is a usage error, <c>--sut mock</c>
/// reaches the command as the mock, and the mock cannot be combined with a real target. Through 0.42 these commands
/// quietly measured a built-in stand-in when no target was named and stored the result as a measurement.
/// </summary>
/// <remarks>
/// These drive the real entry point, so they cover the argument wiring in <c>Program.cs</c> that the commands' own
/// tests cannot see. No case reaches a model: every refusal comes before the judge, and a mock run never reads the
/// provider environment.
/// </remarks>
[Collection("ConsoleTests")]
public sealed class BenchTargetCommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-target-cli-" + Guid.NewGuid().ToString("N"));

    public BenchTargetCommandLineTests()
    {
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "solution.json"), JsonSerializer.Serialize(
            new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "TargetCliTest" },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string[] WorkspaceEntries() =>
        Directory.GetFileSystemEntries(Path.Combine(_root, ".agenteval"), "*", SearchOption.AllDirectories)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToArray();

    public static TheoryData<string[]> NoTarget => new()
    {
        new[] { "bench", "owasp", "--subject", "A" },
        new[] { "bench", "mitre", "--subject", "A" },
        new[] { "bench", "nist", "--subject", "A" },
        new[] { "bench", "perf", "latency", "--subject", "A" },
        new[] { "bench", "gdpr", "--subject", "A" },
        new[] { "bench", "eu-ai-act", "--subject", "A", "--input", "Are you a person?" },
        new[] { "bench", "agentic", "--subject", "A" },
    };

    [Theory]
    [MemberData(nameof(NoTarget))]
    public async Task NoTarget_IsAUsageError_ThatNamesTheMock(string[] args)
    {
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync([.. args, "--root", _root]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("needs a real target", stderr, StringComparison.Ordinal);
        Assert.Contains("--sut mock", stderr, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> NoTargetOutsideBench => new()
    {
        new[] { "redteam" },
        new[] { "redteam", "--attacks", "PromptInjection" },
        new[] { "eval", "--dataset", "cases.jsonl" },
    };

    [Theory]
    [MemberData(nameof(NoTargetOutsideBench))]
    public async Task NoTarget_OutsideBench_IsAUsageError(string[] args)
    {
        // redteam and eval refused too, but as a runtime error (exit 3). The message is asserted so a parse error,
        // which also exits 2, cannot pass for the guard.
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(args);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Specify --endpoint <url> or --azure, or --sut <target>.", stderr, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> MockWithARealTarget => new()
    {
        new[] { "bench", "owasp", "--subject", "A", "--sut", "mock", "--azure-from-env" },
        new[] { "bench", "mitre", "--subject", "A", "--sut", "mock", "--endpoint", "http://127.0.0.1:9/v1", "--model", "m" },
        new[] { "bench", "nist", "--subject", "A", "--sut", "mock", "--azure-from-env" },
        new[] { "bench", "perf", "latency", "--subject", "A", "--sut", "mock", "--endpoint", "http://127.0.0.1:9/v1", "--model", "m" },
        new[] { "bench", "gdpr", "--subject", "A", "--sut", "mock", "--response", "We keep your email." },
        new[] { "bench", "eu-ai-act", "--subject", "A", "--input", "Q", "--sut", "mock", "--azure-from-env" },
        new[] { "bench", "agentic", "--subject", "A", "--sut", "mock", "--response", "Done." },
    };

    [Theory]
    [MemberData(nameof(MockWithARealTarget))]
    public async Task MockTogetherWithARealTarget_IsAUsageError(string[] args)
    {
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync([.. args, "--root", _root]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("cannot be combined with a real target", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agentic_TakesOnlyTheMockAsASut()
    {
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            "bench", "agentic", "--subject", "A", "--sut", "copilot-studio", "--root", _root);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Valid here: mock", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownSut_ListsTheMockAmongTheValidValues()
    {
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            "bench", "owasp", "--subject", "A", "--sut", "no-such-target", "--root", _root);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Unknown --sut value", stderr, StringComparison.Ordinal);
        Assert.Contains("mock", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gdpr_AnAnswerWithoutItsQuestion_IsAUsageError()
    {
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            "bench", "gdpr", "--subject", "A", "--response", "We keep your email.", "--root", _root);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--input is required", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Perf_SutMock_RunsTheStandIn_LabelledAndNotStored()
    {
        var before = WorkspaceEntries();

        var (exit, stdout, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            "bench", "perf", "latency", "--subject", "A", "--sut", "mock", "--root", _root);

        Assert.Equal(ExitCodes.GateIndeterminate, exit);
        Assert.Contains("MOCK RUN", stderr, StringComparison.Ordinal);
        Assert.Contains("MOCK RESULT", stdout, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }

    [Fact]
    public async Task Owasp_SutMock_RunsTheStandIn_LabelledAndNotStored()
    {
        var before = WorkspaceEntries();

        var (exit, stdout, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            "bench", "owasp", "--preset", "smoke", "--subject", "A", "--sut", "mock", "--root", _root);

        Assert.Equal(ExitCodes.GateIndeterminate, exit);
        Assert.Contains("MOCK RUN", stderr, StringComparison.Ordinal);
        Assert.Contains("MOCK RESULT", stdout, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }
}
