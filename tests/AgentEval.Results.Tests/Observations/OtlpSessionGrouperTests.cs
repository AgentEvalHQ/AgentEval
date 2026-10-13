// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using AgentEval.Results.Observations;

namespace AgentEval.Results.Tests.Observations;

public sealed class OtlpSessionGrouperTests
{
    private static readonly OtlpSessionGrouper Grouper = new(new OtlpTraceNormalizer());
    private static OtlpAttribute Text(string key, string value) => new(key, new OtlpValue.Text(value));
    private static OtlpSpan Span(string trace, params OtlpAttribute[] attributes) =>
        new(trace.PadLeft(32, '0'), "0000000000000001", "", "", "", 0, 1, 2, 0, [.. attributes], 0, [], 0, [], 0, null);
    private static OtlpResourceSpans Resource(string? service, params OtlpSpan[] spans) =>
        new(new(service is null ? [] : [Text("service.name", service)], 0, []), "", [new(null, "", [.. spans])]);
    private static ImmutableArray<OtlpObservedSession> Group(params OtlpResourceSpans[] resources) => Grouper.Group("receiver", new([.. resources]));

    [Fact]
    public void Group_SameSessionAcrossTraces_JoinsWithoutInferringCompleteness()
    {
        var result = Assert.Single(Group(Resource("service", Span("1", Text("session.id", "session")), Span("2", Text("gen_ai.conversation.id", "session")))));
        Assert.StartsWith("obs-", result.Id);
        Assert.Equal(OtlpSessionGrouper.Revision, result.Revision);
        Assert.Equal(2, result.Members.Length);
        Assert.All(result.Members, member => Assert.Equal(OtlpGroupingBasis.Session, member.Basis));
    }

    [Fact]
    public void Group_SeparateSourcesServicesOrInstances_NeverSharesGroupId()
    {
        var span = Span("1", Text("session.id", "shared"));
        var first = Resource("first", span);
        var second = Resource("second", span);
        Assert.Equal(2, Group(first, second).Length);
        var instance = first with { Resource = first.Resource! with { Attributes = [Text("service.name", "first"), Text("service.instance.id", "instance")] } };
        Assert.Equal(2, Group(first, instance).Length);
        Assert.NotEqual(Assert.Single(Group(first)).Id, Assert.Single(Grouper.Group("other", new([first]))).Id);
    }

    [Theory]
    [InlineData("missing", OtlpGroupingBasis.TraceMissingSession)]
    [InlineData("invalid", OtlpGroupingBasis.TraceInvalidSession)]
    [InlineData("conflict", OtlpGroupingBasis.TraceConflictingSession)]
    [InlineData("no-service", OtlpGroupingBasis.TraceMissingService)]
    [InlineData("invalid-service", OtlpGroupingBasis.TraceInvalidService)]
    public void Group_UnusableIdentity_FallsBackToTraceWithExplicitReason(string input, OtlpGroupingBasis expected)
    {
        OtlpAttribute[] attributes = input switch
        {
            "missing" => [],
            "invalid" => [new("session.id", new OtlpValue.Integer(1))],
            "conflict" => [Text("session.id", "one"), Text("gen_ai.conversation.id", "two")],
            _ => [Text("session.id", "one")]
        };
        var resource = Resource(input == "no-service" ? null : "service", Span("1", attributes), Span("2", attributes));
        if (input == "invalid-service") resource = resource with { Resource = resource.Resource! with { Attributes = [Text("service.name", "service"), new("service.namespace", new OtlpValue.Empty())] } };
        var groups = Group(resource);
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Equal(expected, Assert.Single(group.Members).Basis));
    }

    [Fact]
    public void Group_ReorderedAndDuplicateReports_StableIdsAndRetainedReports()
    {
        var early = Span("1", Text("session.id", "session"));
        var late = Span("2", Text("session.id", "session")) with { StartTimeUnixNano = 10 };
        var first = Assert.Single(Group(Resource("service", late, early, early)));
        var second = Assert.Single(Group(Resource("service", early, late, early)));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(new ulong[] { 1, 1, 10 }, first.Members.Select(m => m.Span.StartTimeUnixNano));
        Assert.Equal(first.Members.Select(m => m.Span.TraceId), second.Members.Select(m => m.Span.TraceId));
        Assert.Equal(3, first.Members.Length);
        Assert.Equal(3, first.Members.Select(m => m.Span.SpanIndex).Distinct().Count());
    }

    [Fact]
    public void Group_TraceFallbackReasonsDiffer_SharesTraceWithoutLosingReasons()
    {
        var result = Assert.Single(Group(Resource("service", Span("1"), Span("1", new OtlpAttribute("session.id", new OtlpValue.Empty())))));
        Assert.Equal(2, result.Members.Length);
        Assert.Equal(2, result.Members.Select(m => m.Basis).Distinct().Count());
    }

    [Fact]
    public void Group_IdentityDelimiterAndUnicodeDifferences_DoNotCollide()
    {
        var first = Assert.Single(Group(Resource("a|b", Span("1", Text("session.id", "c")))));
        var second = Assert.Single(Group(Resource("a", Span("1", Text("session.id", "b|c")))));
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(Assert.Single(Group(Resource("é", Span("1")))).Id, Assert.Single(Group(Resource("e\u0301", Span("1")))).Id);
    }

    [Fact]
    public void Group_EmptyCancelledOrInvalidSource_HandlesBoundary()
    {
        Assert.Empty(Group());
        Assert.Throws<ArgumentException>(() => Grouper.Group(" ", new([])));
        Assert.Throws<ArgumentException>(() => Grouper.Group(new string('é', 129), new([])));
        Assert.Throws<OperationCanceledException>(() => Grouper.Group("source", new([]), new(true)));
        var bounded = new OtlpSessionGrouper(new OtlpTraceNormalizer(new() { MaxSpans = 1 }));
        Assert.Throws<OtlpIngestionException>(() => bounded.Group("source", new([Resource("service", Span("1"), Span("2"))])));
    }
}
