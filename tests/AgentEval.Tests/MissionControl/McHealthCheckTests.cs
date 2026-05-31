// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net;
using AgentEval.MissionControl;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Tests for <see cref="McHealthCheck"/> (SEC-14) — the self-contained container health probe that
/// replaces the curl-based Docker HEALTHCHECK. Covers loopback URL/port resolution and the
/// status→exit-code mapping (the network GET itself needs a running server and is not unit-tested here).
/// </summary>
public class McHealthCheckTests
{
    [Fact]
    public void ResolveProbeUrl_NullOrEmpty_DefaultsToLoopbackPort5000()
    {
        Assert.Equal("http://127.0.0.1:5000/api/v1/version", McHealthCheck.ResolveProbeUrl(null));
        Assert.Equal("http://127.0.0.1:5000/api/v1/version", McHealthCheck.ResolveProbeUrl(""));
        Assert.Equal("http://127.0.0.1:5000/api/v1/version", McHealthCheck.ResolveProbeUrl("   "));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000", 5000)]      // the container's default bind — host forced to loopback
    [InlineData("http://+:8080", 8080)]            // wildcard host (invalid Uri host) — port still extracted
    [InlineData("http://*:8443/", 8443)]
    [InlineData("http://localhost:1234", 1234)]
    [InlineData("https://0.0.0.0:7000;http://0.0.0.0:7001", 7000)] // ';'-separated → first entry's port
    public void ResolveProbeUrl_ForcesLoopbackHostAndExtractsPort(string aspnetcoreUrls, int expectedPort)
    {
        var url = McHealthCheck.ResolveProbeUrl(aspnetcoreUrls);

        Assert.Equal($"http://127.0.0.1:{expectedPort}/api/v1/version", url);
        Assert.StartsWith("http://127.0.0.1:", url); // never leaves the netns, even for a 0.0.0.0 bind
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.NoContent, 0)]
    [InlineData(HttpStatusCode.Accepted, 0)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.Found, 1)] // 3xx is not healthy
    public void MapStatusToExitCode_OnlyTwoHundredsAreHealthy(HttpStatusCode status, int expectedExit)
    {
        Assert.Equal(expectedExit, McHealthCheck.MapStatusToExitCode(status));
    }

    [Fact]
    public async Task RunAsync_NoServerListening_ReturnsUnhealthy()
    {
        // No server on the resolved port → connection refused → exit code 1 (unhealthy), not an exception.
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:59999");
            var exit = await McHealthCheck.RunAsync();
            Assert.Equal(1, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previous);
        }
    }
}

#endif
