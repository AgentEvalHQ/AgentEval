// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, Task 3.1 — tenant-scoped containment model and store contract.</summary>
public class ContainmentContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TargetHierarchy_IsExternallyClosedAndVariantsAreSealed()
    {
        var constructor = typeof(ContainmentTarget)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();

        Assert.True(constructor.IsPrivate);
        Assert.True(typeof(ContainmentTarget.Session).IsSealed);
        Assert.True(typeof(ContainmentTarget.McpServer).IsSealed);
        Assert.True(typeof(ContainmentTarget.AgentEndpoint).IsSealed);
    }

    [Fact]
    public void TargetEquality_NormalizesUnicodeAndIsolatesTenantKindAndCase()
    {
        ContainmentTarget normalized = new ContainmentTarget.Session(" tenant-a ", "Cafe\u0301");
        ContainmentTarget canonical = new ContainmentTarget.Session("tenant-a", "Café");

        Assert.Equal(normalized, canonical);
        Assert.True(normalized == canonical);
        Assert.NotEqual(normalized, new ContainmentTarget.Session("tenant-b", "Café"));
        Assert.NotEqual(normalized, new ContainmentTarget.McpServer("tenant-a", "Café"));
        Assert.NotEqual(normalized, new ContainmentTarget.Session("tenant-a", "CAFÉ"));
        Assert.Equal("tenant-a", normalized.Tenant);
        Assert.Equal("Café", normalized.Identifier);
        Assert.DoesNotContain("tenant-a", normalized.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("value\nwith-line")]
    [InlineData("\nleading-control")]
    [InlineData("trailing-control\r")]
    [InlineData("bidi-\u202Eoverride")]
    [InlineData("zero-\u200Bwidth")]
    public void Target_InvalidIdentifiersFailWithoutEchoingValue(string invalid)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(
            () => new ContainmentTarget.Session("tenant", invalid));

        if (!string.IsNullOrWhiteSpace(invalid))
        {
            Assert.DoesNotContain(invalid, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Target_UnpairedSurrogateFails()
    {
        var invalid = new string('\uD800', 1);
        Assert.Throws<ArgumentException>(() => new ContainmentTarget.Session("tenant", invalid));
    }


    [Fact]
    public void Target_OversizedIdentifiersAndTenantsFail()
    {
        Assert.Throws<ArgumentException>(
            () => new ContainmentTarget.Session(new string('t', 129), "session"));
        Assert.Throws<ArgumentException>(
            () => new ContainmentTarget.Session("tenant", new string('s', 257)));
    }

    [Fact]
    public void Record_ActiveAndReleasedInvariantsProduceImmutableVersionedModels()
    {
        var active = ActiveRecord();
        var released = ReleasedRecord();

        Assert.Equal(1, active.SchemaVersion);
        Assert.Equal(ContainmentStatus.Active, active.Status);
        Assert.Null(active.ReleasedAtUtc);
        Assert.Null(active.Reviewer);
        Assert.Equal(ContainmentStatus.Released, released.Status);
        Assert.Equal(Now.AddMinutes(2), released.ReleasedAtUtc);
        Assert.Equal("operator-2", released.Reviewer);
        Assert.All(
            typeof(ContainmentRecord).GetProperties(),
            property => Assert.False(property.CanWrite));
    }

    [Fact]
    public void Record_RejectsStatusTimestampReviewerAndVersionContradictions()
    {
        var target = Target();

        Assert.Throws<ArgumentException>(
            () => Record(target, ContainmentStatus.Active, Now.AddMinutes(1), "reviewer", version: 1));
        Assert.Throws<ArgumentException>(
            () => Record(target, ContainmentStatus.Released, releasedAt: null, reviewer: null, version: 1));
        Assert.Throws<ArgumentException>(
            () => Record(target, ContainmentStatus.Released, Now.AddMinutes(-1), "reviewer", version: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Record(target, ContainmentStatus.Active, releasedAt: null, reviewer: null, version: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Record(target, (ContainmentStatus)99, releasedAt: null, reviewer: null, version: 1));
    }

    [Fact]
    public void Record_RejectsNonUtcAndFreeFormEvidenceWithoutEchoingIt()
    {
        const string secret = "raw evidence with spaces SECRET-42";
        var nonUtc = Now.ToOffset(TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(
            () => Record(Target(), ContainmentStatus.Active, null, null, 1, containedAt: nonUtc));
        var exception = Assert.Throws<ArgumentException>(
            () => new ContainmentRecord(
                Target(),
                ContainmentStatus.Active,
                Now,
                null,
                "block-storm",
                secret,
                "issuer",
                null,
                1,
                "etag:1"));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotStates_HaveFailClosedMustBlockTruthTable()
    {
        var target = Target();
        var notContained = ContainmentSnapshot.NotContained(target);
        var active = ContainmentSnapshot.FromRecord(ActiveRecord());
        var released = ContainmentSnapshot.FromRecord(ReleasedRecord());
        var indeterminate = ContainmentSnapshot.Indeterminate(target, "store-unavailable");

        Assert.False(notContained.MustBlock);
        Assert.True(active.MustBlock);
        Assert.False(released.MustBlock);
        Assert.True(indeterminate.MustBlock);
        Assert.Null(notContained.Record);
        Assert.Same(active.Target, active.Record!.Target);
        Assert.Null(indeterminate.Record);
        Assert.Equal("store-unavailable", indeterminate.FailureCode);
    }

    [Fact]
    public void Snapshot_IndeterminateFailureCodeIsBoundedAndSecretFree()
    {
        const string secret = "failure code with SECRET";
        var exception = Assert.Throws<ArgumentException>(
            () => ContainmentSnapshot.Indeterminate(Target(), secret));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MutationResultFactories_EnforceSnapshotConsistency()
    {
        var target = Target();
        var active = ContainmentSnapshot.FromRecord(ActiveRecord());
        var released = ContainmentSnapshot.FromRecord(ReleasedRecord());
        var clean = ContainmentSnapshot.NotContained(target);
        var indeterminate = ContainmentSnapshot.Indeterminate(target, "store-unavailable");

        Assert.Equal(ContainmentMutationDisposition.Applied, ContainmentMutationResult.Applied(active).Disposition);
        Assert.Equal(ContainmentMutationDisposition.Applied, ContainmentMutationResult.Applied(released).Disposition);
        Assert.Equal(ContainmentMutationDisposition.Unchanged, ContainmentMutationResult.Unchanged(active).Disposition);
        Assert.Equal(ContainmentMutationDisposition.Conflict, ContainmentMutationResult.Conflict(clean).Disposition);
        Assert.Equal(
            ContainmentMutationDisposition.Indeterminate,
            ContainmentMutationResult.Indeterminate(indeterminate).Disposition);

        Assert.Throws<ArgumentException>(() => ContainmentMutationResult.Applied(clean));
        Assert.Throws<ArgumentException>(() => ContainmentMutationResult.Unchanged(released));
        Assert.Throws<ArgumentException>(() => ContainmentMutationResult.Conflict(indeterminate));
        Assert.Throws<ArgumentException>(() => ContainmentMutationResult.Indeterminate(active));
    }

    [Fact]
    public void ContainmentRequest_AllowsOnlyBoundedCodesAndNormalizesIssuer()
    {
        var request = new ContainmentRequest(Target(), "block-storm", "ref:123", " operator@example.com ");

        Assert.Equal("operator@example.com", request.Issuer);
        const string secret = "raw reason SECRET";
        var exception = Assert.Throws<ArgumentException>(
            () => new ContainmentRequest(Target(), secret, "ref:123", "issuer"));
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAuthorization_ValidEnvelopePreservesExactCasAuthority()
    {
        var authorization = Authorization();

        Assert.Equal(7, authorization.ExpectedVersion);
        Assert.Equal("operator@example.com", authorization.OperatorId);
        Assert.Equal(Now, authorization.IssuedAtUtc);
        Assert.Equal(Now.AddMinutes(5), authorization.ExpiresAtUtc);
        Assert.Equal("hmac-sha256", authorization.Algorithm);
        Assert.Equal(1, authorization.AlgorithmVersion);
    }

    [Fact]
    public void ReleaseAuthorization_RejectsInvalidVersionTimeAndTtl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Authorization(expectedVersion: 0));
        Assert.Throws<ArgumentException>(() => Authorization(expiresAt: Now));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Authorization(expiresAt: Now + ContainmentReleaseAuthorization.MaximumLifetime + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentException>(
            () => Authorization(issuedAt: Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Authorization(algorithmVersion: 0));
    }

    [Fact]
    public void ReleaseAuthorization_RejectsMalformedSignatureWithoutEchoingIt()
    {
        const string secret = "signature with SECRET spaces";
        var exception = Assert.Throws<ArgumentException>(() => Authorization(signature: secret));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StoreContract_UsesBoundedSyncReadAsyncMutationsCancellationAndDisposal()
    {
        var contract = typeof(IContainmentStore);
        var getCurrent = contract.GetMethod(nameof(IContainmentStore.GetCurrent))!;
        var contain = contract.GetMethod(nameof(IContainmentStore.ContainAsync))!;
        var release = contract.GetMethod(nameof(IContainmentStore.ReleaseAsync))!;

        Assert.Equal(typeof(ContainmentSnapshot), getCurrent.ReturnType);
        Assert.Equal(typeof(ValueTask<ContainmentMutationResult>), contain.ReturnType);
        Assert.Equal(typeof(ValueTask<ContainmentMutationResult>), release.ReturnType);
        Assert.Equal(typeof(CancellationToken), contain.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(CancellationToken), release.GetParameters()[1].ParameterType);
        Assert.Contains(typeof(IDisposable), contract.GetInterfaces());
    }

    [Fact]
    public void ResolvedOptions_PreserveExactCallerOwnedStoreAndTargetResolverReferences()
    {
        using var store = new FakeStore();
        Func<Microsoft.Agents.AI.AgentSession, IReadOnlyList<ContainmentTarget>> targets = _ => [Target()];
        var options = new GatekeeperOptions
        {
            ContainmentStore = store,
            ContainmentTargets = targets,
        };
        var resolver = typeof(GatekeeperOptions).Assembly
            .GetType("AgentEval.MAF.Gatekeeper.GatekeeperOptionsResolver", throwOnError: true)!;
        var resolved = resolver.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [options])!;
        var resolvedStore = resolved.GetType().GetProperty("ContainmentStore")!.GetValue(resolved);
        var resolvedTargets = resolved.GetType().GetProperty("ContainmentTargets")!.GetValue(resolved);


        Assert.Same(targets, resolvedTargets);
        Assert.Same(store, resolvedStore);
    }

    private static ContainmentTarget Target()
        => new ContainmentTarget.Session("tenant-a", "session-1");

    private static ContainmentRecord ActiveRecord()
        => Record(Target(), ContainmentStatus.Active, releasedAt: null, reviewer: null, version: 7);

    private static ContainmentRecord ReleasedRecord()
        => Record(Target(), ContainmentStatus.Released, Now.AddMinutes(2), "operator-2", version: 8);

    private static ContainmentRecord Record(
        ContainmentTarget target,
        ContainmentStatus status,
        DateTimeOffset? releasedAt,
        string? reviewer,
        long version,
        DateTimeOffset? containedAt = null)
        => new(
            target,
            status,
            containedAt ?? Now,
            releasedAt,
            "block-storm",
            "ref:123",
            "sentinel",
            reviewer,
            version,
            $"etag:{version}");

    private static ContainmentReleaseAuthorization Authorization(
        long expectedVersion = 7,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        int algorithmVersion = 1,
        string signature = "abcdef0123456789")
        => new(
            Target(),
            expectedVersion,
            "operator@example.com",
            issuedAt ?? Now,
            expiresAt ?? Now.AddMinutes(5),
            "nonce-0123456789",
            "hmac-sha256",
            algorithmVersion,
            "key-1",
            signature);

    private sealed class FakeStore : IContainmentStore
    {
        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
            => ContainmentSnapshot.NotContained(target);

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
