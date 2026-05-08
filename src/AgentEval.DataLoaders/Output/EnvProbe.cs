// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace AgentEval.Output;

internal static class EnvProbe
{
    /// <summary>Collects runtime environment information from the current process.</summary>
    public static EnvRef Probe()
    {
        var ciSystem = DetectCiSystem();
        var runner = DetectRunner(ciSystem);
        return new EnvRef(
            Machine: Environment.MachineName,
            Os: RuntimeInformation.OSDescription,
            DotnetVersion: RuntimeInformation.FrameworkDescription,
            Ci: ciSystem is not null,
            CiSystem: ciSystem,
            Runner: runner);
    }

    private static string? DetectCiSystem()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))) return "GitHub Actions";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"))) return "Azure DevOps";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITLAB_CI"))) return "GitLab CI";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CIRCLECI"))) return "CircleCI";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) return "CI";
        return null;
    }

    private static string? DetectRunner(string? ciSystem)
    {
        if (ciSystem == "GitHub Actions")
            return Environment.GetEnvironmentVariable("RUNNER_NAME");
        if (ciSystem == "Azure DevOps")
            return Environment.GetEnvironmentVariable("Agent.Name");
        return null;
    }
}
