// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Headers;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// The serving contract for the portal's files, requested the way a browser requests them (<c>Accept-Encoding:
/// gzip, br</c>): a script arrives whole and typed as JavaScript, and a page or client-side route arrives as HTML.
/// <para><b>What these tests do NOT catch.</b> Under <c>MapStaticAssets</c>, the copy <c>agenteval mc serve</c>
/// launches answered a browser with 200, an empty body and no content type (its build manifest lists pre-compressed
/// variants that copy does not have), so with <c>nosniff</c> the bundle never ran and the portal was blank. The
/// in-process test host has those variants, so these tests pass with <c>MapStaticAssets</c> too (checked by putting
/// it back on 2026-10-02). The fix (<c>UseStaticFiles</c>) was verified against the real <c>mc serve</c> in a browser
/// the same day. They serve a temporary web root, so they need no SPA build.</para>
/// </summary>
public sealed class StaticAssetServingTests : IClassFixture<WebApplicationFactory<Query>>, IDisposable
{
    private const string Script = "console.log('mission control static test');";
    private const string Index = "<!doctype html><html><body><div id=\"root\"></div><script type=\"module\" src=\"/assets/mc-static-test.js\"></script></body></html>";

    private readonly WebApplicationFactory<Query> _factory;
    private readonly string _webRoot;

    public StaticAssetServingTests(WebApplicationFactory<Query> factory)
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "mc-webroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), Index);
        File.WriteAllText(Path.Combine(_webRoot, "assets", "mc-static-test.js"), Script);
        _factory = factory.WithWebHostBuilder(builder => builder.UseWebRoot(_webRoot));
    }

    public void Dispose()
    {
        try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private HttpClient BrowserLikeClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br, zstd");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    [Fact]
    public async Task Script_RequestedLikeABrowser_ComesBackWhole_AsJavaScript()
    {
        using var client = BrowserLikeClient();

        var response = await client.GetAsync("/assets/mc-static-test.js");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}");
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Script, body);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/subjects/agent/SomeAgent")]   // a client-side route falls back to index.html
    public async Task Pages_RequestedLikeABrowser_ServeIndexHtml(string path)
    {
        using var client = BrowserLikeClient();

        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}");
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        // The test host also serves the project's own wwwroot when a local SPA build exists, and that index.html
        // wins over this test's; either way the page must arrive whole, as HTML, with the SPA's mount point.
        Assert.StartsWith("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"root\"", body, StringComparison.Ordinal);
    }
}

#endif
