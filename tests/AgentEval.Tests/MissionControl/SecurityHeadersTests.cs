// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Phase-5 Task 5.3 — pins defence-in-depth response headers (CSP, nosniff,
/// X-Frame-Options, Referrer-Policy) on the Mission Control surface. A future
/// refactor that drops the header middleware in <c>McHost.ConfigurePipeline</c>
/// must not pass these assertions.
/// </summary>
public class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Query>>
{
    private readonly WebApplicationFactory<Query> _factory;

    public SecurityHeadersTests(WebApplicationFactory<Query> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/graphql")]
    [InlineData("/api/v1/version")]
    public async Task GET_KnownEndpoints_EmitDefenceInDepthHeaders(string path)
    {
        using var client = _factory.CreateClient();

        // GraphQL responds to POST, but the security-header middleware runs
        // on the request pipeline regardless of verb / status code. A GET to
        // /graphql returns 405 but should still carry the headers.
        var response = path == "/graphql"
            ? await client.PostAsJsonAsync(path, new { query = "{ __typename }" })
            : await client.GetAsync(path);

        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY",    string.Join(",", response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", string.Join(",", response.Headers.GetValues("Referrer-Policy")));

        var csp = string.Join(",", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    [Fact]
    public async Task GET_UnknownPath_StillEmitsHeaders()
    {
        // The middleware runs before routing — even a 404 should carry the headers
        // so a malicious response body can never be sniffed-into-html or framed.
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/this-path-does-not-exist");

        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY",    string.Join(",", response.Headers.GetValues("X-Frame-Options")));
    }
}

#endif
