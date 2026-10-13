// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Results.Observations;

/// <summary>Content capture requires explicit opt-in; structural metadata is retained in either mode.</summary>
public enum OtlpContentCapture { Off, On }
/// <summary>Receiver omissions, separate from producer loss counters. Contains no removed keys or values.</summary>
public sealed record OtlpContentRemovals(long Attributes, long InvalidValues, long SpanNames,
    long TraceStates, long StatusMessages, long Events, long EntityReferences);
/// <summary>A filtered snapshot and its policy provenance; never an evaluation or completeness verdict.</summary>
public sealed record OtlpContentSnapshot(string Revision, OtlpContentCapture Capture,
    OtlpTraceBatch Batch, OtlpContentRemovals Removals);
/// <summary>Applies the persistence content boundary to an immutable, decoder-validated batch.</summary>
public interface IOtlpContentPolicy
{
    /// <summary>Filters decoded input. Wire validation and resource budgets belong to the bounded decoder.</summary>
    OtlpContentSnapshot Apply(OtlpTraceBatch batch, OtlpContentCapture capture = OtlpContentCapture.Off,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default-off allowlist policy. Structural strings remain producer-controlled; this is not secret detection.
/// Discarded events are counted as events, without double-counting their contained attributes.
/// </summary>
public sealed class OtlpContentPolicy : IOtlpContentPolicy
{
    /// <summary>Version of the structural allowlist and omission semantics.</summary>
    public const string Revision = "otlp-content/1";

    /// <inheritdoc />
    public OtlpContentSnapshot Apply(OtlpTraceBatch batch, OtlpContentCapture capture = OtlpContentCapture.Off,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (capture is not (OtlpContentCapture.Off or OtlpContentCapture.On))
            throw new ArgumentOutOfRangeException(nameof(capture));
        if (capture == OtlpContentCapture.On)
            return new(Revision, capture, batch, new(0, 0, 0, 0, 0, 0, 0));

        long attributes = 0, invalid = 0, names = 0, states = 0, messages = 0, events = 0, entities = 0;
        var resources = ImmutableArray.CreateBuilder<OtlpResourceSpans>();
        foreach (var resource in batch.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OtlpResource? filteredResource = null;
            if (resource.Resource is { } metadata)
            {
                entities += metadata.EntityRefs.Length;
                filteredResource = metadata with { Attributes = Filter(metadata.Attributes, OtlpFieldMappings.ResourceMappings), EntityRefs = [] };
            }
            var scopes = ImmutableArray.CreateBuilder<OtlpScopeSpans>();
            foreach (var scope in resource.Scopes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var spans = ImmutableArray.CreateBuilder<OtlpSpan>();
                foreach (var span in scope.Spans)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (span.Name.Length != 0) names++;
                    if (span.TraceState.Length != 0) states++;
                    if (span.Status?.Message.Length > 0) messages++;
                    events += span.Events.Length;
                    var links = ImmutableArray.CreateBuilder<OtlpSpanLink>();
                    foreach (var link in span.Links)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (link.TraceState.Length != 0) states++;
                        attributes += link.Attributes.Length;
                        links.Add(link with { TraceState = "", Attributes = [] });
                    }
                    spans.Add(span with
                    {
                        Name = "", TraceState = "", Events = [], Links = links.ToImmutable(),
                        Status = span.Status is { } status ? status with { Message = "" } : null,
                        Attributes = Filter(span.Attributes, OtlpFieldMappings.SpanMappings)
                    });
                }
                if (scope.Scope is { } instrumentation) attributes += instrumentation.Attributes.Length;
                scopes.Add(scope with { Scope = scope.Scope is { } original ? original with { Attributes = [] } : null, Spans = spans.ToImmutable() });
            }
            resources.Add(resource with { Resource = filteredResource, Scopes = scopes.ToImmutable() });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Revision, capture, new(resources.ToImmutable()), new(attributes, invalid, names, states, messages, events, entities));

        ImmutableArray<OtlpAttribute> Filter(ImmutableArray<OtlpAttribute> source,
            ImmutableDictionary<string, OtlpFieldMappings.Mapping> mappings)
        {
            var output = ImmutableArray.CreateBuilder<OtlpAttribute>();
            foreach (var attribute in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!mappings.TryGetValue(attribute.Key, out var mapping)) { attributes++; continue; }
                // Retain invalid-field evidence without retaining its arbitrary content. Empty is never valid
                // for either mapped type, so subsequent normalization cannot silently upgrade it to missing.
                var valid = mapping.Count ? attribute.Value is OtlpValue.Integer { Value: >= 0 }
                    : attribute.Value is OtlpValue.Text text && !string.IsNullOrWhiteSpace(text.Value);
                if (!valid) invalid++;
                output.Add(valid ? attribute : attribute with { Value = new OtlpValue.Empty() });
            }
            return output.ToImmutable();
        }
    }
}
