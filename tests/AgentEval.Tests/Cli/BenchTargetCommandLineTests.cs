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

    public static TheoryData<string[], string> RequiredInputs => new()
    {
        { ["bench", "gdpr"], "--subject is required" },
        { ["bench", "eu-ai-act"], "--subject is required" },
        { ["bench", "eu-ai-act", "--subject", "A"], "--input is required" },
        { ["bench", "agentic"], "--subject is required" },
        { ["bench", "owasp"], "--subject is required" },
        { ["bench", "mitre"], "--subject is required" },
        { ["bench", "nist"], "--subject is required" },
        { ["bench", "longmemeval"], "--subject is required" },
        { ["bench", "memory"], "--subject is required" },
        { ["bench", "typedmemeval"], "--vertical is required" },
        { ["bench", "typedmemeval", "--vertical", "episodic"], "--subject is required" },
        { ["bench", "typedmemeval", "--vertical", "episodic", "--subject", "A", "--evidence-detail", "unsupported"], "--evidence-detail must be" },
        { ["bench", "trace-fidelity"], "--agent-trace and --chat-trace are required" },
        { ["bench", "trace-fidelity", "--agent-trace", "agent.json", "--chat-trace", "chat.json"], "--subject is required" },
        { ["bench", "workflow-trace-fidelity"], "--workflow-trace is required" },
        { ["bench", "workflow-trace-fidelity", "--workflow-trace", "workflow.json"], "--subject is required" },
        { ["render"], "--benchmark and --subject are required" },
        { ["compliance", "render"], "--regulation and --subject are required" },
    };

    [Theory]
    [MemberData(nameof(RequiredInputs))]
    public async Task MissingOrInvalidRequiredInput_IsExplicitUsageError_WithoutWorkspaceWrites(string[] args, string diagnostic)
    {
        var before = WorkspaceEntries();

        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync([.. args, "--root", _root]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains(diagnostic, stderr, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }

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

    public static TheoryData<string[]> InvalidTarget => new()
    {
        new[] { "bench", "gdpr", "--subject", "A" },
        new[] { "bench", "eu-ai-act", "--subject", "A", "--input", "q" },
        new[] { "bench", "owasp", "--subject", "A" },
        new[] { "bench", "mitre", "--subject", "A" },
        new[] { "bench", "nist", "--subject", "A" },
        new[] { "bench", "perf", "cost", "--subject", "A" },
        new[] { "bench", "autoaudit" },
    };

    [Theory]
    [MemberData(nameof(InvalidTarget))]
    public async Task UnknownTarget_IsRejectedBeforeRunningOrWritingEvidence(string[] args)
    {
        var before = WorkspaceEntries();
        // autoaudit has no workspace option; it must reject the target before its command runs.
        var rooted = args[1] == "autoaudit" ? args : [.. args, "--root", _root];
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync([.. rooted, "--sut", "unsupported"]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Unknown --sut value: 'unsupported'", stderr, StringComparison.Ordinal);
        Assert.Contains("mock", stderr, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu-ai-act")]
    public async Task MissingResponseFile_IsRejectedBeforeJudgeResolution(string family)
    {
        var before = WorkspaceEntries();
        var missing = Path.Combine(_root, "missing-answer.txt");
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            ["bench", family, "--subject", "A", "--input", "q", "--response-file", missing, "--root", _root]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--response-file not found: " + missing, stderr, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }

    [Theory]
    [InlineData("0", "--limit must be at least 1.")]
    [InlineData("1", "--limit requires --out")]
    public async Task AgenticCalibration_InvalidBoundedRun_RefusesBeforeAnyJudge(string limit, string diagnostic)
    {
        var before = WorkspaceEntries();
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            ["bench", "agentic", "calibrate", "--limit", limit, "--records", Path.Combine(_root, "records.jsonl"), "--root", _root]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains(diagnostic, stderr, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
        Assert.False(File.Exists(Path.Combine(_root, "records.jsonl")));
    }

    [Theory]
    [InlineData("latency")]
    [InlineData("throughput")]
    [InlineData("cost")]
    public async Task PerformancePreset_RequiresSubjectBeforeResolvingTarget(string preset)
    {
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(["bench", "perf", preset]);
        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--subject is required", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BenchmarkList_IsAvailableWithoutTarget_AndListsRegisteredFamilies()
    {
        var (exit, stdout, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(["bench", "--list"]);
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(stderr);
        Assert.Contains("Registered benchmark families:", stdout, StringComparison.Ordinal);
        Assert.Contains("agentic", stdout, StringComparison.Ordinal);
        Assert.Contains("gdpr", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TraceFidelity_MissingTrace_RefusesWithoutWritingEvidence(bool workflow)
    {
        var before = WorkspaceEntries();
        var missing = Path.Combine(_root, "missing-trace.json");
        string[] args = workflow
            ? ["bench", "workflow-trace-fidelity", "--workflow-trace", missing]
            : ["bench", "trace-fidelity", "--agent-trace", missing, "--chat-trace", missing];
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync([.. args, "--subject", "A", "--root", _root]);

        Assert.Equal(1, exit);
        Assert.Contains(workflow ? "Workflow trace not found: " + missing : "Agent-boundary trace not found: " + missing,
            stderr, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }

    [Fact]
    public async Task Render_NoCapturedReport_ExplainsTheMissingEvidence()
    {
        var before = WorkspaceEntries();
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            ["render", "--benchmark", "agentic", "--subject", "A", "--root", _root]);
        Assert.Equal(1, exit);
        Assert.Contains("No captured original agentic report is available", stderr, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }

    [Fact]
    public async Task ComplianceRender_NoCapturedReport_ExplainsTheMissingEvidence()
    {
        var before = WorkspaceEntries();
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            ["compliance", "render", "--regulation", "gdpr", "--subject", "A", "--root", _root]);
        Assert.Equal(1, exit);
        Assert.Contains("No captured rich compliance report is available", stderr, StringComparison.Ordinal);
        Assert.Equal(before, WorkspaceEntries());
    }

    [Theory]
    [MemberData(nameof(NoTarget))]
    public async Task NoTarget_IsAUsageError_ThatNamesTheMock(string[] args)
    {
        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync([.. args, "--root", _root]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("needs a real target", stderr, StringComparison.Ordinal);
        Assert.Contains("--sut mock", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--reference-file")]
    [InlineData("--context-file")]
    public async Task BenchAgentic_AMissingReferenceOrContextFile_IsAUsageError_BeforeAnyJudge(string option)
    {
        var missing = Path.Combine(_root, "does-not-exist.txt");

        var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
            ["bench", "agentic", "--subject", "A", "--input", "q", "--response", "r", option, missing, "--root", _root]);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains($"{option} not found", stderr, StringComparison.Ordinal);
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

    [Fact]
    public async Task RedTeamPackList_NeedsNoTarget()
    {
        // `--pack list` evaluates nothing; the no-target guard must not stand in front of it.
        var (exit, stdout, _) = await CliParseErrorExitCodeTests.RunCliAsync(["redteam", "--pack", "list"]);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Available benchmark packs", stdout, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> MockWithARealTarget => new()
    {
        new[] { "bench", "owasp", "--subject", "A", "--sut", "mock", "--azure-from-env" },
        new[] { "bench", "mitre", "--subject", "A", "--sut", "mock", "--endpoint", "http://127.0.0.1:9/v1", "--model", "m" },
        new[] { "bench", "nist", "--subject", "A", "--sut", "mock", "--from-env" },
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

    public static TheoryData<string[]> FromEnvCommands => new()
    {
        new[] { "bench", "owasp" },
        new[] { "bench", "mitre" },
        new[] { "bench", "nist" },
        new[] { "bench", "perf", "latency" },
        new[] { "bench", "gdpr" },
        new[] { "bench", "eu-ai-act", "--input", "Q" },
    };

    [Theory]
    [MemberData(nameof(FromEnvCommands))]
    public async Task FromEnv_AndItsOldName_AzureFromEnv_AreTheSameOption(string[] command)
    {
        // --azure-from-env was renamed --from-env (it never meant Azure only); the old name must keep working. Both
        // reach the command as a real target, which the mock then refuses to be combined with.
        foreach (var flag in new[] { "--from-env", "--azure-from-env" })
        {
            var (exit, _, stderr) = await CliParseErrorExitCodeTests.RunCliAsync(
                [.. command, "--subject", "A", "--sut", "mock", flag, "--root", _root]);

            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Contains("cannot be combined with a real target", stderr, StringComparison.Ordinal);
        }
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
