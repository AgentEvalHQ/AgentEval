// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentEval.Indexing;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using AgentEval.MissionControl.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AgentEval.Tests.MissionControl;

public sealed class EvidenceQueryBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-http-budget-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.sqlite");
    public EvidenceQueryBudgetTests() => Directory.CreateDirectory(_root);
    private WebApplicationFactory<Query> Factory() => new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
    {
        LoopbackTestConnection.Configure(builder);
        builder.UseSetting("AgentEval:Root", _root);
        builder.UseSetting("AgentEval:Queries:Enabled", "true");
        builder.UseSetting("AgentEval:Queries:IndexPath", Database);
        builder.UseSetting("AgentEval:Queries:HttpLimits:RequestBytes", "1024");
        builder.UseSetting("AgentEval:Queries:HttpLimits:QueryStringBytes", "1024");
        builder.UseSetting("AgentEval:Queries:HttpLimits:ResponseBytes", "1024");
    });

    [Theory]
    [InlineData(false, 1024, 200)]
    [InlineData(false, 1025, 413)]
    [InlineData(true, 1024, 200)]
    [InlineData(true, 1025, 413)]
    public async Task Query_RequestByteBoundary_AppliesWithAndWithoutContentLength(bool unknownLength, int bytes, int status)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var payload = Encoding.UTF8.GetBytes("{\"query\":\"{ping}\"}".PadRight(bytes));
        using HttpContent content = unknownLength ? new UnknownLengthContent(payload) : new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/graphql", content);
        Assert.Equal(status, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(status == 200 ? "pong" : "request-byte-limit", text);
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task Query_AliasedResponseExceedsCap_RefusesWholeJsonAndNextRequestStillWorks()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        var relativePath = new string('x', 850);
        writer.Upsert(new("root", relativePath, new string('a', 64), IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
            new("run", null, null, null, null, null, null, "unknown"), [], [], null));
        using var factory = Factory(); using var client = factory.CreateClient();
        const string field = "evidenceSources(pageSize:1) { data { items { relativePath } } }";
        using var small = await client.PostAsJsonAsync("/graphql", new { query = "{ a:" + field + " }" });
        small.EnsureSuccessStatusCode(); Assert.Contains(relativePath, await small.Content.ReadAsStringAsync());
        using var large = await client.PostAsJsonAsync("/graphql", new { query = "{ a:" + field + " b:" + field + " }" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, large.StatusCode);
        var text = await large.Content.ReadAsStringAsync();
        Assert.DoesNotContain(relativePath, text);
        using var body = JsonDocument.Parse(text);
        Assert.False(body.RootElement.TryGetProperty("data", out _));
        Assert.Equal("response-byte-limit", body.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        using var next = await client.PostAsJsonAsync("/graphql", new { query = "{ ping }" });
        next.EnsureSuccessStatusCode(); Assert.Contains("pong", await next.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Query_OversizedGetQueryString_RefusesBeforeExecution()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/graphql?query=" + new string('x', 1024));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("request-byte-limit", await response.Content.ReadAsStringAsync());
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    [Fact]
    public async Task Query_StalledBody_TimesOutAndReleasesConcurrencySlot()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentEval:Queries:Enabled"] = "true",
            ["AgentEval:Queries:HttpLimits:RequestSeconds"] = "1",
            ["AgentEval:Queries:HttpLimits:Concurrency"] = "1"
        });
        EvidenceQueryBudget.Configure(builder);
        await using var app = builder.Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Stall")) context.Request.Body = new StalledInput(entered);
            await next(context);
        });
        EvidenceQueryBudget.Use(app);
        app.MapPost("/graphql", () => "completed");
        await app.StartAsync(); using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql"); request.Headers.Add("X-Test-Stall", "true");
        var first = client.SendAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var busy = await client.PostAsync("/graphql", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Contains("query-concurrency-limit", await busy.Content.ReadAsStringAsync());
        using var timedOut = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.RequestTimeout, timedOut.StatusCode);
        using var recovered = await client.PostAsync("/graphql", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("completed", await recovered.Content.ReadAsStringAsync());
    }

    private sealed class StalledInput(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
#endif
