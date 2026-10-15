// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using AgentEval.MissionControl.Core;

namespace AgentEval.MissionControl.Hosting;

internal sealed record EvidenceNotificationOptions(int PollSeconds, int ReconcileEvery, int LifetimeSeconds, int WriteSeconds);

internal static class EvidenceNotificationHosting
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("AgentEval:Queries:Notifications");
        if (!builder.Configuration.GetValue<bool>("AgentEval:Queries:Enabled") || !section.GetValue<bool>("Enabled")) return;
        int Limit(string key, int fallback, int maximum)
        {
            var value = section.GetValue(key, fallback);
            if (value is < 1 || value > maximum)
                throw new InvalidOperationException($"Expected: {key} between 1 and {maximum}. Actual: invalid notification limit. Suggestions: configure a finite value in range. Because: streaming resources must remain bounded.");
            return value;
        }
        var subscribers = Limit("MaxSubscribers", 32, 256);
        var capacity = Limit("BufferCapacity", 16, 1024);
        builder.Services.AddSingleton(new EvidenceNotificationOptions(Limit("PollSeconds", 5, 300),
            Limit("ReconcileEvery", 6, 60), Limit("LifetimeSeconds", 600, 3600), Limit("WriteSeconds", 10, 60)));
        builder.Services.AddSingleton(_ => new EvidenceChangeFeed(subscribers, capacity));
        builder.Services.AddSingleton<IEvidenceChangeFeed>(sp => sp.GetRequiredService<EvidenceChangeFeed>());
        builder.Services.AddSingleton<IEvidenceChangePublisher>(sp => sp.GetRequiredService<EvidenceChangeFeed>());
        builder.Services.AddHostedService<EvidenceNotificationMonitor>();
    }

    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/evidence/events", async (HttpContext context) =>
        {
            var feed = context.RequestServices.GetService<IEvidenceChangeFeed>();
            if (feed is null) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            IEvidenceChangeSubscription? candidate;
            try { candidate = feed.Subscribe(); }
            catch (ObjectDisposedException) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            using var subscription = candidate;
            if (subscription is null)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "5";
                return;
            }
            var options = context.RequestServices.GetRequiredService<EvidenceNotificationOptions>();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            lifetime.CancelAfter(TimeSpan.FromSeconds(options.LifetimeSeconds));
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            try
            {
                await foreach (var change in subscription.ReadAllAsync(lifetime.Token))
                {
                    var sequence = change.Sequence.ToString(CultureInfo.InvariantCulture);
                    var name = change.Reason == EvidenceChangeReason.Changed ? "changed" : "resync";
                    var reason = change.Reason.ToString().ToLowerInvariant();
                    var body = JsonSerializer.Serialize(new { epoch = change.Epoch, sequence, reason });
                    using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    write.CancelAfter(TimeSpan.FromSeconds(options.WriteSeconds));
                    await context.Response.WriteAsync($"id: {change.Epoch}:{sequence}\nevent: {name}\ndata: {body}\n\n", write.Token);
                    await context.Response.Body.FlushAsync(write.Token);
                }
            }
            catch (OperationCanceledException) { context.Abort(); }
            catch (IOException) { context.Abort(); }
        });
    }
}

internal sealed class EvidenceNotificationMonitor(IEvidenceQueryService queries, EvidenceChangeFeed feed,
    EvidenceNotificationOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        (QueryState State, string? Code, string? Instance, long? Generation)? previous = null;
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // One bounded, validated read; never enumerate sources or verify every evidence directory.
                var result = queries.Sources(new(PageSize: 1), stoppingToken);
                var revision = (result.State, result.Code, result.Data?.InstanceId, result.Data?.Generation);
                if (previous is not null && previous != revision) feed.Invalidate();
                previous = revision;
                // Overlay changes need not change the derived index. Periodic resync covers these and missed transitions.
                if (++ticks == options.ReconcileEvery) { feed.Invalidate(EvidenceChangeReason.Reconcile); ticks = 0; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { feed.Dispose(); }
    }
}
