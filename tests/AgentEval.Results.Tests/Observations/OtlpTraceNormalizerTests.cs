// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;
using AgentEval.Results.Observations;

namespace AgentEval.Results.Tests.Observations;

public sealed class OtlpTraceNormalizerTests
{
    private static readonly OtlpSpan EmptySpan = new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes("""
        {"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef"}]}]}]}
        """)).Resources[0].Scopes[0].Spans[0];
    private static OtlpAttribute Text(string key, string value) => new(key, new OtlpValue.Text(value));
    private static OtlpAttribute Count(string key, long value) => new(key, new OtlpValue.Integer(value));
    private static OtlpTraceBatch Batch(params OtlpAttribute[] attributes) => new([
        new(new([Text("service.name", "service")], 0, []), "resource-schema", [
            new(new("instrumentation", "producer-version", [], 0), "producer-schema", [EmptySpan with { Attributes = [.. attributes] }])])]);
    private static OtlpNormalizedSpan Normalize(params OtlpAttribute[] attributes) =>
        Assert.Single(new OtlpTraceNormalizer().Normalize(Batch(attributes)));

    [Fact]
    public void Normalize_CurrentGenAi_RetainsIdentityAndProducerProvenance()
    {
        var result = Normalize(Text("gen_ai.operation.name", "chat"), Text("gen_ai.provider.name", "provider"),
            Text("gen_ai.request.model", "requested"), Text("gen_ai.response.model", "actual"), Count("gen_ai.usage.input_tokens", 9007199254740993),
            Text("gen_ai.conversation.id", "conversation"));
        Assert.Equal(OtlpTraceNormalizer.Revision, result.MappingRevision);
        Assert.Equal("resource-schema", result.ResourceSchemaUrl);
        Assert.Equal("producer-schema", result.ScopeSchemaUrl);
        Assert.Equal("producer-version", result.ScopeVersion);
        Assert.Equal("instrumentation", result.ScopeName);
        Assert.Equal(EmptySpan.TraceId, result.TraceId);
        Assert.True(result.HasRecognizedDialectFields);
        Assert.Equal(new OtlpValue.Text("requested"), result.Field("model.request").Value);
        Assert.Equal(new OtlpValue.Text("actual"), result.Field("model.response").Value);
        Assert.Equal(new OtlpValue.Integer(9007199254740993), result.Field("tokens.input").Value);
        Assert.Equal("resource", Assert.Single(result.Field("service.name").Sources).Location);
        Assert.Equal("gen_ai.conversation.id", Assert.Single(result.Field("session.id").Sources).Attribute);
    }

    [Fact]
    public void Normalize_EqualAliases_AgreesWithoutDoubleCounting()
    {
        var result = Normalize(Count("gen_ai.usage.input_tokens", 12), Count("gen_ai.usage.prompt_tokens", 12),
            Count("llm.token_count.prompt", 12), Count("ai.usage.promptTokens", 12));
        var field = result.Field("tokens.input");
        Assert.Equal(OtlpFieldState.Present, field.State);
        Assert.Equal(new OtlpValue.Integer(12), field.Value);
        Assert.Equal(4, field.Sources.Length);
        Assert.Equal(OtlpFieldState.Missing, result.Field("tokens.total").State);
    }

    [Fact]
    public void Normalize_ConflictingAliases_RetainsCandidatesWithoutChoosing()
    {
        var result = Normalize(Count("gen_ai.usage.input_tokens", 12), Count("gen_ai.usage.prompt_tokens", 13));
        var field = result.Field("tokens.input");
        Assert.Equal(OtlpFieldState.Conflict, field.State);
        Assert.Null(field.Value);
        Assert.Equal(new long[] { 12, 13 }, field.Sources.Select(s => Assert.IsType<OtlpValue.Integer>(s.Value).Value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normalize_InvalidAlias_IsNotRescuedByValidAlias(bool wrongType)
    {
        var invalid = wrongType ? Text("gen_ai.usage.prompt_tokens", "42") : Count("gen_ai.usage.prompt_tokens", -1);
        var field = Normalize(Count("gen_ai.usage.input_tokens", 42), invalid).Field("tokens.input");
        Assert.Equal(OtlpFieldState.Invalid, field.State);
        Assert.Null(field.Value);
        Assert.Contains(field.Sources, s => s.Issue == (wrongType ? "expected-integer" : "negative-count") && s.Value is null);
    }

    [Fact]
    public void Normalize_SystemAndProviderAndGenericModel_RemainDistinct()
    {
        var result = Normalize(Text("gen_ai.system", "strands-agents"), Text("gen_ai.provider.name", "aws.bedrock"),
            Text("llm.model_name", "reported"), Text("openinference.span.kind", "EVALUATOR"));
        Assert.Equal(new OtlpValue.Text("strands-agents"), result.Field("system").Value);
        Assert.Equal(new OtlpValue.Text("aws.bedrock"), result.Field("provider").Value);
        Assert.Equal(new OtlpValue.Text("reported"), result.Field("model.reported").Value);
        Assert.Equal(OtlpFieldState.Missing, result.Field("model.request").State);
        Assert.Equal(OtlpFieldState.Missing, result.Field("model.response").State);
        Assert.Equal(OtlpFieldState.Missing, result.Field("verdict").State);
        Assert.Equal(new OtlpValue.Text("EVALUATOR"), result.Field("span.role").Value);
    }

    [Fact]
    public void Normalize_LegacyVercelAndEino_PreservesExplicitSources()
    {
        var result = Normalize(Text("ai.operationId", "ai.generateText"), Text("ai.model.id", "model"),
            Text("ai.toolCall.name", "tool"), Text("gen_ai.session.id", "session"));
        Assert.Equal(new OtlpValue.Text("ai.generateText"), result.Field("operation.native").Value);
        Assert.Equal(OtlpFieldState.Missing, result.Field("operation").State);
        Assert.Equal(new OtlpValue.Text("model"), result.Field("model.request").Value);
        Assert.Equal("eino-session-alias/1", Assert.Single(result.Field("session.id").Sources).Profile);
    }

    [Fact]
    public void Normalize_UnknownOrContentFields_DoesNotGuessOrCopyContent()
    {
        var result = Normalize(Text("gen_ai.input.messages", "secret prompt"), Text("ai.toolCall.args", "secret arguments"),
            Text("custom.model", "not a recognized model"));
        Assert.False(result.HasRecognizedDialectFields);
        Assert.Equal(3, result.UnmappedSpanAttributeCount);
        Assert.Single(result.Fields); // resource service identity only
        Assert.Equal(OtlpFieldState.Missing, result.Field("model.request").State);
        Assert.DoesNotContain(result.Fields.Values.SelectMany(f => f.Sources), s => s.Attribute.Contains("messages") || s.Attribute.Contains("args"));
    }

    [Fact]
    public void Normalize_AttributeOrderChanges_ProducesStableSourcesAndProfiles()
    {
        OtlpAttribute[] attributes = [Count("gen_ai.usage.input_tokens", 12), Count("llm.token_count.prompt", 12), Text("gen_ai.provider.name", "provider")];
        var first = Normalize(attributes);
        var second = Normalize(Enumerable.Reverse(attributes).ToArray());
        Assert.Equal(first.Profiles.ToArray(), second.Profiles.ToArray());
        Assert.Equal(first.Field("tokens.input").Sources.ToArray(), second.Field("tokens.input").Sources.ToArray());
    }

    [Fact]
    public void Normalize_ParentAndChildUsage_DoesNotAggregateSpans()
    {
        var batch = Batch(Count("gen_ai.usage.input_tokens", 10));
        var resource = batch.Resources[0];
        var scope = resource.Scopes[0];
        var parent = scope.Spans[0];
        var child = parent with { SpanId = "fedcba9876543210", ParentSpanId = parent.SpanId };
        batch = new([resource with { Scopes = [scope with { Spans = [parent, child] }] }]);
        var result = new OtlpTraceNormalizer().Normalize(batch);
        Assert.Equal(2, result.Length);
        Assert.All(result, span => Assert.Equal(new OtlpValue.Integer(10), span.Field("tokens.input").Value));
    }

    [Fact]
    public void Normalize_BoundsCancellationAndDuplicateKeys_Refuses()
    {
        var batch = Batch(Text("gen_ai.request.model", "model"));
        Assert.ThrowsAny<OperationCanceledException>(() => new OtlpTraceNormalizer().Normalize(batch, new(true)));
        Assert.Equal("limit", Assert.Throws<OtlpIngestionException>(() => new OtlpTraceNormalizer(new() { MaxItems = 1 }).Normalize(batch)).Code);
        Assert.Equal("malformed", Assert.Throws<OtlpIngestionException>(() => Normalize(Text("gen_ai.request.model", "a"), Text("gen_ai.request.model", "b"))).Code);
        Assert.Equal(OtlpFieldState.Invalid, Normalize(Text("gen_ai.request.model", " ")).Field("model.request").State);
    }
}
