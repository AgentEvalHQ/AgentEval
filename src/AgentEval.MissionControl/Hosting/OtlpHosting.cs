// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Observations;
using Path = System.IO.Path;

namespace AgentEval.MissionControl.Hosting;

internal sealed record OtlpHttpOptions(byte[] KeyHash, OtlpIngestionLimits Limits, OtlpObservationStoreOptions Store);

internal static class OtlpHosting
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var section = configuration.GetSection("AgentEval:Otlp");
        if (!section.GetValue<bool>("Enabled")) return;
        var keyHash = IngestionAccessPolicy.HashKey(section["ApiKey"]);
        var root = section["Root"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw Invalid("an absolute dedicated OTLP root");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var configuredViewer = configuration["AgentEval:Root"];
        var viewer = Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(configuredViewer)
            ? McHost.DiscoverWorkspaceRoot(Directory.GetCurrentDirectory()) : configuredViewer, ".agenteval"));
        Disjoint(root, viewer);
        var ingestion = configuration["AgentEval:Ingestion:Root"];
        if (!string.IsNullOrWhiteSpace(ingestion)) Disjoint(root, Path.GetFullPath(ingestion));
        var limits = new OtlpIngestionLimits
        {
            MaxRequestBytes = section.GetValue("MaxRequestBytes", 4 << 20),
            MaxDecodedBytes = section.GetValue("MaxDecodedBytes", 4 << 20),
            MaxSpans = section.GetValue("MaxSpans", 4096), MaxAttributes = section.GetValue("MaxAttributes", 128),
            MaxItems = section.GetValue("MaxItems", 100_000), MaxDepth = section.GetValue("MaxDepth", 32),
            MaxValueBytes = section.GetValue("MaxValueBytes", 16 << 10),
            MaxConcurrentRequests = section.GetValue("MaxConcurrentRequests", 2), TimeoutSeconds = section.GetValue("TimeoutSeconds", 30)
        };
        limits.Validate();
        var capture = section["Capture"] ?? "Off";
        if (capture is not ("Off" or "On")) throw Invalid("Capture equal to Off or On");
        var source = section["Source"];
        if (string.IsNullOrWhiteSpace(source)) throw Invalid("an explicit stable Source namespace");
        var options = new OtlpHttpOptions(keyHash, limits, new()
        {
            Root = root, Source = source, Capture = capture == "On" ? OtlpContentCapture.On : OtlpContentCapture.Off,
            MaxRecords = section.GetValue("MaxRecords", 4096), MaxRecordBytes = section.GetValue("MaxRecordBytes", 16 << 20),
            MaxStoredBytes = section.GetValue("MaxStoredBytes", 512L << 20)
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IOtlpPayloadReader>(_ => new OtlpPayloadReader(limits));
        builder.Services.AddSingleton<IOtlpTraceNormalizer>(_ => new OtlpTraceNormalizer(limits));
        builder.Services.AddSingleton<IOtlpContentPolicy, OtlpContentPolicy>();
        builder.Services.AddSingleton<IOtlpSessionGrouper, OtlpSessionGrouper>();
        builder.Services.AddSingleton<IOtlpObservationStore>(sp => new OtlpObservationStore(options.Store,
            sp.GetRequiredService<IOtlpContentPolicy>(), sp.GetRequiredService<IOtlpSessionGrouper>()));
        builder.Services.AddSingleton(sp => new OtlpReceiver(options, sp.GetRequiredService<IOtlpPayloadReader>(),
            new OtlpJsonTraceDecoder(limits), new OtlpProtobufTraceDecoder(limits), sp.GetRequiredService<IOtlpObservationStore>()));
        builder.Services.AddHostedService<OtlpLifetime>();
    }

    private static void Disjoint(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        first = Path.TrimEndingDirectorySeparator(first); second = Path.TrimEndingDirectorySeparator(second);
        if (first.Equals(second, comparison) || first.StartsWith(second + Path.DirectorySeparatorChar, comparison)
            || second.StartsWith(first + Path.DirectorySeparatorChar, comparison)) throw Invalid("disjoint OTLP, viewer and AEF ingestion roots");
    }
    private static InvalidOperationException Invalid(string expected) => new(
        $"Expected: {expected}. Actual: invalid OTLP configuration. Suggestions: configure dedicated observation storage. Because: telemetry must remain separate from evaluation evidence.");
    private sealed class OtlpLifetime(OtlpReceiver receiver) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); _ = receiver; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal sealed class OtlpReceiver(OtlpHttpOptions options, IOtlpPayloadReader reader,
    IOtlpTraceDecoder json, IOtlpTraceDecoder protobuf, IOtlpObservationStore store) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Limits.MaxConcurrentRequests);
    internal async Task AcceptAsync(Stream body, bool isJson, OtlpCompression compression, CancellationToken token)
    {
        if (!await _slots.WaitAsync(0, token)) throw new OtlpIngestionException("busy", "receiver concurrency limit reached");
        try
        {
            var bytes = await reader.ReadAsync(body, compression, token);
            // Keep the slot until synchronous decoding and durable acceptance actually finish.
            await Task.Run(() => store.Accept((isJson ? json : protobuf).Decode(bytes, token), token), token);
        }
        finally { _slots.Release(); }
    }
    public void Dispose() => _slots.Dispose();
}
