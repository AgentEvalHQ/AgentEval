// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using AgentEval.MissionControl.Hosting;
using AgentEval.MissionControl.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentEval.Tests.MissionControl;

public sealed class OtlpEndpointTests : IDisposable
{
    private const string Key = "fixture-otlp-key-01234567890123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-otlp-" + Guid.NewGuid().ToString("N"));
    private async Task<WebApplication> Start(bool enabled = true, string? remote = "127.0.0.1", bool https = false,
        Dictionary<string, string?>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        var settings = new Dictionary<string, string?>
        {
            ["AgentEval:Root"] = Path.Combine(_root, "viewer"),
            ["AgentEval:Otlp:Enabled"] = enabled.ToString(),
            ["AgentEval:Otlp:Root"] = Path.Combine(_root, "observations"),
            ["AgentEval:Otlp:Source"] = "receiver", ["AgentEval:Otlp:ApiKey"] = Key
        };
        if (overrides is not null) foreach (var pair in overrides) settings[pair.Key] = pair.Value;
        builder.Configuration.AddInMemoryCollection(settings);
        OtlpHosting.Configure(builder);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
            context.Request.Scheme = https ? "https" : "http";
            await next(context);
        });
        app.MapOtlpEndpoints();
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
    private static HttpClient Client(WebApplication app, bool authorized = true)
    {
        var client = app.GetTestClient();
        if (authorized) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        return client;
    }
    private static ByteArrayContent Body(byte[] bytes, string type = "application/json", bool gzip = false)
    {
        if (gzip)
        {
            using var stream = new MemoryStream();
            using (var compressor = new GZipStream(stream, CompressionMode.Compress, true)) compressor.Write(bytes);
            bytes = stream.ToArray();
            // GZipStream can emit no envelope if no input was written. Use an actual empty gzip member.
            if (bytes.Length == 0) bytes = Convert.FromHexString("1f8b080000000000000303000000000000000000");
        }
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (gzip) content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Export_EmptyValidRequest_AcknowledgesDurableRecordInMatchingFormat(bool json, bool gzip)
    {
        await using var app = await Start();
        using var client = Client(app);
        var type = json ? "application/json" : "application/x-protobuf";
        using var body = Body(json ? "{}"u8.ToArray() : [], type, gzip);
        var response = await client.PostAsync("/v1/traces", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(json ? "{}" : "", await response.Content.ReadAsStringAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "observations", "records")));
        var retry = await client.PostAsync("/v1/traces", body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "observations", "records")));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Export_InvalidPayload_ReturnsStatusMessageAndNoAcceptedRecord(bool json)
    {
        await using var app = await Start();
        using var client = Client(app);
        using var body = Body([0], json ? "application/json" : "application/x-protobuf");
        var response = await client.PostAsync("/v1/traces", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (json) Assert.Contains("message", Encoding.UTF8.GetString(bytes));
        else { Assert.Equal(0x12, bytes[0]); Assert.Equal(bytes.Length - 2, bytes[1]); }
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "observations", "records")));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 8)]
    [InlineData(false, 1)]
    [InlineData(false, 8)]
    public async Task Export_TruncatedGzip_ReturnsBadRequestWithoutAcceptingRecord(bool json, int missingBytes)
    {
        await using var app = await Start();
        using var client = Client(app);
        var type = json ? "application/json" : "application/x-protobuf";
        using var compressed = Body(json ? "{}"u8.ToArray() : [], type, gzip: true);
        var wire = await compressed.ReadAsByteArrayAsync();
        using var body = Body(wire[..^missingBytes], type);
        body.Headers.ContentEncoding.Add("gzip");
        var response = await client.PostAsync("/v1/traces", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "observations", "records")));
    }

    [Theory]
    [InlineData(false, null, "127.0.0.1", false, 401)]
    [InlineData(true, "Origin", "127.0.0.1", false, 403)]
    [InlineData(true, "Sec-Fetch-Site", "127.0.0.1", false, 403)]
    [InlineData(true, null, "192.0.2.1", false, 403)]
    [InlineData(true, null, null, false, 403)]
    [InlineData(true, null, "192.0.2.1", true, 200)]
    public async Task Export_AccessPolicy_EnforcesOwnerOriginAndDirectPeer(bool authorized, string? header, string? remote, bool https, int status)
    {
        await using var app = await Start(remote: remote, https: https);
        using var client = Client(app, authorized);
        if (header is not null) client.DefaultRequestHeaders.TryAddWithoutValidation(header, "fixture");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var body = Body("{}"u8.ToArray());
        Assert.Equal(status, (int)(await client.PostAsync("/v1/traces", body)).StatusCode);
    }

    [Fact]
    public async Task Export_Disabled_DoesNotExposeEndpointOrCreateStore()
    {
        await using var app = await Start(enabled: false);
        using var client = Client(app);
        using var body = Body("{}"u8.ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/v1/traces", body)).StatusCode);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("Root", "relative")]
    [InlineData("Source", "")]
    [InlineData("ApiKey", "short")]
    [InlineData("Capture", "true")]
    [InlineData("MaxConcurrentRequests", "0")]
    public async Task Startup_InvalidConfiguration_RefusesBeforeWriting(string key, string value)
    {
        var error = await Record.ExceptionAsync(() => Start(overrides: new() { ["AgentEval:Otlp:" + key] = value }));
        Assert.True(error is ArgumentException or InvalidOperationException);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("viewer/.agenteval")]
    [InlineData("viewer")]
    [InlineData("viewer/.agenteval/nested")]
    public async Task Startup_OverlappingViewer_RefusesBeforeWriting(string relative)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(overrides: new() { ["AgentEval:Otlp:Root"] = Path.Combine(_root, relative) }));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Startup_OverlappingAefRoot_RefusesBeforeWriting()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(overrides: new()
        { ["AgentEval:Ingestion:Root"] = Path.Combine(_root, "observations", "aef") }));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_WireOrDecodedByteLimit_RefusesWithoutRecord(bool compressed)
    {
        await using var app = await Start(overrides: new()
        { ["AgentEval:Otlp:" + (compressed ? "MaxDecodedBytes" : "MaxRequestBytes")] = "16" });
        using var client = Client(app);
        using var body = Body(Encoding.UTF8.GetBytes("{\"unknown\":\"" + new string('a', 1000) + "\"}"), gzip: compressed);
        var response = await client.PostAsync("/v1/traces", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "observations", "records")));
    }

    [Fact]
    public async Task Export_StorageCapacity_ReturnsRetryableFailureWithoutSuccessAcknowledgement()
    {
        await using var app = await Start(overrides: new() { ["AgentEval:Otlp:MaxStoredBytes"] = "1" });
        using var client = Client(app);
        using var body = Body("{}"u8.ToArray());
        var response = await client.PostAsync("/v1/traces", body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "observations", "records")));
        Assert.DoesNotContain(_root, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_SpanContent_DefaultOffIsEnforcedBeforeHttpSuccess()
    {
        await using var app = await Start();
        using var client = Client(app);
        using var body = Body(Encoding.UTF8.GetBytes("""
            {"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"0123456789abcdef0123456789abcdef",
            "spanId":"0123456789abcdef","name":"private-prompt","attributes":[{"key":"private","value":{"stringValue":"private-output"}}]}]}]}]}
            """));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/traces", body)).StatusCode);
        var record = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(_root, "observations", "records"))));
        Assert.DoesNotContain("private-prompt", record);
        Assert.DoesNotContain("private-output", record);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
#endif
