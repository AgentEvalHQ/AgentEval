// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Microsoft365.Evidence;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// <see cref="EvidenceSet{T}.Describe"/>: the one line every host prints for a channel. A channel that was not provided
/// prints its availability, an observed-empty channel says so, and a conflicting or malformed channel (always zero items)
/// names its validation, so it never reads like an observed-empty one.
/// </summary>
public sealed class EvidenceSetDescribeTests
{
    private static readonly ImmutableArray<SourceRef> Sources = ImmutableArray.Create(new SourceRef("cap-1", 0, EvidenceOrigin.A2AArtifact, "/artifacts"));

    private static EvidenceSet<string> Provided(EvidenceValidation validation, params string[] items) =>
        EvidenceSet<string>.Provided(items.ToImmutableArray(), validation, EvidenceCoverage.Unknown, EvidenceScope.Unknown, Sources);

    [Fact]
    public void NotProvidedChannels_PrintTheirAvailability()
    {
        Assert.Equal("NotRequested", EvidenceSet<string>.NotRequested(EvidenceScope.Turn).Describe("execution(s)"));
        Assert.Equal("NotProvided", EvidenceSet<string>.NotProvided(EvidenceScope.Unknown, Sources).Describe("execution(s)"));
        Assert.Equal("Disabled", EvidenceSet<string>.Disabled(EvidenceScope.Turn).Describe("execution(s)"));
        Assert.Equal("Redacted", EvidenceSet<string>.Redacted(EvidenceScope.Turn).Describe("execution(s)"));
        Assert.Equal("Unsupported", EvidenceSet<string>.Unsupported(EvidenceScope.Turn, Sources).Describe("execution(s)"));
    }

    [Fact]
    public void ObservedEmpty_SaysZeroInThisSnapshot()
    {
        var set = Provided(EvidenceValidation.Valid);

        Assert.True(set.IsObservedEmpty);
        Assert.Equal("Provided (observed empty: 0 execution(s) in this snapshot)", set.Describe("execution(s)"));
    }

    [Fact]
    public void ValidItems_PrintTheCountAndNoValidation() =>
        Assert.Equal("Provided (2 execution(s))", Provided(EvidenceValidation.Valid, "a", "b").Describe("execution(s)"));

    [Theory]
    [InlineData(EvidenceValidation.Conflicting, "Provided (0 execution(s), Conflicting)")]
    [InlineData(EvidenceValidation.Malformed, "Provided (0 execution(s), Malformed)")]
    public void ConflictingOrMalformed_NeverReadsLikeObservedEmpty(EvidenceValidation validation, string expected)
    {
        var set = Provided(validation);
        var observedEmpty = Provided(EvidenceValidation.Valid);

        Assert.False(set.IsObservedEmpty);
        Assert.Equal(expected, set.Describe("execution(s)"));
        Assert.NotEqual(observedEmpty.Describe("execution(s)"), set.Describe("execution(s)"));
    }

    [Fact]
    public void PartialItems_PrintTheCountAndTheValidation() =>
        Assert.Equal("Provided (3 marker(s), Partial)", Provided(EvidenceValidation.Partial, "a", "b", "c").Describe("marker(s)"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankNoun_Throws(string? noun) =>
        Assert.ThrowsAny<ArgumentException>(() => Provided(EvidenceValidation.Valid).Describe(noun!));
}
