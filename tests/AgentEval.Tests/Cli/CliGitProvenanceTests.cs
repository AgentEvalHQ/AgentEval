// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Diagnostics;
using AgentEval.Cli.Infrastructure;

namespace AgentEval.Tests.Cli;

public sealed class CliGitProvenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cli-git-" + Guid.NewGuid().ToString("N"));
    public CliGitProvenanceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Capture_Repository_ReportsCleanDirtyAndDetachedWithoutPaths()
    {
        await Git("init", "--initial-branch=feature/provenance");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--allow-empty", "-m", "fixture");
        var clean = await CliGitProvenance.CaptureAsync("bench gdpr", _root);
        Assert.Equal(40, clean.GitCommit!.Length);
        Assert.Equal("feature/provenance", clean.GitBranch);
        Assert.False(clean.GitDirty);
        File.WriteAllText(Path.Combine(_root, "private-input.txt"), "private body");
        var dirty = await CliGitProvenance.CaptureAsync("eval", _root);
        Assert.True(dirty.GitDirty);
        Assert.Equal(clean.GitCommit, dirty.GitCommit);
        await Git("checkout", "--detach", "HEAD");
        var detached = await CliGitProvenance.CaptureAsync("redteam", _root);
        Assert.Null(detached.GitBranch);
        Assert.Equal(clean.GitCommit, detached.GitCommit);
        var json = System.Text.Json.JsonSerializer.Serialize(detached);
        Assert.DoesNotContain(_root, json);
        Assert.DoesNotContain("private-input", json);
        Assert.DoesNotContain("private body", json);
    }

    [Fact]
    public async Task Capture_OutsideRepository_KeepsUnknownFieldsAbsent()
    {
        var snapshot = await CliGitProvenance.CaptureAsync("eval", _root);
        Assert.Equal("eval", snapshot.Verb);
        Assert.Null(snapshot.GitCommit);
        Assert.Null(snapshot.GitBranch);
        Assert.Null(snapshot.GitDirty);
    }

    private async Task Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, errors, process.WaitForExitAsync());
        Assert.True(process.ExitCode == 0, await errors);
    }
}
