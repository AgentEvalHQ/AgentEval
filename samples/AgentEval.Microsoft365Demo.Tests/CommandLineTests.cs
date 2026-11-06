// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The entry point and the help: <c>Program.Main</c> delegates to the host (the no-config path exits 11 through it), and
/// <c>--help</c> lists every option the README, the Copilot skills and the tester guide name.
/// </summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    public static TheoryData<string> ContractOptions => new()
    {
        "--config", "--agent-id", "--verbosity", "-v", "--i-understand-live-side-effects", "--readiness-only", "--prompt",
        "--dataset", "--no-benchmark", "--no-judge", "--profile", "--out", "--capture-raw", "--zip", "--max-turns",
        "--timeout-seconds",
    };

    [Theory]
    [MemberData(nameof(ContractOptions))]
    public async Task Help_ListsEveryContractOption(string option)
    {
        var run = await _rig.RunAsync(["--help"], addConfig: false, addOut: false);

        run.AssertExit(0);
        Assert.Contains(run.OutLines, l => l.TrimStart().StartsWith(option + " ", StringComparison.Ordinal) || l.TrimStart().StartsWith(option + ",", StringComparison.Ordinal)
            || l.Contains(", " + option + " ", StringComparison.Ordinal) || l.Contains(", " + option + ",", StringComparison.Ordinal));
        _rig.Handler.AssertNoRequests();
        Assert.False(_rig.RunFolder.Exists);
    }

    [Fact]
    public async Task Help_NamesTheRunFolderFilesAndTheJudgeSelector()
    {
        var run = await _rig.RunAsync(["--help"], addConfig: false, addOut: false);

        run.AssertExit(0);

        // Single tokens only: the help wraps its text at a fixed width, so a phrase may be split across lines.
        foreach (var text in new[] { "summary.md", "console.log", "issues.jsonl", "turns.jsonl", "AI_INFERENCE_PROVIDER", "M365_CONFIG_PATH" })
        {
            Assert.Contains(text, run.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProgramMain_MissingConfig_ExitsEleven()
    {
        var missing = Path.Combine(_rig.Root.FullName, "absent.json");

        var exit = await Program.Main(["--config", missing, "--out", _rig.RunFolder.FullName]);

        Assert.Equal(11, exit);
        Assert.False(_rig.RunFolder.Exists);
    }
}
