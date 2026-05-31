// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// T3.9 — Dockerfile HEALTHCHECK directive integration smoke.
//
// The Dockerfile-content assertion always runs (it's static + cheap).
// The full docker-build smoke is gated behind an opt-in env var
// (AGENTEVAL_RUN_DOCKER_TESTS=1) because the build + inspect cycle takes
// minutes and Docker is not present on every CI matrix or dev box. When
// the env var is unset the test logs a skip-equivalent message and
// returns success — xUnit has no first-class runtime skip without the
// Xunit.SkippableFact package, which we deliberately do not pull in for
// a single docker smoke. The full "container reports healthy" assertion
// lives in the manual smoke documented in the Dockerfile header.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace AgentEval.Tests.Docker;

public sealed class HealthCheckTests
{
    [Fact]
    public async Task Dockerfile_DeclaresHealthCheckDirective()
    {
        var repoRoot = LocateRepoRoot();
        var dockerfile = Path.Combine(repoRoot, "Dockerfile");
        Assert.True(File.Exists(dockerfile), $"Dockerfile not found at {dockerfile}");

        var contents = await File.ReadAllTextAsync(dockerfile);
        Assert.Contains("HEALTHCHECK", contents);
        // SEC-14: the HEALTHCHECK uses the self-contained internal probe (no curl/wget). Assert the probe
        // is the app in --health-check mode, and that the runtime stage installs NO apt packages (the curl
        // install was the unpinned attack surface we removed).
        Assert.Contains("--health-check", contents);
        Assert.Contains("AgentEval.MissionControl.dll", contents);
        Assert.DoesNotContain("apt-get install", contents);
    }

    [Fact]
    public void DockerBuild_HealthcheckIsConfiguredOnImage()
    {
        // Opt-in smoke: only run when explicitly requested AND docker is
        // present. xUnit lacks a first-class runtime skip without an
        // extra package, so we early-return on miss.
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_DOCKER_TESTS") != "1")
        {
            return;
        }
        if (!IsDockerAvailable())
        {
            return;
        }

        var repoRoot = LocateRepoRoot();
        var tag = $"agenteval-mc-healthcheck-test:{Guid.NewGuid():N}";

        try
        {
            var build = RunDocker($"build -t {tag} \"{repoRoot}\"", timeoutMs: 600_000);
            Assert.True(build.ExitCode == 0, $"docker build failed: {build.Stderr}");

            var inspect = RunDocker(
                $"inspect --format=\"{{{{json .Config.Healthcheck}}}}\" {tag}",
                timeoutMs: 30_000);
            Assert.True(inspect.ExitCode == 0, $"docker inspect failed: {inspect.Stderr}");
            Assert.Contains("Test", inspect.Stdout);
            // SEC-14: the configured healthcheck CMD is the internal probe, not a curl GET.
            Assert.Contains("--health-check", inspect.Stdout);
        }
        finally
        {
            try { RunDocker($"rmi -f {tag}", timeoutMs: 30_000); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "version --format \"{{.Server.Version}}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            return p.WaitForExit(5_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDocker(string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process.");
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"docker {args} timed out after {timeoutMs}ms.");
        }
        return (p.ExitCode, p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not located.");
    }
}
