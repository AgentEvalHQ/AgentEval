// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Http.Headers;
using AgentEval.MissionControl.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace AgentEval.Tests.MissionControl;

public sealed class EvidenceReadAccessTests
{
    private static readonly string[] ReadRoutes = ["/graphql", "/api/v1/evidence/events", "/api/v1/runs/id/trace",
        "/api/v1/runs/id/reports/md", "/api/v1/runs/id/artifacts/E-data", "/api/v1/compliance/gdpr/agent/time/report.pdf",
        "/api/v1/subjects/agent/name/history"];
    private const string Key = "fixture-only-read-key-0123456789012345";
    private static async Task<WebApplication> Start(string? peer = "127.0.0.1", bool https = false,
        bool key = false, bool enabled = true, bool realSocket = false)
    {
        var builder = WebApplication.CreateBuilder();
        if (realSocket) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentEval:Queries:Enabled"] = enabled.ToString(),
            ["AgentEval:Queries:ReadApiKey"] = key ? Key : null
        });
        EvidenceReadAccess.Configure(builder);
        var app = builder.Build();
        if (!realSocket) app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
            context.Request.Scheme = https ? "https" : "http";
            await next(context);
        });
        EvidenceReadAccess.Use(app);
        app.MapGet("/graphql", () => "authorized-read");
        app.MapGet("/api/v1/evidence/events", () => "authorized-stream");
        foreach (var path in ReadRoutes.Skip(2)) app.MapGet(path, () => "authorized-artifact");
        app.MapGet("/unrelated", () => "unchanged");
        await app.StartAsync(); return app;
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost", false, false, false, null, null, 200)]
    [InlineData("::1", "[::1]", false, false, false, null, null, 200)]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1", false, false, false, null, null, 200)]
    [InlineData(null, "localhost", false, false, false, null, null, 403)]
    [InlineData("192.0.2.1", "localhost", false, false, false, null, null, 403)]
    [InlineData("127.0.0.1", "rebinding.example", false, false, false, null, null, 403)]
    [InlineData("127.0.0.1", "localhost", false, false, false, "http://evil.example", null, 403)]
    [InlineData("127.0.0.1", "localhost", false, false, false, "null", null, 403)]
    [InlineData("127.0.0.1", "localhost", false, false, false, "http://localhost", "same-origin", 200)]
    [InlineData("127.0.0.1", "localhost", false, false, false, null, "cross-site", 403)]
    [InlineData("127.0.0.1", "localhost", false, false, false, null, "same-site", 403)]
    [InlineData("127.0.0.1", "localhost", false, true, false, null, null, 401)]
    [InlineData("127.0.0.1", "localhost", false, true, true, null, null, 200)]
    [InlineData("192.0.2.1", "service.example", false, true, true, null, null, 403)]
    [InlineData("192.0.2.1", "service.example", true, true, false, null, null, 401)]
    [InlineData("192.0.2.1", "service.example", true, true, true, "https://service.example", "same-origin", 200)]
    [InlineData("192.0.2.1", "service.example", true, false, false, null, null, 403)]
    public async Task ReadAccess_PeerHostOriginAndKey_EnforceSameBoundaryForQueriesStreamsAndDownloads(string? peer, string host,
        bool https, bool configuredKey, bool correctKey, string? origin, string? site, int expected)
    {
        await using var app = await Start(peer, https, configuredKey); using var client = app.GetTestClient();
        foreach (var path in ReadRoutes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = host;
            if (configuredKey) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", correctKey ? Key : "wrong");
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
            request.Headers.Add("X-Forwarded-For", "127.0.0.1"); request.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            if (expected != 200) Assert.DoesNotContain("authorized", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ReadAccess_RealLoopbackSocket_AcceptsLocalAndRefusesRebindingHost()
    {
        await using var app = await Start(realSocket: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var allowed = await client.GetAsync("/graphql"); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/evidence/events"); forged.Headers.Host = "rebinding.example";
        using var refused = await client.SendAsync(forged); Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task ReadAccess_DisabledCapabilityAndUnrelatedRoutes_PreserveExistingBehavior()
    {
        await using var disabled = await Start(peer: "192.0.2.1", enabled: false); using var client = disabled.GetTestClient();
        foreach (var path in ReadRoutes)
        {
            using var legacy = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
            Assert.Null(legacy.Headers.CacheControl);
        }
        await using var enabled = await Start(peer: "192.0.2.1"); using var enabledClient = enabled.GetTestClient();
        using var unrelated = await enabledClient.GetAsync("/unrelated"); Assert.Equal(HttpStatusCode.OK, unrelated.StatusCode);
    }
}
#endif
