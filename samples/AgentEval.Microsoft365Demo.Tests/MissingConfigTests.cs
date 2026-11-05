// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// Without a config file the demo says exactly what is missing and how to create it, contacts nothing (no token, no
/// request, no run folder) and exits 11 — nothing failed, nothing was measured. It never falls back to a mock target.
/// </summary>
public sealed class MissingConfigTests : IDisposable
{
    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task RunAsync_ConfigFileMissing_ExitsElevenWithWhatIsMissingAndHowToCreateIt()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        var missing = Path.Combine(_rig.Root.FullName, "nope", "workiq.json");

        var run = await _rig.RunAsync("--config", missing, DemoRig.Consent);

        run.AssertExit(11);
        Assert.Contains("no config file at " + missing, run.Out, StringComparison.Ordinal);
        Assert.Contains("(--config)", run.Out, StringComparison.Ordinal);
        Assert.Contains("tenantId", run.Out, StringComparison.Ordinal);
        Assert.Contains("appClientId", run.Out, StringComparison.Ordinal);
        Assert.Contains("WorkIQAgent.Ask", run.Out, StringComparison.Ordinal);
        Assert.Contains("samples/datasets/m365-config.template.json", run.Out, StringComparison.Ordinal);
        Assert.Contains("--readiness-only", run.Out, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent to any tenant", run.Out, StringComparison.Ordinal);

        // Nothing was contacted and nothing was created.
        _rig.Handler.AssertNoRequests();
        Assert.Equal(0, _rig.Tokens.Calls);
        Assert.False(run.Folder.Exists, "A run without a config must not create a run folder.");
        Assert.False(_rig.RunsRoot.Exists);
    }

    [Fact]
    public async Task RunAsync_ConfigPathVariableNamesAMissingFile_NamesTheVariableAsTheSource()
    {
        var missing = Path.Combine(_rig.Root.FullName, "from-variable.json");
        _rig.Environment["M365_CONFIG_PATH"] = missing;

        var run = await _rig.RunAsync(["--readiness-only"], addConfig: false, addOut: true);

        run.AssertExit(11);
        Assert.Contains("no config file at " + missing, run.Out, StringComparison.Ordinal);
        Assert.Contains("(M365_CONFIG_PATH)", run.Out, StringComparison.Ordinal);
        _rig.Handler.AssertNoRequests();
        Assert.Equal(0, _rig.Tokens.Calls);
        Assert.False(run.Folder.Exists);
    }

    [Theory]
    [InlineData("--verbosity", "loud")]
    [InlineData("--profile", "full")]
    [InlineData("--max-turns", "0")]
    [InlineData("--timeout-seconds", "0")]
    public async Task RunAsync_UsageMistake_ExitsTwoBeforeAnythingIsCreatedOrSent(string option, string value)
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();

        var run = await _rig.RunAsync(option, value, DemoRig.Consent);

        run.AssertExit(2);
        Assert.Contains("--help", run.Err, StringComparison.Ordinal);
        _rig.Handler.AssertNoRequests();
        Assert.Equal(0, _rig.Tokens.Calls);
        Assert.False(run.Folder.Exists);
    }

    [Fact]
    public async Task RunAsync_OutFolderNotEmpty_ExitsTwoAndLeavesItUntouched()
    {
        _rig.WithHealthyEndpoint();
        _rig.RunFolder.Create();
        var other = Path.Combine(_rig.RunFolder.FullName, "another-run.txt");
        File.WriteAllText(other, "not this run's");

        var run = await _rig.RunAsync("--readiness-only");

        run.AssertExit(2);
        Assert.Contains("not empty", run.Err, StringComparison.Ordinal);
        Assert.Equal(new[] { "another-run.txt" }, _rig.RunFolder.EnumerateFileSystemInfos().Select(f => f.Name).ToArray());
        _rig.Handler.AssertNoRequests();
    }
}
