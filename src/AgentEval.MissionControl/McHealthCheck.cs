// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text.RegularExpressions;

namespace AgentEval.MissionControl;

/// <summary>
/// Self-contained container health probe (SEC-14). Invoked as
/// <c>dotnet AgentEval.MissionControl.dll --health-check</c> from the Docker <c>HEALTHCHECK</c>: it
/// performs a single loopback HTTP GET against the running server's <c>/api/v1/version</c> endpoint and
/// returns process exit code 0 (healthy) / 1 (unhealthy). Because the .NET runtime is already present in
/// the image, this removes the need to <c>apt-get install curl</c> into the runtime stage — shrinking the
/// attack surface and dropping an unpinned apt package.
/// </summary>
public static class McHealthCheck
{
    /// <summary>The CLI flag that triggers the health probe instead of starting the server.</summary>
    public const string Flag = "--health-check";

    /// <summary>
    /// Resolves the loopback probe URL from the container's <c>ASPNETCORE_URLS</c>. The host is ALWAYS
    /// forced to 127.0.0.1 (the probe must never leave the container netns even when the server binds
    /// 0.0.0.0); only the port is taken from the configured URLs. Defaults to port 5000.
    /// </summary>
    public static string ResolveProbeUrl(string? aspnetcoreUrls)
    {
        var port = 5000;
        if (!string.IsNullOrWhiteSpace(aspnetcoreUrls))
        {
            // ASPNETCORE_URLS can be ';'-separated and use wildcard hosts (http://+:5000, http://0.0.0.0:5000,
            // http://*:8080) that are not valid Uri hosts — so extract the port textually from the first entry.
            var first = aspnetcoreUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            var m = Regex.Match(first, @":(\d{1,5})(?:/|$)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p is > 0 and <= 65535)
                port = p;
        }
        return $"http://127.0.0.1:{port}/api/v1/version";
    }

    /// <summary>Maps an HTTP status to a process exit code: any 2xx is healthy (0), everything else 1.</summary>
    public static int MapStatusToExitCode(HttpStatusCode status) =>
        (int)status is >= 200 and < 300 ? 0 : 1;

    /// <summary>
    /// Runs the probe against the resolved loopback URL and returns the process exit code. Any transport
    /// failure (connection refused, timeout, DNS) maps to 1 (unhealthy).
    /// </summary>
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var url = ResolveProbeUrl(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var resp = await http.GetAsync(url, ct);
            return MapStatusToExitCode(resp.StatusCode);
        }
        catch
        {
            return 1;
        }
    }
}
