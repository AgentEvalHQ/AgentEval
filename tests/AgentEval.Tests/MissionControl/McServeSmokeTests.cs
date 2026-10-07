// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER

using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentEval.Cli.Commands;
using AgentEval.MissionControl;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// S0 smoke: the tool detects a missing UI honestly, and the real HTTP stack (Kestrel,
/// not the in-memory WebApplicationFactory transport) serves <c>index.html</c> + GraphQL.
/// </summary>
/// <remarks>
/// <para>
/// What <see cref="StaticAssetServingTests"/> and <see cref="GraphQLSmokeTests"/> do NOT cover:
/// the real Kestrel binding. <c>WebApplicationFactory</c> uses an in-memory transport — if something
/// in the middleware chain is incompatible with the physical socket path these tests catch it;
/// the factory tests do not. The <c>MapStaticAssets</c> blank-portal bug (serving 200 with an empty
/// body for every asset because the pre-compressed variant list wasn't present) is exactly the kind
/// of defect that passes a factory test and fails here.
/// </para>
/// <para>
/// The Kestrel test builds its own minimal wwwroot/ so it runs whether or not
/// <c>npm run build</c> has been executed — no SPA build required.
/// </para>
/// </remarks>
public class McServeSmokeTests
{
    // ─── MissingWebUiReason ─────────────────────────────────────────────────

    [Fact]
    public void MissingWebUiReason_WwwrootAbsent_ReturnsMessage()
    {
        // A brand-new install with no npm build must not claim the portal works.
        var dir = Path.Combine(Path.GetTempPath(), "mc-smoke-nodir-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var reason = McServeCommand.MissingWebUiReason(dir);
            Assert.NotNull(reason);
            Assert.Contains("wwwroot", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void MissingWebUiReason_WwwrootPresentNoIndex_ReturnsMessage()
    {
        // wwwroot/ exists (partial SPA build?) but index.html is missing — still not usable.
        var dir = Path.Combine(Path.GetTempPath(), "mc-smoke-noindex-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "wwwroot", "assets"));
        try
        {
            var reason = McServeCommand.MissingWebUiReason(dir);
            Assert.NotNull(reason);
            Assert.Contains("index.html", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void MissingWebUiReason_WwwrootWithIndex_ReturnsNull()
    {
        // A complete SPA build — no complaint.
        var dir = Path.Combine(Path.GetTempPath(), "mc-smoke-ok-" + Guid.NewGuid().ToString("N")[..8]);
        var wwwroot = Path.Combine(dir, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><html><body><div id=\"root\"></div></body></html>");
        try { Assert.Null(McServeCommand.MissingWebUiReason(dir)); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ─── real-Kestrel smoke ──────────────────────────────────────────────────

    /// <summary>
    /// Boots the MC host on a real Kestrel socket with a minimal wwwroot/ and verifies:
    /// <list type="bullet">
    ///   <item><c>GET /</c> returns <c>text/html</c> with the SPA mount point.</item>
    ///   <item><c>GET /runs/some-id</c> (a client-side route) also returns index.html — react-router fallback.</item>
    ///   <item><c>POST /graphql { ping }</c> returns <c>pong</c>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task McHost_OverRealKestrel_ServesIndexHtmlAndGraphQL()
    {
        // Build a minimal content root: just wwwroot/index.html.
        // ContentRootPath defaults to the process CWD in a real `dotnet run`; here we
        // override it explicitly so the test is hermetic regardless of where the test runner sits.
        const string IndexHtml =
            "<!doctype html><html><body><div id=\"root\"></div></body></html>";

        var tempRoot = Path.Combine(Path.GetTempPath(), "mc-kestrel-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        var wwwroot = Path.Combine(tempRoot, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), IndexHtml);

        var port = PickFreePort();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = tempRoot,
        });
        // Point the workspace at an empty dir — no .agenteval/ folder, which is
        // exactly the "fresh install" state the smoke test should handle gracefully.
        builder.Configuration["AgentEval:Root"] = tempRoot;
        McHost.ConfigureServices(builder);

        var app = builder.Build();
        // app.Urls must be set before StartAsync; this is the portable way to
        // bind a specific port without touching environment variables or Kestrel
        // internals (ConfigureWebHostBuilder does not expose UseUrls / ConfigureKestrel
        // in the minimal hosting model).
        app.Urls.Add($"http://127.0.0.1:{port}");
        McHost.ConfigurePipeline(app);
        await app.StartAsync();

        try
        {
            using var http = new HttpClient();

            // GET / — must return the SPA shell.
            var indexResp = await http.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal(HttpStatusCode.OK, indexResp.StatusCode);
            Assert.Equal("text/html", indexResp.Content.Headers.ContentType?.MediaType);
            var html = await indexResp.Content.ReadAsStringAsync();
            Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("id=\"root\"", html, StringComparison.Ordinal);

            // GET /runs/some-id (client-side route) — must fall back to index.html.
            var routeResp = await http.GetAsync($"http://127.0.0.1:{port}/runs/some-run-id");
            Assert.Equal(HttpStatusCode.OK, routeResp.StatusCode);
            Assert.Contains("id=\"root\"", await routeResp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // POST /graphql ping — must return pong.
            var gqlResp = await http.PostAsync(
                $"http://127.0.0.1:{port}/graphql",
                new StringContent("{\"query\":\"{ ping }\"}", Encoding.UTF8, "application/json"));
            Assert.True(gqlResp.IsSuccessStatusCode, $"POST /graphql returned {(int)gqlResp.StatusCode}");
            var gqlBody = await gqlResp.Content.ReadAsStringAsync();
            Assert.Contains("pong", gqlBody, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

#endif
