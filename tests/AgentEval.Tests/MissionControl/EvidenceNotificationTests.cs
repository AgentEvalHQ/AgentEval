// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net;
using System.Text.Json;
using AgentEval.Indexing;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using AgentEval.MissionControl.Core;

namespace AgentEval.Tests.MissionControl;

public sealed class EvidenceNotificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-sse-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.sqlite");
    public EvidenceNotificationTests() => Directory.CreateDirectory(_root);
    private WebApplicationFactory<Query> Factory(bool enabled = true, int lifetime = 30) =>
        new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentEval:Root", _root);
            builder.UseSetting("AgentEval:Queries:Enabled", "true");
            builder.UseSetting("AgentEval:Queries:IndexPath", Database);
            builder.UseSetting("AgentEval:Queries:Notifications:Enabled", enabled.ToString());
            builder.UseSetting("AgentEval:Queries:Notifications:PollSeconds", "1");
            builder.UseSetting("AgentEval:Queries:Notifications:ReconcileEvery", "1");
            builder.UseSetting("AgentEval:Queries:Notifications:MaxSubscribers", "1");
            builder.UseSetting("AgentEval:Queries:Notifications:LifetimeSeconds", lifetime.ToString());
        });

    private static async Task<(string Id, string Event, string Data)> Frame(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string id = "", name = "", data = "";
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length == 0) return (id, name, data);
            if (line.StartsWith("id: ")) id = line[4..];
            if (line.StartsWith("event: ")) name = line[7..];
            if (line.StartsWith("data: ")) data = line[6..];
        }
        throw new InvalidOperationException("Stream ended before a complete event.");
    }

    [Fact]
    public async Task Events_Disabled_ReturnsUnavailableWithoutCreatingStorage()
    {
        using var factory = Factory(false); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/evidence/events");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task Events_ActualIndexMutationAndPeriodicReconcile_EmitBoundedInvalidations()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        using var factory = Factory(); using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/evidence/events");
        request.Headers.Add("Last-Event-ID", "old-process:9007199254740993");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var initial = await Frame(reader);
        Assert.Equal("resync", initial.Event);
        using var initialData = JsonDocument.Parse(initial.Data);
        Assert.Equal("connected", initialData.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.String, initialData.RootElement.GetProperty("sequence").ValueKind);
        var reconcile = await Frame(reader); Assert.Equal("resync", reconcile.Event);
        Assert.Contains("reconcile", reconcile.Data);
        writer.Upsert(new("root", "run", new string('a', 64), IndexedEvidenceKind.Legacy, IndexedVerification.Unverified,
            new("run", null, null, null, null, null, null, "unknown"), [], [], null));
        var changed = await Frame(reader);
        // A timer tick can have queued a reconciliation before the mutation committed.
        for (var i = 0; changed.Event != "changed" && i < 5; i++) changed = await Frame(reader);
        Assert.Equal("changed", changed.Event); Assert.NotEqual(initial.Id, changed.Id);
        Assert.DoesNotContain(_root, changed.Data);
        using var rejected = await client.GetAsync("/api/v1/evidence/events");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    [Fact]
    public async Task Events_ConnectionLifetime_ReleasesSubscriberSlotForReconnect()
    {
        using var factory = Factory(lifetime: 1); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/evidence/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("resync", (await Frame(reader)).Event);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { while (await reader.ReadLineAsync(timeout.Token) is not null) { } }
        catch (IOException) { }
        catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
        using var reconnect = await client.GetAsync("/api/v1/evidence/events", HttpCompletionOption.ResponseHeadersRead);
        reconnect.EnsureSuccessStatusCode();
        using var next = new StreamReader(await reconnect.Content.ReadAsStreamAsync());
        var frame = await Frame(next); Assert.Equal("resync", frame.Event); Assert.Contains("connected", frame.Data);
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task Events_HostShutdown_ClosesSharedFeedAndActiveStream()
    {
        await using var factory = Factory(); using var client = factory.CreateClient();
        var feed = factory.Services.GetRequiredService<IEvidenceChangeFeed>();
        using var response = await client.GetAsync("/api/v1/evidence/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("resync", (await Frame(reader)).Event);
        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<ObjectDisposedException>(() => feed.Subscribe());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { while (await reader.ReadLineAsync(timeout.Token) is not null) { } }
        catch (IOException) { }
        catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
#endif
