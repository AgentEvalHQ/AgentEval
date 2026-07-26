// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, Task 3.2 — durable single-process JSON containment store and signed release.</summary>
public sealed class JsonFileContainmentStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "agenteval-containment-" + Guid.NewGuid().ToString("N"));

    private string StorePath => System.IO.Path.Combine(_directory, "containment.json");

    [Fact]
    public void Constructor_MissingStoreRequiresExplicitBootstrap()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new JsonFileContainmentStore(StorePath, new TestVerifier()));

        Assert.Contains("store_missing", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Constructor_BootstrapCreatesStrictVersionedStore()
    {
        using (var store = CreateStore())
        {
            Assert.Equal(
                ContainmentSnapshotState.NotContained,
                store.GetCurrent(Session()).State);
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(StorePath));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("records").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("liveReleaseNonces").GetArrayLength());

        using var reopened = new JsonFileContainmentStore(StorePath, new TestVerifier());
        Assert.Equal(ContainmentSnapshotState.NotContained, reopened.GetCurrent(Session()).State);
    }

    [Fact]
    public void Constructor_SecondLiveOwnerFailsAndDisposeReleasesOwnership()
    {
        using var first = CreateStore();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new JsonFileContainmentStore(StorePath, new TestVerifier()));
        Assert.Contains("ownership_unavailable", exception.Message, StringComparison.Ordinal);

        first.Dispose();
        using var replacement = new JsonFileContainmentStore(StorePath, new TestVerifier());
        Assert.Equal(ContainmentSnapshotState.NotContained, replacement.GetCurrent(Session()).State);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"records":[],"liveReleaseNonces":[]}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"records":[],"liveReleaseNonces":[]}""")]
    [InlineData("""{"schemaVersion":1,"records":[],"liveReleaseNonces":[],"unknown":true}""")]
    [InlineData("""{"schemaVersion":1,"records":{},"liveReleaseNonces":[]}""")]
    [InlineData("""{"schemaVersion":1,"records":[],"liveReleaseNonces":[],}""")]
    public void Constructor_InvalidOrNonCanonicalShapeFailsClosed(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, json);

        Assert.Throws<InvalidOperationException>(
            () => new JsonFileContainmentStore(StorePath, new TestVerifier()));
    }

    [Fact]
    public async Task Constructor_DuplicateTargetsFailWithoutPublishingPartialState()
    {
        using (var store = CreateStore())
        {
            await store.ContainAsync(Request(Session()));
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(StorePath));
        var record = document.RootElement.GetProperty("records")[0].GetRawText();
        File.WriteAllText(
            StorePath,
            $$"""{"schemaVersion":1,"records":[{{record}},{{record}}],"liveReleaseNonces":[]}""");

        Assert.Throws<InvalidOperationException>(
            () => new JsonFileContainmentStore(StorePath, new TestVerifier()));
    }

    [Fact]
    public void Constructor_OverBoundFileFails()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(StorePath, Enumerable.Repeat((byte)' ', 1025).ToArray());

        Assert.Throws<InvalidOperationException>(
            () => new JsonFileContainmentStore(
                StorePath,
                new TestVerifier(),
                new JsonFileContainmentStoreOptions { MaxFileBytes = 1024 }));
    }

    [Fact]
    public async Task GetCurrent_UsesPublishedMemoryAndIsolatesTenantKindAndIdentifier()
    {
        using var store = CreateStore();
        var target = Session("tenant-a", "session-a");
        await store.ContainAsync(Request(target));
        File.Delete(StorePath);

        Assert.Equal(ContainmentSnapshotState.Active, store.GetCurrent(target).State);
        Assert.Equal(
            ContainmentSnapshotState.NotContained,
            store.GetCurrent(Session("tenant-b", "session-a")).State);
        Assert.Equal(
            ContainmentSnapshotState.NotContained,
            store.GetCurrent(new ContainmentTarget.McpServer("tenant-a", "session-a")).State);
        Assert.Equal(
            ContainmentSnapshotState.NotContained,
            store.GetCurrent(Session("tenant-a", "session-b")).State);
    }

    [Fact]
    public async Task ContainAsync_ConcurrentCallsConvergeAndSurviveReopen()
    {
        var target = Session();
        using (var store = CreateStore())
        {
            var results = await Task.WhenAll(
                Enumerable.Range(0, 32)
                    .Select(_ => store.ContainAsync(Request(target)).AsTask()));

            Assert.Equal(1, results.Count(result => result.Disposition == ContainmentMutationDisposition.Applied));
            Assert.Equal(31, results.Count(result => result.Disposition == ContainmentMutationDisposition.Unchanged));
            Assert.All(results, result => Assert.Equal(ContainmentSnapshotState.Active, result.Snapshot.State));
            Assert.All(results, result => Assert.Equal(1, result.Snapshot.Record!.Version));
        }

        using var reopened = new JsonFileContainmentStore(StorePath, new TestVerifier());
        var snapshot = reopened.GetCurrent(target);
        Assert.Equal(ContainmentSnapshotState.Active, snapshot.State);
        Assert.Equal(1, snapshot.Record!.Version);
    }

    [Fact]
    public async Task ContainAsync_AfterReleaseUsesNextMonotonicVersion()
    {
        var clock = new TestClock(Now);
        using var store = CreateStore(clock: clock);
        var target = Session();
        await store.ContainAsync(Request(target));
        await store.ReleaseAsync(Authorization(target, expectedVersion: 1));

        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await store.ContainAsync(Request(target));

        Assert.Equal(ContainmentMutationDisposition.Applied, result.Disposition);
        Assert.Equal(ContainmentSnapshotState.Active, result.Snapshot.State);
        Assert.Equal(3, result.Snapshot.Record!.Version);
        Assert.Null(result.Snapshot.Record.ReleasedAtUtc);
        Assert.Null(result.Snapshot.Record.Reviewer);
    }

    [Fact]
    public async Task ActiveContainment_DoesNotSilentlyExpire()
    {
        var clock = new TestClock(Now);
        using var store = CreateStore(clock: clock);
        await store.ContainAsync(Request(Session()));

        clock.Advance(TimeSpan.FromDays(3650));

        Assert.Equal(ContainmentSnapshotState.Active, store.GetCurrent(Session()).State);
        Assert.True(store.GetCurrent(Session()).MustBlock);
    }

    [Fact]
    public void Canonicalizer_IsDeterministicDomainSeparatedAndExcludesSignature()
    {
        var first = Authorization(Session(), signature: "signature-value-0001");
        var second = Authorization(Session(), signature: "signature-value-0002");

        var firstPayload = ContainmentReleaseAuthorizationCanonicalizer.CreatePayload(first);
        var secondPayload = ContainmentReleaseAuthorizationCanonicalizer.CreatePayload(second);
        var unsignedPayload = ContainmentReleaseAuthorizationCanonicalizer.CreatePayload(
            first.Target,
            first.ExpectedVersion,
            first.OperatorId,
            first.IssuedAtUtc,
            first.ExpiresAtUtc,
            first.Nonce,
            first.Algorithm,
            first.AlgorithmVersion,
            first.KeyId);

        Assert.Equal(firstPayload, secondPayload);
        Assert.Equal(firstPayload, unsignedPayload);
        var text = Encoding.UTF8.GetString(firstPayload);
        Assert.Contains(ContainmentReleaseAuthorizationCanonicalizer.Domain, text, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Signature, text, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Signature, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseAsync_ValidAuthorityPersistsAuditedCompareAndSwapRelease()
    {
        var verifier = new TestVerifier();
        var clock = new TestClock(Now);
        var target = Session();
        using (var store = CreateStore(verifier, clock))
        {
            await store.ContainAsync(Request(target));
            var result = await store.ReleaseAsync(Authorization(target));

            Assert.Equal(ContainmentMutationDisposition.Applied, result.Disposition);
            Assert.Equal(ContainmentSnapshotState.Released, result.Snapshot.State);
            Assert.Equal(2, result.Snapshot.Record!.Version);
            Assert.Equal("operator-a", result.Snapshot.Record.Reviewer);
            Assert.Equal(Now, result.Snapshot.Record.ReleasedAtUtc);
            Assert.Equal(1, verifier.Calls);
        }

        using var reopened = new JsonFileContainmentStore(StorePath, verifier, timeProvider: clock);
        var persisted = reopened.GetCurrent(target);
        Assert.Equal(ContainmentSnapshotState.Released, persisted.State);
        Assert.Equal("operator-a", persisted.Record!.Reviewer);
    }

    [Fact]
    public async Task ReleaseAsync_RejectsStaleFutureExpiredWrongTargetAndInvalidSignature()
    {
        var verifier = new TestVerifier();
        var clock = new TestClock(Now);
        using var store = CreateStore(verifier, clock);
        var target = Session();
        await store.ContainAsync(Request(target));

        var cases = new[]
        {
            Authorization(target, expectedVersion: 2, nonce: "nonce-stale-00001"),
            Authorization(
                target,
                issuedAtUtc: Now.AddMinutes(1),
                expiresAtUtc: Now.AddMinutes(2),
                nonce: "nonce-future-0001"),
            Authorization(
                target,
                issuedAtUtc: Now.AddMinutes(-2),
                expiresAtUtc: Now,
                nonce: "nonce-expired-001"),
            Authorization(Session(identifier: "other"), nonce: "nonce-target-00001"),
            Authorization(target, nonce: "nonce-invalid-0001", signature: "invalid-signature-1"),
        };

        foreach (var authorization in cases)
        {
            var result = await store.ReleaseAsync(authorization);
            Assert.Equal(ContainmentMutationDisposition.Conflict, result.Disposition);
        }

        Assert.Equal(ContainmentSnapshotState.Active, store.GetCurrent(target).State);
        Assert.Equal(1, store.GetCurrent(target).Record!.Version);
    }

    [Fact]
    public async Task ReleaseAsync_VerifierExceptionFailsClosedAsConflict()
    {
        using var store = CreateStore(new TestVerifier(throwOnVerify: true));
        await store.ContainAsync(Request(Session()));

        var result = await store.ReleaseAsync(Authorization(Session()));

        Assert.Equal(ContainmentMutationDisposition.Conflict, result.Disposition);
        Assert.Equal(ContainmentSnapshotState.Active, store.GetCurrent(Session()).State);
    }

    [Fact]
    public async Task ReleaseAsync_ConsumedNonceSurvivesReopenAndBlocksReplay()
    {
        var target = Session();
        using (var first = CreateStore())
        {
            await first.ContainAsync(Request(target));
            await first.ReleaseAsync(Authorization(target, nonce: "durable-nonce-0001"));
        }

        using var reopened = new JsonFileContainmentStore(StorePath, new TestVerifier(), timeProvider: new TestClock(Now));
        var recontained = await reopened.ContainAsync(Request(target));
        Assert.Equal(3, recontained.Snapshot.Record!.Version);

        var replay = await reopened.ReleaseAsync(
            Authorization(target, expectedVersion: 3, nonce: "durable-nonce-0001"));

        Assert.Equal(ContainmentMutationDisposition.Conflict, replay.Disposition);
        Assert.Equal(ContainmentSnapshotState.Active, reopened.GetCurrent(target).State);
    }

    [Fact]
    public async Task FailedCommit_MarksAllReadsIndeterminateUntilExplicitSuccessfulReload()
    {
        using var store = CreateStore();
        var validEmptyStore = File.ReadAllBytes(StorePath);
        File.Delete(StorePath);
        Directory.CreateDirectory(StorePath);

        var failed = await store.ContainAsync(Request(Session()));

        Assert.Equal(ContainmentMutationDisposition.Indeterminate, failed.Disposition);
        Assert.Equal(ContainmentSnapshotState.Indeterminate, store.GetCurrent(Session()).State);
        Assert.Equal(
            ContainmentSnapshotState.Indeterminate,
            store.GetCurrent(Session(identifier: "other")).State);
        Assert.False(await store.TryReloadAsync());

        Directory.Delete(StorePath);
        File.WriteAllBytes(StorePath, validEmptyStore);

        Assert.True(await store.TryReloadAsync());
        Assert.Equal(ContainmentSnapshotState.NotContained, store.GetCurrent(Session()).State);
    }

    [Fact]
    public async Task RecordCapacityFailure_MarksStoreIndeterminate()
    {
        using var store = CreateStore(
            options: new JsonFileContainmentStoreOptions
            {
                BootstrapIfMissing = true,
                MaxRecords = 1,
            });
        await store.ContainAsync(Request(Session(identifier: "one")));

        var result = await store.ContainAsync(Request(Session(identifier: "two")));

        Assert.Equal(ContainmentMutationDisposition.Indeterminate, result.Disposition);
        Assert.True(store.GetCurrent(Session(identifier: "one")).MustBlock);
        Assert.Equal(
            ContainmentSnapshotState.Indeterminate,
            store.GetCurrent(Session(identifier: "one")).State);
    }

    [Fact]
    public async Task ContainAsync_PreCanceledMutationDoesNotPoisonHealthyState()
    {
        using var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => store.ContainAsync(Request(Session()), cancellation.Token).AsTask());

        Assert.Equal(ContainmentSnapshotState.NotContained, store.GetCurrent(Session()).State);
    }

    [Fact]
    public void Dispose_MakesReadsIndeterminateAndAllowsIdempotentDispose()
    {
        var store = CreateStore();
        store.Dispose();
        store.Dispose();

        var snapshot = store.GetCurrent(Session());

        Assert.Equal(ContainmentSnapshotState.Indeterminate, snapshot.State);
        Assert.True(snapshot.MustBlock);
        Assert.Equal("store_disposed", snapshot.FailureCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonFileContainmentStore CreateStore(
        TestVerifier? verifier = null,
        TestClock? clock = null,
        JsonFileContainmentStoreOptions? options = null)
        => new(
            StorePath,
            verifier ?? new TestVerifier(),
            options ?? new JsonFileContainmentStoreOptions { BootstrapIfMissing = true },
            clock ?? new TestClock(Now));

    private static ContainmentTarget Session(
        string tenant = "tenant-a",
        string identifier = "session-a")
        => new ContainmentTarget.Session(tenant, identifier);

    private static ContainmentRequest Request(ContainmentTarget target)
        => new(target, "block_storm", "evidence:incident-1", "gatekeeper");

    private static ContainmentReleaseAuthorization Authorization(
        ContainmentTarget target,
        long expectedVersion = 1,
        DateTimeOffset? issuedAtUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        string nonce = "release-nonce-0001",
        string signature = TestVerifier.ValidSignature)
        => new(
            target,
            expectedVersion,
            "operator-a",
            issuedAtUtc ?? Now.AddMinutes(-1),
            expiresAtUtc ?? Now.AddMinutes(5),
            nonce,
            "test",
            algorithmVersion: 1,
            "key-a",
            signature);

    private sealed class TestVerifier(bool throwOnVerify = false) : IContainmentReleaseAuthorizationVerifier
    {
        public const string ValidSignature = "valid-signature-0001";

        public int Calls { get; private set; }

        public bool Verify(
            ContainmentReleaseAuthorization authorization,
            ReadOnlyMemory<byte> canonicalPayload)
        {
            Calls++;
            if (throwOnVerify)
            {
                throw new InvalidOperationException("Simulated unavailable verifier.");
            }

            return authorization.Signature == ValidSignature && !canonicalPayload.IsEmpty;
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
