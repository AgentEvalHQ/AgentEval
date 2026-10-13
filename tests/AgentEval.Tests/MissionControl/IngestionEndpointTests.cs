// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentEval.MissionControl.Hosting;
using AgentEval.MissionControl.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentEval.Tests.MissionControl;

public sealed class IngestionEndpointTests : IDisposable
{
    private const string Key = "fixture-only-ingestion-key-0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-upload-" + Guid.NewGuid().ToString("N"));

    private async Task<WebApplication> Start(bool enabled = true, long bytes = 4096, long fileBytes = 2048,
        string? remote = "127.0.0.1", bool https = false, string? key = Key)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentEval:Root"] = Path.Combine(_root, "viewer"),
            ["AgentEval:Ingestion:Enabled"] = enabled.ToString(),
            ["AgentEval:Ingestion:Root"] = Path.Combine(_root, "ingestion"),
            ["AgentEval:Ingestion:ApiKey"] = key,
            ["AgentEval:Ingestion:MaxBytes"] = bytes.ToString(),
            ["AgentEval:Ingestion:MaxFileBytes"] = fileBytes.ToString()
        });
        IngestionHosting.Configure(builder);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
            context.Request.Scheme = https ? "https" : "http";
            await next(context);
        });
        app.MapIngestionEndpoints();
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private static MultipartFormDataContent Files(params (string Path, string Text)[] files)
    {
        var content = new MultipartFormDataContent("aef-fixture-boundary");
        foreach (var file in files) content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(file.Text)), "file", file.Path);
        return content;
    }

    private static HttpClient Client(WebApplication app, bool authorized = true)
    {
        var client = app.GetTestClient();
        if (authorized) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        return client;
    }

    private string Staging => Path.Combine(_root, "ingestion", ".staging");

    [Fact]
    public async Task Upload_ValidMultipart_StagesRawBytesWithoutPublishingOrExposingPaths()
    {
        await using var app = await Start();
        using var client = Client(app);
        using var content = Files(("run.json", "unverified bytes"), ("ext/vendor/a.json", "{}"));
        var response = await client.PostAsync("/api/v1/ingestion/runs", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_root, text);
        using var json = JsonDocument.Parse(text);
        Assert.False(json.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal("staged", json.RootElement.GetProperty("status").GetString());
        var id = json.RootElement.GetProperty("stageId").GetString()!;
        Assert.True(Guid.TryParseExact(id, "N", out _));
        Assert.Equal("unverified bytes", File.ReadAllText(Path.Combine(Staging, id, "content", "run.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "ingestion", "runs")));
    }

    [Fact]
    public async Task Upload_Disabled_HasNoRouteOrStorage()
    {
        await using var app = await Start(enabled: false);
        using var client = Client(app);
        var response = await client.PostAsync("/api/v1/ingestion/runs", Files(("run.json", "{}")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task Startup_InvalidSecret_RefusesBeforeCreatingStorage(string? key)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(key: key));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a-wrong-but-long-enough-secret-0000")]
    public async Task Upload_Unauthorized_ReservesNoStage(string? key)
    {
        await using var app = await Start();
        using var client = Client(app, false);
        if (key is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        var response = await client.PostAsync("/api/v1/ingestion/runs", Files(("run.json", "{}")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Theory]
    [InlineData("Origin", "https://untrusted.example")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    public async Task Upload_BrowserOrigin_Refuses(string header, string value)
    {
        await using var app = await Start();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(header, value);
        var response = await client.PostAsync("/api/v1/ingestion/runs", Files(("run.json", "{}")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Theory]
    [InlineData("192.0.2.1", false, 403)]
    [InlineData(null, false, 403)]
    [InlineData("192.0.2.1", true, 202)]
    public async Task Upload_RemoteTransport_RequiresHttps(string? remote, bool https, int expected)
    {
        await using var app = await Start(remote: remote, https: https);
        using var client = Client(app);
        var response = await client.PostAsync("/api/v1/ingestion/runs", Files(("run.json", "{}")));
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("../outside", "run.json", 400)]
    [InlineData("run.json", "run.json", 409)]
    [InlineData("Ext/a", "ext/b", 409)]
    public async Task Upload_BadOrConflictingPaths_RetainsFailedStage(string first, string second, int expected)
    {
        await using var app = await Start();
        using var client = Client(app);
        var response = await client.PostAsync("/api/v1/ingestion/runs", Files((first, "one"), (second, "two")));
        Assert.Equal(expected, (int)response.StatusCode);
        var stage = Assert.Single(Directory.EnumerateDirectories(Staging));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "state.json")));
        Assert.Equal("failed", state.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Upload_FileTooLarge_Returns413AndDoesNotPublish()
    {
        await using var app = await Start(fileBytes: 3);
        using var client = Client(app);
        var response = await client.PostAsync("/api/v1/ingestion/runs", Files(("run.json", "four")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "ingestion", "runs")));
    }

    [Fact]
    public async Task Upload_RequestTooLarge_RefusesBeforeReservingStage()
    {
        await using var app = await Start(bytes: 100, fileBytes: 100);
        using var client = Client(app);
        var response = await client.PostAsync("/api/v1/ingestion/runs", Files(("run.json", new string('x', 101))));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task Upload_TruncatedMultipart_DoesNotReportStaged()
    {
        await using var app = await Start();
        using var client = Client(app);
        using var content = new StringContent("--boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"run.json\"\r\n\r\npartial");
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");
        var response = await client.PostAsync("/api/v1/ingestion/runs", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stage = Assert.Single(Directory.EnumerateDirectories(Staging));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "state.json")));
        Assert.Equal("failed", state.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LimitedStream_UnknownLength_EnforcesRawByteLimitAndLeavesInputOpen()
    {
        using var input = new MemoryStream(new byte[20]);
        using (var bounded = new LimitedUploadStream(input, 10))
        {
            await Assert.ThrowsAsync<AgentEval.Results.Ingestion.AefStagingException>(async () => await bounded.ReadAsync(new byte[30]));
            Assert.Equal(11, input.Position);
        }
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Upload_UnknownLengthBody_EnforcesRequestByteLimit()
    {
        await using var app = await Start(bytes: 512, fileBytes: 512);
        using var client = Client(app);
        using var content = new UnknownLengthContent(Files(("run.json", new string('x', 600))));
        var response = await client.PostAsync("/api/v1/ingestion/runs", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var stage = Assert.Single(Directory.EnumerateDirectories(Staging));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "state.json")));
        Assert.Equal("failed", state.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Upload_UnknownLengthEpilogue_EnforcesRequestByteLimit()
    {
        await using var app = await Start();
        using var client = Client(app);
        var text = "--boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"run.json\"\r\n\r\n{}\r\n--boundary--\r\n" + new string('x', 5000);
        using var original = new StringContent(text);
        original.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");
        using var content = new UnknownLengthContent(original);
        var response = await client.PostAsync("/api/v1/ingestion/runs", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _inner;
        internal UnknownLengthContent(HttpContent inner)
        {
            _inner = inner;
            Headers.ContentType = inner.Headers.ContentType;
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
#endif
