// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Diagnostics;
using AgentEval.Results.Adapters.Native;

namespace AgentEval.Cli.Infrastructure;

internal static class CliGitProvenance
{
    internal static async Task<AefCliProvenance> CaptureAsync(string verb, string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await CaptureAsync(verb, directory, timeout.Token);
    }

    internal static async Task<AefCliProvenance> CaptureAsync(string verb, string directory, CancellationToken token)
    {
        var commit = await ReadAsync(directory, ["rev-parse", "--verify", "HEAD"], token);
        var branch = await ReadAsync(directory, ["symbolic-ref", "--quiet", "--short", "HEAD"], token);
        var status = await ReadAsync(directory, ["status", "--porcelain", "--untracked-files=normal"], token);
        return new()
        {
            Verb = verb,
            GitCommit = commit is { Length: 40 or 64 } && commit.All(Uri.IsHexDigit) ? commit : null,
            GitBranch = branch is { Length: > 0 and <= 256 } && !branch.Any(char.IsControl) ? branch : null,
            GitDirty = status is null ? null : status.Length > 0,
        };
    }

    private static async Task<string?> ReadAsync(string directory, string[] arguments, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        using var process = new Process
        {
            StartInfo = new("git")
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--no-optional-locks");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token)).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            return process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception cleanup) when (cleanup is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return null;
        }
    }
}
