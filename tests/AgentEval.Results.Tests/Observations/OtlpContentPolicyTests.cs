// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Results.Observations;

namespace AgentEval.Results.Tests.Observations;

public sealed class OtlpContentPolicyTests
{
    private static OtlpTraceBatch Input() => new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes("""
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"service.name","value":{"stringValue":"service"}},
          {"key":"secret","value":{"stringValue":"resource-secret"}}],"droppedAttributesCount":3,
          "entityRefs":[{"type":"private","idKeys":["secret"]}]},
          "schemaUrl":"schema","scopeSpans":[{"scope":{"name":"sdk","version":"1",
          "attributes":[{"key":"secret","value":{"stringValue":"scope-secret"}}]},"spans":[{
          "traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef",
          "name":"prompt-secret","traceState":"secret","droppedAttributesCount":4,
          "droppedEventsCount":5,"droppedLinksCount":6,"status":{"code":2,"message":"error-secret"},
          "attributes":[{"key":"gen_ai.request.model","value":{"stringValue":"model"}},
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"12"}},
            {"key":"gen_ai.usage.prompt_tokens","value":{"arrayValue":{"values":[{"stringValue":"nested-secret"}]}}},
            {"key":"gen_ai.input.messages","value":{"stringValue":"message-secret"}}],
          "events":[{"name":"event-secret","attributes":[{"key":"secret","value":{"stringValue":"event-payload"}}]}],
          "links":[{"traceId":"0123456789abcdef0123456789abcdef","spanId":"1123456789abcdef",
            "traceState":"link-secret","droppedAttributesCount":7,
            "attributes":[{"key":"gen_ai.request.model","value":{"stringValue":"link-secret"}}]}]
        }]}]}]}
        """));

    [Fact]
    public void Apply_DefaultOff_RemovesContentAcrossAllLocationsAndPreservesLossProvenance()
    {
        var original = Input();
        var result = new OtlpContentPolicy().Apply(original);
        Assert.Equal(OtlpContentCapture.Off, result.Capture);
        Assert.Equal(OtlpContentPolicy.Revision, result.Revision);
        Assert.Equal(new OtlpContentRemovals(4, 1, 1, 2, 1, 1, 1), result.Removals);
        var resource = Assert.Single(result.Batch.Resources);
        Assert.Equal("schema", resource.SchemaUrl);
        Assert.Empty(resource.Resource!.EntityRefs);
        Assert.Equal(3u, resource.Resource.DroppedAttributesCount);
        Assert.Single(resource.Resource.Attributes);
        var scope = Assert.Single(resource.Scopes);
        Assert.Equal("sdk", scope.Scope!.Name);
        Assert.Empty(scope.Scope.Attributes);
        var span = Assert.Single(scope.Spans);
        Assert.Equal("", span.Name);
        Assert.Equal("", span.TraceState);
        Assert.Equal(new OtlpSpanStatus(2, ""), span.Status);
        Assert.Empty(span.Events);
        Assert.Equal(4u, span.DroppedAttributesCount);
        Assert.Equal(5u, span.DroppedEventsCount);
        Assert.Equal(6u, span.DroppedLinksCount);
        var link = Assert.Single(span.Links);
        Assert.Equal("1123456789abcdef", link.SpanId);
        Assert.Equal(7u, link.DroppedAttributesCount);
        Assert.Empty(link.Attributes);
        Assert.Equal("", link.TraceState);
        var normalized = Assert.Single(new OtlpTraceNormalizer().Normalize(result.Batch));
        Assert.Equal(OtlpFieldState.Invalid, normalized.Field("tokens.input").State);
        Assert.Equal(new OtlpValue.Text("model"), normalized.Field("model.request").Value);
        Assert.Equal("prompt-secret", original.Resources[0].Scopes[0].Spans[0].Name);
    }

    [Fact]
    public void Apply_ExplicitOn_RetainsImmutableDecodedSnapshot()
    {
        var input = Input();
        var result = new OtlpContentPolicy().Apply(input, OtlpContentCapture.On);
        Assert.Same(input, result.Batch);
        Assert.Equal(new OtlpContentRemovals(0, 0, 0, 0, 0, 0, 0), result.Removals);
    }

    [Fact]
    public void Apply_FilteredAgain_DoesNotRestoreContentOrLoseInvalidFieldState()
    {
        var policy = new OtlpContentPolicy();
        var first = policy.Apply(Input());
        var second = policy.Apply(first.Batch);
        Assert.Equal(0, second.Removals.Attributes);
        Assert.Equal(0, second.Removals.Events);
        Assert.Equal(OtlpFieldState.Invalid, Assert.Single(new OtlpTraceNormalizer().Normalize(second.Batch)).Field("tokens.input").State);
    }

    [Fact]
    public void Apply_CancelledOrUnknownMode_Refuses()
    {
        var policy = new OtlpContentPolicy();
        Assert.Throws<OperationCanceledException>(() => policy.Apply(Input(), cancellationToken: new(true)));
        Assert.Throws<OperationCanceledException>(() => policy.Apply(Input(), OtlpContentCapture.On, new(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Apply(Input(), (OtlpContentCapture)3));
    }
}
