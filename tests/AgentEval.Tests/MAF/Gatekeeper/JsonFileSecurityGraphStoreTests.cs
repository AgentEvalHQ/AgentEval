// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class JsonFileSecurityGraphStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key =
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] DifferentKey =
        Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
    private static readonly SecurityGraphNode Agent =
        new(SecurityGraphNodeKind.Agent, "agent-a");
    private static readonly SecurityGraphNode Tool =
        new(SecurityGraphNodeKind.Tool, "tool-a");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "agenteval-security-graph-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_directory, "graph.json");

    [Fact]
    public void Constructor_MissingStoreRequiresExplicitBootstrap()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key));

        Assert.Contains(
            "store_missing",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Constructor_BootstrapBindsTenantKeyIdAndRetention()
    {
        using (var store = CreateStore())
        {
            Assert.Equal(
                SecurityGraphCoverageState.Complete,
                store.Read(TimeSpan.FromHours(1)).Coverage);
        }

        using var document = JsonDocument.Parse(
            File.ReadAllBytes(StorePath));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(
            "tenant-a",
            document.RootElement.GetProperty("tenant").GetString());
        Assert.Equal(
            "key-a",
            document.RootElement.GetProperty("sessionKeyId").GetString());
        Assert.Equal(
            43,
            document.RootElement.GetProperty("sessionKeyVerifier")
                .GetString()!
                .Length);
        Assert.Equal(
            2 * 60 * 60,
            document.RootElement.GetProperty("retentionSeconds").GetInt64());

        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-b",
                "key-a",
                Key,
                Options(bootstrap: false)));
        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-b",
                Key,
                Options(bootstrap: false)));
        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                DifferentKey,
                Options(bootstrap: false)));
        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key,
                new JsonFileSecurityGraphStoreOptions
                {
                    Retention = TimeSpan.FromHours(3),
                }));
    }

    [Fact]
    public void Constructor_SecondOwnerFailsAndDisposeReleasesOwnership()
    {
        using var first = CreateStore();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key,
                Options(bootstrap: false),
                new TestClock(Now)));
        Assert.Contains(
            "ownership_unavailable",
            exception.Message,
            StringComparison.Ordinal);

        first.Dispose();
        using var replacement = new JsonFileSecurityGraphStore(
            StorePath,
            "tenant-a",
            "key-a",
            Key,
            Options(bootstrap: false),
            new TestClock(Now));
        Assert.Equal(
            SecurityGraphCoverageState.Complete,
            replacement.Read(TimeSpan.FromHours(1)).Coverage);
    }

    [Theory]
    [InlineData("""{"version":2,"tenant":"tenant-a","sessionKeyId":"key-a","retentionSeconds":7200,"observations":[],"coverageGaps":[]}""")]
    [InlineData("""{"version":1,"version":1,"tenant":"tenant-a","sessionKeyId":"key-a","retentionSeconds":7200,"observations":[],"coverageGaps":[]}""")]
    [InlineData("""{"version":1,"tenant":"tenant-a","sessionKeyId":"key-a","retentionSeconds":7200,"observations":[],"coverageGaps":[],"unknown":true}""")]
    [InlineData("""{"version":1,"tenant":"tenant-a","sessionKeyId":"key-a","retentionSeconds":7200,"observations":{},"coverageGaps":[]}""")]
    [InlineData("""{"version":1,"tenant":"tenant-a","sessionKeyId":"key-a","retentionSeconds":7200,"observations":[],"coverageGaps":[],}""")]
    public void Constructor_InvalidDuplicateOrUnknownJsonFailsClosed(
        string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, json);

        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key,
                Options(bootstrap: false),
                new TestClock(Now)));
    }

    [Fact]
    public void Constructor_OverBoundFileFailsClosed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(
            StorePath,
            Enumerable.Repeat((byte)' ', 1025).ToArray());

        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key,
                new JsonFileSecurityGraphStoreOptions
                {
                    MaxFileBytes = 1024,
                    Retention = TimeSpan.FromHours(2),
                },
                new TestClock(Now)));
    }

    [Fact]
    public async Task AppendAsync_HashesSessionAndPersistsNoRawIdentifier()
    {
        const string secretSession = "raw-session-secret";
        string firstDigest;
        using (var store = CreateStore())
        {
            Assert.Equal(
                SecurityGraphMutationDisposition.Applied,
                (await store.AppendAsync(
                    Request("event-1", secretSession))).Disposition);
            await store.AppendAsync(
                Request("event-2", secretSession));
            await store.AppendAsync(
                Request("event-3", "different-session"));

            var snapshot = store.Read(TimeSpan.FromHours(1));
            firstDigest = snapshot.Observations[0].SessionDigest;
            Assert.Equal(
                firstDigest,
                snapshot.Observations[1].SessionDigest);
            Assert.NotEqual(
                firstDigest,
                snapshot.Observations[2].SessionDigest);
            Assert.DoesNotContain(
                snapshot.Observations,
                observation =>
                    observation.SessionDigest == secretSession);
        }

        var durableJson = File.ReadAllText(StorePath);
        Assert.DoesNotContain(
            secretSession,
            durableJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "different-session",
            durableJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(Key),
            durableJson,
            StringComparison.OrdinalIgnoreCase);

        using var reopened = new JsonFileSecurityGraphStore(
            StorePath,
            "tenant-a",
            "key-a",
            Key,
            Options(bootstrap: false),
            new TestClock(Now));
        Assert.Equal(
            firstDigest,
            reopened.Read(TimeSpan.FromHours(1))
                .Observations[0]
                .SessionDigest);
    }

    [Fact]
    public async Task AppendAsync_IsIdempotentAndConflictingEventIdIsRejected()
    {
        using var store = CreateStore();
        var request = Request("event-1", "session-a");

        var applied = await store.AppendAsync(request);
        var replay = await store.AppendAsync(request);
        var conflict = await store.AppendAsync(
            Request(
                "event-1",
                "session-a",
                evidenceReference: "evidence:other"));

        Assert.Equal(
            SecurityGraphMutationDisposition.Applied,
            applied.Disposition);
        Assert.Equal(
            SecurityGraphMutationDisposition.Unchanged,
            replay.Disposition);
        Assert.Equal(
            SecurityGraphMutationDisposition.Conflict,
            conflict.Disposition);
        Assert.Single(
            store.Read(TimeSpan.FromHours(1)).Observations);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentRetriesConvergeAndSurviveRestart()
    {
        using (var store = CreateStore())
        {
            var request = Request("event-1", "session-a");
            var results = await Task.WhenAll(
                Enumerable.Range(0, 24)
                    .Select(_ => store.AppendAsync(request).AsTask()));

            Assert.Equal(
                1,
                results.Count(result =>
                    result.Disposition ==
                    SecurityGraphMutationDisposition.Applied));
            Assert.Equal(
                23,
                results.Count(result =>
                    result.Disposition ==
                    SecurityGraphMutationDisposition.Unchanged));
        }

        using var reopened = new JsonFileSecurityGraphStore(
            StorePath,
            "tenant-a",
            "key-a",
            Key,
            Options(bootstrap: false),
            new TestClock(Now));
        Assert.Single(
            reopened.Read(TimeSpan.FromHours(1)).Observations);
    }

    [Fact]
    public async Task CapacityRejectionDurablyMarksCoverageIncomplete()
    {
        using var store = CreateStore(
            options: new JsonFileSecurityGraphStoreOptions
            {
                BootstrapIfMissing = true,
                Retention = TimeSpan.FromHours(2),
                MaxObservations = 1,
            });
        await store.AppendAsync(Request("event-1", "session-a"));

        var rejected = await store.AppendAsync(
            Request("event-2", "session-b"));
        var snapshot = store.Read(TimeSpan.FromHours(1));

        Assert.Equal(
            SecurityGraphMutationDisposition.RejectedWithGap,
            rejected.Disposition);
        Assert.Equal(
            SecurityGraphCoverageState.Incomplete,
            snapshot.Coverage);
        Assert.Single(snapshot.Observations);
        var gap = Assert.Single(snapshot.CoverageGaps);
        Assert.Equal("capacity_exceeded", gap.ReasonCode);
    }

    [Fact]
    public async Task CoverageGapsCoalescePerMinuteAndOnlyAffectIntersectingWindow()
    {
        var clock = new TestClock(Now);
        using var store = CreateStore(clock: clock);

        await store.MarkCoverageGapAsync(
            new SecurityGraphCoverageGap("queue_full", count: 2));
        clock.Advance(TimeSpan.FromSeconds(10));
        await store.MarkCoverageGapAsync(
            new SecurityGraphCoverageGap("queue_full", count: 3));

        var current = store.Read(TimeSpan.FromMinutes(30));
        Assert.Equal(
            SecurityGraphCoverageState.Incomplete,
            current.Coverage);
        Assert.Equal(5, Assert.Single(current.CoverageGaps).Count);

        clock.Advance(TimeSpan.FromHours(1));
        var later = store.Read(TimeSpan.FromMinutes(30));
        Assert.Equal(
            SecurityGraphCoverageState.Complete,
            later.Coverage);
        Assert.Empty(later.CoverageGaps);
    }

    [Fact]
    public async Task RetentionPrunesExpiredEntriesOnMutationAndRestart()
    {
        var clock = new TestClock(Now);
        var options = new JsonFileSecurityGraphStoreOptions
        {
            BootstrapIfMissing = true,
            Retention = TimeSpan.FromHours(1),
        };
        using (var store = CreateStore(clock, options))
        {
            await store.AppendAsync(Request("old-event", "session-a"));
            await store.MarkCoverageGapAsync(
                new SecurityGraphCoverageGap("old_gap"));
            clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)));
            await store.AppendAsync(Request("new-event", "session-b"));

            var snapshot = store.Read(TimeSpan.FromHours(1));
            Assert.Equal(
                "new-event",
                Assert.Single(snapshot.Observations).EventId);
            Assert.Empty(snapshot.CoverageGaps);
        }

        using var reopened = new JsonFileSecurityGraphStore(
            StorePath,
            "tenant-a",
            "key-a",
            Key,
            new JsonFileSecurityGraphStoreOptions
            {
                Retention = TimeSpan.FromHours(1),
            },
            clock);
        Assert.Equal(
            "new-event",
            Assert.Single(
                reopened.Read(TimeSpan.FromHours(1)).Observations).EventId);
    }

    [Fact]
    public async Task Read_IsMemoryOnlyAndClockRollbackFailsClosed()
    {
        var clock = new TestClock(Now);
        using var store = CreateStore(clock: clock);
        await store.AppendAsync(Request("event-1", "session-a"));
        File.Delete(StorePath);

        Assert.Single(
            store.Read(TimeSpan.FromHours(1)).Observations);

        clock.Advance(TimeSpan.FromSeconds(-1));
        var rollback = store.Read(TimeSpan.FromHours(1));
        Assert.Equal(
            SecurityGraphCoverageState.Indeterminate,
            rollback.Coverage);
        Assert.Equal("clock_rollback", rollback.FailureCode);
        Assert.Equal(
            SecurityGraphMutationDisposition.Indeterminate,
            (await store.AppendAsync(
                Request("event-2", "session-b"))).Disposition);
    }

    [Fact]
    public async Task AppendAsync_PreCanceledRequestDoesNotPoisonStore()
    {
        using var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => store.AppendAsync(
                Request("event-1", "session-a"),
                cancellation.Token).AsTask());

        var snapshot = store.Read(TimeSpan.FromHours(1));
        Assert.Equal(
            SecurityGraphCoverageState.Complete,
            snapshot.Coverage);
        Assert.Empty(snapshot.Observations);
    }

    [Fact]
    public void Dispose_MakesReadsIndeterminateAndIsIdempotent()
    {
        var store = CreateStore();
        store.Dispose();
        store.Dispose();

        var snapshot = store.Read(TimeSpan.FromHours(1));

        Assert.Equal(
            SecurityGraphCoverageState.Indeterminate,
            snapshot.Coverage);
        Assert.Equal("store_disposed", snapshot.FailureCode);
    }

    [Fact]
    public void OptionsAndReadWindow_AreStrictlyBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                new byte[31],
                Options()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key,
                new JsonFileSecurityGraphStoreOptions
                {
                    BootstrapIfMissing = true,
                    Retention = TimeSpan.FromMinutes(59),
                }));

        using var store = CreateStore();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Read(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Read(TimeSpan.FromHours(3)));
    }

    [Fact]
    public async Task Constructor_DuplicateEventsAndUnknownNestedFieldsFailClosed()
    {
        using (var store = CreateStore())
        {
            await store.AppendAsync(Request("event-1", "session-a"));
        }

        var validJson = File.ReadAllText(StorePath);
        using var document = JsonDocument.Parse(validJson);
        var observation = document.RootElement
            .GetProperty("observations")[0]
            .GetRawText();
        var single = "\"observations\":[" + observation + "]";
        var duplicate = "\"observations\":[" + observation + "," + observation + "]";
        Assert.Contains(single, validJson, StringComparison.Ordinal);
        File.WriteAllText(StorePath, validJson.Replace(
            single,
            duplicate,
            StringComparison.Ordinal));

        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key,
                Options(bootstrap: false),
                new TestClock(Now)));

        var nestedUnknown = validJson.Replace(
            "\"id\":\"agent-a\"",
            "\"id\":\"agent-a\",\"unknown\":true",
            StringComparison.Ordinal);
        Assert.NotEqual(validJson, nestedUnknown);
        File.WriteAllText(StorePath, nestedUnknown);
        Assert.Throws<InvalidOperationException>(
            () => new JsonFileSecurityGraphStore(
                StorePath,
                "tenant-a",
                "key-a",
                Key,
                Options(bootstrap: false),
                new TestClock(Now)));
    }

    [Fact]
    public async Task GapCapacityFailureMakesStoreIndeterminate()
    {
        var clock = new TestClock(Now);
        using var store = CreateStore(
            clock,
            new JsonFileSecurityGraphStoreOptions
            {
                BootstrapIfMissing = true,
                Retention = TimeSpan.FromHours(2),
                MaxCoverageGaps = 1,
            });
        await store.MarkCoverageGapAsync(
            new SecurityGraphCoverageGap("queue_full"));

        var result = await store.MarkCoverageGapAsync(
            new SecurityGraphCoverageGap("producer_invalid"));

        Assert.Equal(
            SecurityGraphMutationDisposition.Indeterminate,
            result.Disposition);
        Assert.Equal(
            SecurityGraphCoverageState.Indeterminate,
            store.Read(TimeSpan.FromHours(1)).Coverage);
    }

    [Fact]
    public async Task TryReloadAsync_RequiresCompleteValidFileBeforeRecovery()
    {
        using var store = CreateStore();
        var valid = File.ReadAllBytes(StorePath);
        File.WriteAllText(StorePath, "{\"version\":1");

        Assert.False(await store.TryReloadAsync());
        Assert.Equal(
            SecurityGraphCoverageState.Indeterminate,
            store.Read(TimeSpan.FromHours(1)).Coverage);

        File.WriteAllBytes(StorePath, valid);
        Assert.True(await store.TryReloadAsync());
        Assert.Equal(
            SecurityGraphCoverageState.Complete,
            store.Read(TimeSpan.FromHours(1)).Coverage);
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonFileSecurityGraphStore CreateStore(
        TestClock? clock = null,
        JsonFileSecurityGraphStoreOptions? options = null)
        => new(
            StorePath,
            "tenant-a",
            "key-a",
            Key,
            options ?? Options(),
            clock ?? new TestClock(Now));

    private static JsonFileSecurityGraphStoreOptions Options(
        bool bootstrap = true)
        => new()
        {
            BootstrapIfMissing = bootstrap,
            Retention = TimeSpan.FromHours(2),
        };

    private static SecurityGraphObservationRequest Request(
        string eventId,
        string sessionIdentifier,
        string? evidenceReference = "evidence:incident-1")
        => new(
            eventId,
            Agent,
            Tool,
            SecurityGraphSignalKind.CallBlocked,
            sessionIdentifier,
            evidenceReference);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
