// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentEval.Results.Observations;

/// <summary>The evidence used to select an observation group; none implies telemetry completeness.</summary>
public enum OtlpGroupingBasis { Session, TraceMissingSession, TraceInvalidSession, TraceConflictingSession, TraceMissingService, TraceInvalidService }
/// <summary>A reported span and the reason for its grouping. Duplicate reports are retained.</summary>
public sealed record OtlpGroupedSpan(OtlpNormalizedSpan Span, OtlpGroupingBasis Basis);
/// <summary>
/// A source-scoped observation group. Id is a grouping digest, never an AEF run hash, seal or verdict.
/// Members retain their resource/scope/span positions for joining back to the filtered batch.
/// </summary>
public sealed record OtlpObservedSession(string Id, string Revision, string Source,
    ImmutableArray<OtlpGroupedSpan> Members);
/// <summary>Groups decoded, content-filtered observations using explicit identities.</summary>
public interface IOtlpSessionGrouper
{
    /// <summary>Source is a stable receiver-configured namespace, never a request-supplied tenant authority.</summary>
    ImmutableArray<OtlpObservedSession> Group(string source, OtlpTraceBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>
/// Groups by source, resource service identity and a valid reported session. Falls back to trace identity
/// when service name or session consensus is unavailable. Does not infer sessions from span names or links.
/// </summary>
public sealed class OtlpSessionGrouper : IOtlpSessionGrouper
{
    /// <summary>Version of the grouping identity serialization and fallback rules.</summary>
    public const string Revision = "otlp-grouping/1";
    private static readonly string[] ServiceFields = ["service.namespace", "service.name", "service.instance.id"];
    private readonly IOtlpTraceNormalizer _normalizer;

    /// <summary>Creates a grouper using the configured bounded normalizer.</summary>
    public OtlpSessionGrouper(IOtlpTraceNormalizer normalizer) =>
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));

    /// <inheritdoc />
    public ImmutableArray<OtlpObservedSession> Group(string source, OtlpTraceBatch batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > 256)
            throw new ArgumentException("Expected: a nonempty source namespace of at most 256 UTF-8 bytes. Actual: invalid source. " +
                "Suggestions: configure a stable receiver source. Because: observations must not merge across independent sources.", nameof(source));
        var groups = new Dictionary<string, List<OtlpGroupedSpan>>(StringComparer.Ordinal);
        foreach (var span in _normalizer.Normalize(batch, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = span.Field("session.id");
            var basis = session.State switch
            {
                OtlpFieldState.Missing => OtlpGroupingBasis.TraceMissingSession,
                OtlpFieldState.Invalid => OtlpGroupingBasis.TraceInvalidSession,
                OtlpFieldState.Conflict => OtlpGroupingBasis.TraceConflictingSession,
                _ => ServiceFields.Any(name => span.Field(name).State is OtlpFieldState.Invalid or OtlpFieldState.Conflict)
                    ? OtlpGroupingBasis.TraceInvalidService
                    : span.Field("service.name").State == OtlpFieldState.Present
                        ? OtlpGroupingBasis.Session : OtlpGroupingBasis.TraceMissingService
            };
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(Revision);
                writer.WriteStringValue(source);
                // Use explicit presence/state markers, not concatenated delimiters or placeholder names.
                foreach (var fieldName in ServiceFields)
                {
                    var field = span.Field(fieldName);
                    writer.WriteStartArray();
                    writer.WriteNumberValue((int)field.State);
                    if (field.Value is OtlpValue.Text text) writer.WriteStringValue(text.Value);
                    else writer.WriteNullValue();
                    writer.WriteEndArray();
                }
                writer.WriteStringValue(basis == OtlpGroupingBasis.Session ? "session" : "trace");
                writer.WriteStringValue(basis == OtlpGroupingBasis.Session ? ((OtlpValue.Text)session.Value!).Value : span.TraceId);
                writer.WriteEndArray();
            }
            var id = "obs-" + Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))).ToLowerInvariant();
            if (!groups.TryGetValue(id, out var members)) groups[id] = members = [];
            members.Add(new(span, basis));
        }
        var result = ImmutableArray.CreateBuilder<OtlpObservedSession>();
        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new(group.Key, Revision, source, group.Value
                .OrderBy(m => m.Span.StartTimeUnixNano).ThenBy(m => m.Span.TraceId, StringComparer.Ordinal)
                .ThenBy(m => m.Span.SpanId, StringComparer.Ordinal).ThenBy(m => m.Span.ResourceIndex)
                .ThenBy(m => m.Span.ScopeIndex).ThenBy(m => m.Span.SpanIndex).ToImmutableArray()));
        }
        return result.ToImmutable();
    }
}
