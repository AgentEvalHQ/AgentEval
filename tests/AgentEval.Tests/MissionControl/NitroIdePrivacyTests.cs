// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using ChilliCream.Nitro.App;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Pins the two Nitro GraphQL IDE options that <c>McHost.ConfigurePipeline</c> sets on the
/// <c>/graphql</c> endpoint so that opening the IDE does not contact ChilliCream:
/// <list type="bullet">
///   <item><c>DisableTelemetry = true</c>: without it, the IDE's browser code POSTs a device id,
///         OS and user agent to telemetry.chillicream.com.</item>
///   <item><c>ServeMode = Embedded</c>: Nitro's default (<c>Latest</c>) makes the server proxy the
///         IDE's files from cdn.chillicream.com on each request.</item>
/// </list>
/// PRIVACY.md describes both; these tests fail if either setting is dropped.
/// </summary>
public class NitroIdePrivacyTests : IClassFixture<WebApplicationFactory<Query>>
{
    private readonly WebApplicationFactory<Query> _factory;

    public NitroIdePrivacyTests(WebApplicationFactory<Query> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NitroConfig_ServedToTheBrowser_DisablesTelemetry()
    {
        // Nitro's own options-file middleware answers this path locally (it runs before the
        // CDN middleware), so the request never leaves the test host. Before the fix the JSON
        // had no "disableTelemetry" property at all, which the IDE treats as "telemetry on".
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/graphql/nitro-config.json");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(
            doc.RootElement.TryGetProperty("disableTelemetry", out var disableTelemetry),
            "nitro-config.json must carry disableTelemetry; without it the Nitro IDE sends its usage ping.");
        Assert.Equal(JsonValueKind.True, disableTelemetry.ValueKind);
    }

    [Fact]
    public void GraphQLEndpoint_EffectiveNitroOptions_ServeEmbeddedCopyWithTelemetryOff()
    {
        // Starting the client starts the host, which runs McHost.ConfigurePipeline and maps /graphql.
        using var client = _factory.CreateClient();

        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText?.StartsWith("/graphql", StringComparison.Ordinal) == true
                         && e.Metadata.GetOrderedMetadata<NitroAppOptions>().Count > 0);

        // Same merge the Nitro middleware performs per request: start from NitroAppOptions.Default,
        // then apply each NitroAppOptions in the endpoint metadata, in order.
        var effective = NitroAppOptions.Default.Clone();
        foreach (var options in endpoint.Metadata.GetOrderedMetadata<NitroAppOptions>())
            options.CopyTo(effective);

        // Before the fix: ServeMode.Latest (server-side proxy to cdn.chillicream.com) and
        // DisableTelemetry == null.
        Assert.Same(ServeMode.Embedded, effective.ServeMode);
        Assert.True(effective.DisableTelemetry);
    }
}

#endif
