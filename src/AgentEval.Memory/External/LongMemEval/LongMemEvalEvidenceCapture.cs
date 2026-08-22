// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Buffers;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Validates the single normalized adapter-evidence property and derives evaluator-side
/// diagnostics after answer generation. It never inspects arbitrary provider properties.
/// </summary>
internal static class LongMemEvalEvidenceCapture
{
    internal const string ReservedPropertyKey =
        QuestionEvidenceEnvelope.AdditionalPropertiesKey;

    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static EvidenceCaptureResult Capture(
        AgentResponse response,
        LongMemEvalEntry entry,
        ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        // This early return is a privacy and performance invariant: None mode must not
        // enumerate, index, or otherwise inspect AdditionalProperties.
        if (options.EvidenceCaptureMode == EvidenceCaptureMode.None)
            return new EvidenceCaptureResult(null, null);

        var properties = response.AdditionalProperties;
        if (properties is null)
            return NotObserved();

        try
        {
            if (!properties.TryGetValue(ReservedPropertyKey, out var raw) || raw is null)
                return NotObserved();

            var supplied = DeserializeSupportedValue(raw);
            if (supplied is null)
                return Invalid("invalid_evidence_schema");

            var validation = ValidateAndCopy(supplied, options.EvidenceCaptureMode);
            if (validation.Envelope is null)
                return Invalid(validation.SafeFailureCode!);

            return new EvidenceCaptureResult(
                validation.Envelope,
                DeriveDiagnostics(validation.Envelope, entry, options.EvidenceTopK));
        }
        catch (JsonException)
        {
            return Invalid("invalid_evidence_schema");
        }
        catch (NotSupportedException)
        {
            return Invalid("invalid_evidence_schema");
        }
        catch (Exception)
        {
            // Evidence is advisory and may be supplied by untrusted adapters. Its failure
            // cannot change answer quality or persist arbitrary exception details.
            return Invalid("invalid_evidence");
        }
    }

    private static QuestionEvidenceEnvelope? DeserializeSupportedValue(object raw) => raw switch
    {
        QuestionEvidenceEnvelope envelope => envelope,
        string json when json.Length <= QuestionEvidenceEnvelope.MaximumSerializedLength =>
            JsonSerializer.Deserialize<QuestionEvidenceEnvelope>(json, StrictJsonOptions),
        JsonElement element => DeserializeBounded(element),
        _ => null
    };

    private static QuestionEvidenceEnvelope? DeserializeBounded(JsonElement element)
    {
        var buffer = new BoundedJsonBufferWriter(
            QuestionEvidenceEnvelope.MaximumSerializedLength);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }

