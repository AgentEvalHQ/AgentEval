// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;

namespace AgentEval.Results.Observations;

/// <summary>The result of reconciling reported aliases, without inferring missing evidence.</summary>
public enum OtlpFieldState { Missing, Present, Invalid, Conflict }
/// <summary>One allowlisted source. Invalid values are omitted and explained by a stable issue code.</summary>
public sealed record OtlpFieldSource(string Location, string Attribute, string Profile, OtlpValue? Value, string? Issue);
/// <summary>Consensus only exists when every reported candidate is valid and agrees.</summary>
public sealed record OtlpNormalizedField(OtlpFieldState State, OtlpValue? Value, ImmutableArray<OtlpFieldSource> Sources)
{
    /// <summary>A field for which no recognized attribute was reported.</summary>
    public static OtlpNormalizedField Missing { get; } = new(OtlpFieldState.Missing, null, []);
}
/// <summary>Versioned, structural span projection. This is neither content filtering certification nor an evaluation result.</summary>
public sealed record OtlpNormalizedSpan(
    string MappingRevision, int ResourceIndex, int ScopeIndex, int SpanIndex,
    string TraceId, string SpanId, string ParentSpanId, ulong StartTimeUnixNano, ulong EndTimeUnixNano,
    string ResourceSchemaUrl, string ScopeSchemaUrl, string ScopeName, string ScopeVersion,
    bool HasRecognizedDialectFields, ImmutableArray<string> Profiles, int UnmappedSpanAttributeCount,
    ImmutableDictionary<string, OtlpNormalizedField> Fields)
{
    /// <summary>Looks up a canonical field; absent fields remain explicitly missing.</summary>
    public OtlpNormalizedField Field(string name) => Fields.TryGetValue(name, out var field) ? field : OtlpNormalizedField.Missing;
}
/// <summary>Produces structural projections of decoded telemetry without modifying source data.</summary>
public interface IOtlpTraceNormalizer
{
    /// <summary>Normalizes under finite budgets. Unknown field families are not guessed from span names.</summary>
    ImmutableArray<OtlpNormalizedSpan> Normalize(OtlpTraceBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>Explicit, versioned field mapping for OTel GenAI, OpenInference and documented legacy aliases.</summary>
public sealed class OtlpTraceNormalizer : IOtlpTraceNormalizer
{
    /// <summary>Application mapping policy version, not a claim about the producer's convention version.</summary>
    public const string Revision = "otlp-normalization/1";
    private readonly OtlpIngestionLimits _limits;
    private sealed record Mapping(string Target, string Profile, bool Count = false);
    private const string GenAi = "otel-genai-1.37.0-fields/1";
    private const string Legacy = "genai-legacy-aliases/1";
    private const string OpenInference = "openinference-2026-10-14/1";
    private const string Vercel = "vercel-ai-legacy-2026-10-14/1";
    private static readonly ImmutableDictionary<string, Mapping> SpanMappings = new Dictionary<string, Mapping>(StringComparer.Ordinal)
    {
        ["gen_ai.operation.name"] = new("operation", GenAi),
        ["gen_ai.provider.name"] = new("provider", GenAi),
        ["gen_ai.system"] = new("system", Legacy),
        ["gen_ai.request.model"] = new("model.request", GenAi),
        ["gen_ai.response.model"] = new("model.response", GenAi),
        ["gen_ai.conversation.id"] = new("session.id", GenAi),
        ["gen_ai.agent.id"] = new("agent.id", GenAi),
        ["gen_ai.agent.name"] = new("agent.name", GenAi),
        ["gen_ai.tool.name"] = new("tool.name", GenAi),
        ["gen_ai.tool.call.id"] = new("tool.call.id", GenAi),
        ["gen_ai.usage.input_tokens"] = new("tokens.input", GenAi, true),
        ["gen_ai.usage.output_tokens"] = new("tokens.output", GenAi, true),
        ["gen_ai.usage.prompt_tokens"] = new("tokens.input", Legacy, true),
        ["gen_ai.usage.completion_tokens"] = new("tokens.output", Legacy, true),
        ["gen_ai.usage.total_tokens"] = new("tokens.total", Legacy, true),
        ["gen_ai.session.id"] = new("session.id", "eino-session-alias/1"),
        ["openinference.span.kind"] = new("span.role", OpenInference),
        ["llm.provider"] = new("provider", OpenInference),
        ["llm.system"] = new("system", OpenInference),
        ["llm.model_name"] = new("model.reported", OpenInference),
        ["llm.request.model_name"] = new("model.request", OpenInference),
        ["llm.response.model_name"] = new("model.response", OpenInference),
        ["embedding.model_name"] = new("embedding.model", OpenInference),
        ["llm.token_count.prompt"] = new("tokens.input", OpenInference, true),
        ["llm.token_count.completion"] = new("tokens.output", OpenInference, true),
        ["llm.token_count.total"] = new("tokens.total", OpenInference, true),
        ["session.id"] = new("session.id", OpenInference),
        ["tool.name"] = new("tool.name", OpenInference),
        ["ai.operationId"] = new("operation.native", Vercel),
        ["ai.model.id"] = new("model.request", Vercel),
        ["ai.model.provider"] = new("provider", Vercel),
        ["ai.response.model"] = new("model.response", Vercel),
        ["ai.usage.promptTokens"] = new("tokens.input", Vercel, true),
        ["ai.usage.completionTokens"] = new("tokens.output", Vercel, true),
        ["ai.toolCall.name"] = new("tool.name", Vercel),
        ["ai.toolCall.id"] = new("tool.call.id", Vercel)
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static readonly ImmutableDictionary<string, Mapping> ResourceMappings = new Dictionary<string, Mapping>(StringComparer.Ordinal)
    {
        ["service.name"] = new("service.name", "otel-resource-fields/1"),
        ["service.namespace"] = new("service.namespace", "otel-resource-fields/1"),
        ["service.instance.id"] = new("service.instance.id", "otel-resource-fields/1")
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Creates a normalizer with validated limits shared with decoding.</summary>
    public OtlpTraceNormalizer(OtlpIngestionLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc />
    public ImmutableArray<OtlpNormalizedSpan> Normalize(OtlpTraceBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        var result = ImmutableArray.CreateBuilder<OtlpNormalizedSpan>();
        var visited = 0;
        for (var ri = 0; ri < batch.Resources.Length; ri++)
        {
            Visit();
            var resource = batch.Resources[ri];
            var resourceFields = Collect(resource.Resource?.Attributes ?? [], ResourceMappings, "resource");
            for (var si = 0; si < resource.Scopes.Length; si++)
            {
                Visit();
                var scope = resource.Scopes[si];
                for (var pi = 0; pi < scope.Spans.Length; pi++)
                {
                    Visit();
                    if (result.Count >= _limits.MaxSpans) throw Limit();
                    var span = scope.Spans[pi];
                    var spanFields = Collect(span.Attributes, SpanMappings, "span");
                    var fields = resourceFields.Concat(spanFields).ToImmutableDictionary(pair => pair.Key, pair => Resolve(pair.Value), StringComparer.Ordinal);
                    var profiles = fields.Values.SelectMany(f => f.Sources).Select(s => s.Profile).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
                    result.Add(new(Revision, ri, si, pi, span.TraceId, span.SpanId, span.ParentSpanId, span.StartTimeUnixNano, span.EndTimeUnixNano,
                        Metadata(resource.SchemaUrl), Metadata(scope.SchemaUrl), Metadata(scope.Scope?.Name ?? ""), Metadata(scope.Scope?.Version ?? ""),
                        spanFields.Count != 0, profiles, span.Attributes.Count(a => !SpanMappings.ContainsKey(a.Key)), fields));
                }
            }
        }
        return result.ToImmutable();

        void Visit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > _limits.MaxItems) throw Limit();
        }
        string Metadata(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) > _limits.MaxValueBytes) throw Limit();
            return text;
        }
        Dictionary<string, List<OtlpFieldSource>> Collect(ImmutableArray<OtlpAttribute> attributes, ImmutableDictionary<string, Mapping> mappings, string location)
        {
            if (attributes.Length > _limits.MaxAttributes) throw Limit();
            var output = new Dictionary<string, List<OtlpFieldSource>>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in attributes)
            {
                Visit();
                if (!keys.Add(attribute.Key)) throw new OtlpIngestionException("malformed", "duplicate attribute in normalization input");
                if (!mappings.TryGetValue(attribute.Key, out var mapping)) continue;
                string? issue = mapping.Count
                    ? attribute.Value is OtlpValue.Integer number ? number.Value < 0 ? "negative-count" : null : "expected-integer"
                    : attribute.Value is OtlpValue.Text text ? string.IsNullOrWhiteSpace(text.Value) ? "empty-string" : null : "expected-string";
                if (attribute.Value is OtlpValue.Text value && Encoding.UTF8.GetByteCount(value.Value) > _limits.MaxValueBytes) throw Limit();
                if (!output.TryGetValue(mapping.Target, out var sources)) output[mapping.Target] = sources = [];
                sources.Add(new(location, attribute.Key, mapping.Profile, issue is null ? attribute.Value : null, issue));
            }
            return output;
        }
    }

    private static OtlpNormalizedField Resolve(List<OtlpFieldSource> sources)
    {
        var ordered = sources.OrderBy(s => s.Location, StringComparer.Ordinal).ThenBy(s => s.Attribute, StringComparer.Ordinal).ToImmutableArray();
        var valid = ordered.Where(s => s.Issue is null).Select(s => s.Value).Distinct().ToArray();
        var state = valid.Length > 1 ? OtlpFieldState.Conflict
            : ordered.Any(s => s.Issue is not null) ? OtlpFieldState.Invalid : OtlpFieldState.Present;
        return new(state, state == OtlpFieldState.Present ? valid[0] : null, ordered);
    }
    private static OtlpIngestionException Limit() => new("limit", "normalization resource budget exceeded");
}
