// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Results.Adapters.Tests.Reading;

public sealed class SemanticReadBudgetTests
{
    [Theory]
    [InlineData("assertion")]
    [InlineData("metric-key")]
    [InlineData("trace")]
    [InlineData("subject")]
    [InlineData("escaped")]
    public async Task Read_OversizedNestedToken_RejectsBeforeAllocatingTokenBuffer(string location)
    {
        var reader = new AefWorkspaceEvidenceReader();
        // Warm reflection metadata and the failure path before measuring reader allocations.
        var character = location == "escaped" ? '\u0001' : 'x';
        var warm = await Store(location, new string(character, 16_384));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(warm, 8192));
        var store = await Store(location, new string(character, 1 << 20));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var reading = reader.ReadAsync(store, 8192);
        // InMemoryOutputStore completes synchronously; measure before awaiting the fault.
        Assert.True(reading.IsCompleted);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.ThrowsAsync<InvalidDataException>(() => reading);
        // Includes per-read serializer metadata for the fixed DTO graph (about
        // 650 KiB), but must not include multi-megabyte token scratch buffers.
        Assert.True(allocated < 1 << 20, $"Expected bounded reader allocation below 1 MiB; actual {allocated} bytes.");
    }

    [Fact]
    public async Task Convert_SealExceedsRemainingBudget_DiscardsPartialOutput()
    {
        var store = await Store("trace", "small trace");
        var id = (await Manifests(store)).Single().Run.RunId;
        var root = Path.Combine(Path.GetTempPath(), "ae-semantic-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var budget = new AefReadBudget(1 << 20);
            var control = await StoreV1Exporter.ExportSemanticBoundedAsync(store, id, Path.Combine(root, "control"), default, budget);
            var used = (1 << 20) - budget.Remaining;
            var sealBytes = new FileInfo(Path.Combine(control.Directory, "seal.json")).Length;
            var target = Path.Combine(root, "refused");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => StoreV1Exporter.ExportSemanticBoundedAsync(
                store, id, target, default, new(used - sealBytes / 2)));
            Assert.Contains("AefSealer.Seal", error.StackTrace);
            Assert.False(Directory.Exists(target));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Read_SharedBudgetIncludesBothRuns_AndDoesNotChargeVerificationTwice()
    {
        var store = await Store("trace", "small trace");
        var first = (await Manifests(store)).Single();
        var root = Path.Combine(Path.GetTempPath(), "ae-semantic-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var budget = new AefReadBudget(1 << 20);
            await StoreV1Exporter.ExportSemanticBoundedAsync(store, first.Run.RunId, root, default, budget);
            var used = (1 << 20) - budget.Remaining;
            var discoveryBytes = 0L;
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            };
            await foreach (var subject in store.ListSubjectsAsync())
                discoveryBytes += JsonSerializer.SerializeToUtf8Bytes(subject, options).Length;
            discoveryBytes += JsonSerializer.SerializeToUtf8Bytes(first, options).Length;
            var limit = used + discoveryBytes + 64; // Allow timestamp text precision differences.
            var reader = new AefWorkspaceEvidenceReader();
            var single = Assert.Single((await reader.ReadAsync(store, limit)).Runs);
            Assert.True(single.Evidence.Bytes > 64); // Charging the verified snapshot again would fail.

            var second = await store.StartRunAsync(new(SubjectKind.Agent, "Second"), new("test", ".", "offline", null, null, "eval"));
            await store.WriteScenarioResultAsync(second.Run.RunId, new("two", "two", "input", "output", true, 1,
                new Dictionary<string, double>(), [], TimeSpan.Zero, 0));
            Assert.Equal(2, (await reader.ReadAsync(store)).Runs.Count);
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(store, limit));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<List<RunManifest>> Manifests(InMemoryOutputStore store)
    {
        var runs = new List<RunManifest>();
        await foreach (var subject in store.ListSubjectsAsync())
            await foreach (var manifest in store.ListRunsAsync(subject.Identity)) runs.Add(manifest);
        return runs;
    }

    private static async Task<InMemoryOutputStore> Store(string location, string text)
    {
        var store = new InMemoryOutputStore();
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, location == "subject" ? text : "Bounded"),
            new("test", ".", "offline", null, null, "eval"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId, new("case", "case", "input", "output", true, 1,
            new Dictionary<string, double> { [location == "metric-key" ? text : "score"] = 1 },
            [AssertionResult.Fail("check", location is "assertion" or "escaped" ? text : "failed")], TimeSpan.Zero, 0));
        if (location == "trace")
            await store.AppendTraceAsync(manifest.Run.RunId, new(manifest.Run.RunId, "case",
                [new(DateTimeOffset.UtcNow, "message", "agent", text)]));
        return store;
    }
}