        return JsonSerializer.Deserialize<QuestionEvidenceEnvelope>(
            buffer.WrittenSpan,
            StrictJsonOptions);
    }

    private static EvidenceValidationResult ValidateAndCopy(
        QuestionEvidenceEnvelope source,
        EvidenceCaptureMode mode)
    {
        if (!string.Equals(
                source.SchemaVersion,
                QuestionEvidenceEnvelope.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return EvidenceValidationResult.Invalid("unsupported_evidence_schema");
        }

        if (source.Retrieved is null || source.AnswerContext is null)
            return EvidenceValidationResult.Invalid("invalid_evidence_schema");
        if (source.Retrieved.Count > QuestionEvidenceEnvelope.MaximumReferences ||
            source.AnswerContext.Count > QuestionEvidenceEnvelope.MaximumReferences)
        {
            return EvidenceValidationResult.Invalid("evidence_bounds_exceeded");
        }

        var totalContentLength = 0;
        var retrieved = CopyList(source.Retrieved, mode, ref totalContentLength);
        if (retrieved.References is null)
            return EvidenceValidationResult.Invalid(retrieved.SafeFailureCode!);

        var answerContext = CopyList(source.AnswerContext, mode, ref totalContentLength);
        if (answerContext.References is null)
            return EvidenceValidationResult.Invalid(answerContext.SafeFailureCode!);

        if (totalContentLength > QuestionEvidenceEnvelope.MaximumTotalContentLength)
            return EvidenceValidationResult.Invalid("evidence_bounds_exceeded");

        return EvidenceValidationResult.Valid(new QuestionEvidenceEnvelope
        {
            SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
            Retrieved = retrieved.References,
            AnswerContext = answerContext.References
        });
    }

    private static ReferenceListValidationResult CopyList(
        IReadOnlyList<EvidenceReference> source,
        EvidenceCaptureMode mode,
        ref int totalContentLength)
    {
        var ranks = new HashSet<int>();
        var contextOrders = new HashSet<int>();
        var copy = new List<EvidenceReference>(source.Count);

        foreach (var item in source)
        {
            if (item is null)
                return ReferenceListValidationResult.Invalid("invalid_evidence_schema");
            if (item.Rank <= 0 || !ranks.Add(item.Rank))
                return ReferenceListValidationResult.Invalid("invalid_evidence_rank");
            if (item.AnswerContextOrder is { } contextOrder &&
                (contextOrder <= 0 || !contextOrders.Add(contextOrder)))
            {
                return ReferenceListValidationResult.Invalid("invalid_evidence_context_order");
            }
            if (item.SourceTurnIndex is < 0)
                return ReferenceListValidationResult.Invalid("invalid_evidence_turn");
            if (item.SimilarityScore is { } score && !double.IsFinite(score))
                return ReferenceListValidationResult.Invalid("invalid_evidence_score");

            var id = NormalizeId(item.Id);
            var sessionId = item.SourceSessionId is null ? null : NormalizeId(item.SourceSessionId);
            if (id is null || item.SourceSessionId is not null && sessionId is null)
                return ReferenceListValidationResult.Invalid("invalid_evidence_id");
            if (LooksSensitive(id) || sessionId is not null && LooksSensitive(sessionId))
                return ReferenceListValidationResult.Invalid("sensitive_evidence_rejected");

            string? content = null;
            if (item.Content is not null)
            {
                if (mode != EvidenceCaptureMode.Full)
                    return ReferenceListValidationResult.Invalid("evidence_content_not_allowed");
                if (item.Content.Length > EvidenceReference.MaximumContentLength)
                    return ReferenceListValidationResult.Invalid("evidence_bounds_exceeded");
                if (ContainsUnsafeControl(item.Content) || LooksLikeCredential(item.Content))
                    return ReferenceListValidationResult.Invalid("sensitive_evidence_rejected");
                totalContentLength += item.Content.Length;
                content = item.Content;
            }

            copy.Add(new EvidenceReference
            {
                Id = id,
                Rank = item.Rank,
                SimilarityScore = item.SimilarityScore,
                SourceSessionId = sessionId,
                SourceTurnIndex = item.SourceTurnIndex,
                SourceTimestamp = item.SourceTimestamp,
                AnswerContextOrder = item.AnswerContextOrder,
                Content = content
            });
        }

        return ReferenceListValidationResult.Valid(copy);
    }

    private static string? NormalizeId(string? value)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > EvidenceReference.MaximumIdLength)
            return null;
        return trimmed.Any(char.IsControl) ? null : trimmed;
    }

    private static bool LooksSensitive(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("api_key", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("client_secret", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("bearer ", StringComparison.Ordinal) ||
               normalized.Contains("embedding", StringComparison.Ordinal) ||
               normalized.Contains("vector", StringComparison.Ordinal) ||
               normalized.Contains("exception", StringComparison.Ordinal) ||
               normalized.Contains("provider_request", StringComparison.Ordinal) ||
               normalized.Contains("request_header", StringComparison.Ordinal);
    }

    private static bool LooksLikeCredential(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("authorization:", StringComparison.Ordinal) ||
               normalized.Contains("bearer ", StringComparison.Ordinal) ||
               normalized.Contains("api_key=", StringComparison.Ordinal) ||
               normalized.Contains("apikey=", StringComparison.Ordinal) ||
               normalized.Contains("client_secret=", StringComparison.Ordinal) ||
               normalized.Contains("password=", StringComparison.Ordinal);
    }

    private static bool ContainsUnsafeControl(string value) =>
        value.Any(character => char.IsControl(character) &&
                               character is not '\r' and not '\n' and not '\t');

    private static QuestionEvidenceDiagnostics DeriveDiagnostics(
        QuestionEvidenceEnvelope envelope,
        LongMemEvalEntry entry,
        int topK)
    {
        var sourceReferences = envelope.Retrieved
            .Where(reference => reference.SourceSessionId is not null)
            .ToList();
        var goldSessionIds = new HashSet<string>(
            entry.AnswerSessionIds ?? [],
            StringComparer.Ordinal);
        var canObserveSessions = sourceReferences.Count > 0;
        bool? goldSessionPresent = canObserveSessions
            ? sourceReferences.Any(reference =>
                reference.Rank <= topK &&
                goldSessionIds.Contains(reference.SourceSessionId!))
            : null;
        var firstGoldRank = canObserveSessions
            ? sourceReferences
                .Where(reference => goldSessionIds.Contains(reference.SourceSessionId!))
                .Select(reference => (int?)reference.Rank)
                .Min()
            : null;

        // Required-evidence COVERAGE, as counts rather than an any-check.
        //
        // Every other gold diagnostic here is an Any over Retrieved, which is adequate only when one
        // session carries the answer. It cannot distinguish one required session of four from four
        // of four, so a multi-session question reads healthy the moment anything gold is ranked.
        var requiredCount = goldSessionIds.Count > 0 ? goldSessionIds.Count : (int?)null;
        var requiredRetrieved = canObserveSessions
            ? sourceReferences
                .Select(reference => reference.SourceSessionId!)
                .Where(goldSessionIds.Contains)
                .Distinct(StringComparer.Ordinal)
                .Count()
            : (int?)null;

        // Measured at the ANSWER-CONTEXT boundary, which is the point of it. Retrieval can rank
        // every required session highly while a downstream context budget drops most of them before
        // the model sees anything -- a failure that is invisible to every retrieval-side check above
        // and shows up here as a gap against requiredRetrieved.
        //
        // Observability is decided independently of the retrieval lists: an adapter can instrument
        // one boundary and not the other, and inheriting canObserveSessions here would report a
        // confident zero for an adapter that simply never labelled its answer context.
        var answerContextSessions = envelope.AnswerContext
            .Where(reference => reference.SourceSessionId is not null)
            .ToList();
        var requiredInAnswerContext = answerContextSessions.Count > 0
            ? answerContextSessions
                .Select(reference => reference.SourceSessionId!)
                .Where(goldSessionIds.Contains)
                .Distinct(StringComparer.Ordinal)
                .Count()
            : (int?)null;

        var sessionIndex = (entry.HaystackSessionIds ?? [])
            .Select((id, index) => (id, index))
            .GroupBy(pair => pair.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var observedTurnLabels = new List<bool>();
        foreach (var reference in sourceReferences)
        {
            if (reference.SourceTurnIndex is not { } turnIndex ||
                !sessionIndex.TryGetValue(reference.SourceSessionId!, out var sourceSessionIndex) ||
                entry.HaystackSessions is null ||
                sourceSessionIndex >= entry.HaystackSessions.Count ||
                turnIndex >= entry.HaystackSessions[sourceSessionIndex].Count)
            {
                continue;
            }

            var hasAnswer = entry.HaystackSessions[sourceSessionIndex][turnIndex].HasAnswer;
            if (hasAnswer.HasValue)
                observedTurnLabels.Add(hasAnswer.Value);
        }

        var distinctSessions = sourceReferences
            .Select(reference => reference.SourceSessionId!)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new QuestionEvidenceDiagnostics
        {
            Status = EvidenceObservationStatus.Observed,
            RetrievedReferenceCount = envelope.Retrieved.Count,
            AnswerContextReferenceCount = envelope.AnswerContext.Count,
            GoldSessionPresent = goldSessionPresent,
            HasAnswerTurnPresent = observedTurnLabels.Count > 0
                ? observedTurnLabels.Any(value => value)
                : null,
            FirstGoldRank = firstGoldRank,
            RequiredEvidenceSessionCount = requiredCount,
            RequiredEvidenceSessionsRetrieved = requiredRetrieved,
            RequiredEvidenceSessionsInAnswerContext = requiredInAnswerContext,
            DistinctSourceSessionCount = canObserveSessions ? distinctSessions : null,
            SourceSessionDiversityRatio = canObserveSessions
                ? (double)distinctSessions / sourceReferences.Count
                : null,
            AnswerContextOrders = envelope.AnswerContext
                .Select(reference => reference.AnswerContextOrder)
                .Where(order => order.HasValue)
                .Select(order => order!.Value)
                .ToArray(),
            AnswerContextTimestampCount = envelope.AnswerContext.Count(reference =>
                reference.SourceTimestamp.HasValue)
        };
    }

    private static EvidenceCaptureResult NotObserved() => new(
        null,
        new QuestionEvidenceDiagnostics
        {
            Status = EvidenceObservationStatus.NotObserved
        });

    private static EvidenceCaptureResult Invalid(string safeFailureCode) => new(
        null,
        new QuestionEvidenceDiagnostics
        {
            Status = EvidenceObservationStatus.Invalid,
            SafeFailureCode = safeFailureCode
        });

    internal sealed record EvidenceCaptureResult(
        QuestionEvidenceEnvelope? Envelope,
        QuestionEvidenceDiagnostics? Diagnostics);

    private sealed class BoundedJsonBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer;
        private int _written;

        internal BoundedJsonBufferWriter(int maximumLength)
        {
            _buffer = new byte[maximumLength];
        }

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written)
                throw new JsonException("Evidence JSON exceeds the configured bound.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureAvailable(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureAvailable(int sizeHint)
        {
            if (sizeHint < 0)
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            var required = Math.Max(sizeHint, 1);
            if (required > _buffer.Length - _written)
                throw new JsonException("Evidence JSON exceeds the configured bound.");
        }
    }
    private sealed record EvidenceValidationResult(
        QuestionEvidenceEnvelope? Envelope,
        string? SafeFailureCode)
    {
        internal static EvidenceValidationResult Valid(QuestionEvidenceEnvelope envelope) =>
            new(envelope, null);

        internal static EvidenceValidationResult Invalid(string safeFailureCode) =>
            new(null, safeFailureCode);
    }

    private sealed record ReferenceListValidationResult(
        IReadOnlyList<EvidenceReference>? References,
        string? SafeFailureCode)
    {
        internal static ReferenceListValidationResult Valid(
            IReadOnlyList<EvidenceReference> references) => new(references, null);

        internal static ReferenceListValidationResult Invalid(string safeFailureCode) =>
            new(null, safeFailureCode);
    }
}
