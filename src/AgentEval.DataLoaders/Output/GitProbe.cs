// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Output;

namespace AgentEval.Output;

internal static class GitProbe
{
    /// <summary>Probes git state from <paramref name="root"/>; returns empty refs on any failure.</summary>
    public static GitRef Probe(string root)
    {
        try
        {
            var commit = RunGit(root, "rev-parse HEAD");
            var branch = RunGit(root, "rev-parse --abbrev-ref HEAD");
            var status = RunGit(root, "status --porcelain");
            var tag = RunGit(root, "describe --tags --exact-match");
            var dirty = !string.IsNullOrEmpty(status);
            return new GitRef(
                string.IsNullOrEmpty(commit) ? null : commit,
                string.IsNullOrEmpty(branch) ? null : branch,
                dirty,
                string.IsNullOrEmpty(tag) ? null : tag);
        }
        catch
        {
            return new GitRef(null, null, false, null);
        }
    }

    private static string? RunGit(string workDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }
}
