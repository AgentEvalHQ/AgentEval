// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.Models;

/// <summary>Controls whether normalized retrieval evidence is captured in benchmark results.</summary>
public enum EvidenceCaptureMode
{
    /// <summary>Do not inspect agent evidence properties or serialize evidence.</summary>
    None,

    /// <summary>Capture bounded, content-free references and derived diagnostics.</summary>
    References,

    /// <summary>Capture references and bounded evidence content with explicit privacy implications.</summary>
    Full
}

/// <summary>Describes whether evidence instrumentation was observed and valid.</summary>
public enum EvidenceObservationStatus
{
    /// <summary>No normalized evidence was supplied.</summary>
    NotObserved,

    /// <summary>Normalized evidence was supplied and validated.</summary>
    Observed,

    /// <summary>Evidence was supplied but rejected by the safe schema or bounds.</summary>
    Invalid
}

/// <summary>
/// Adapter-supplied, benchmark-neutral evidence references. This DTO must not contain
/// evaluator-side gold labels.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QuestionEvidenceEnvelope
{
    /// <summary>Supported schema version.</summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>
    /// Reserved <c>AgentResponse.AdditionalProperties</c> key used by adapters.
    /// </summary>
    public const string AdditionalPropertiesKey = "agenteval.question_evidence.v1";

    /// <summary>Maximum references accepted in either list.</summary>
    public const int MaximumReferences = 100;

    /// <summary>Maximum total evidence-content characters in Full mode.</summary>
    public const int MaximumTotalContentLength = 32_768;

    /// <summary>Maximum serialized JSON characters accepted from an adapter.</summary>
    public const int MaximumSerializedLength = 262_144;

    /// <summary>Envelope schema version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>References returned by retrieval, in retrieval-rank semantics.</summary>
    public IReadOnlyList<EvidenceReference> Retrieved { get; init; } = [];

    /// <summary>References actually supplied to the answer model, in prompt-order semantics.</summary>
    public IReadOnlyList<EvidenceReference> AnswerContext { get; init; } = [];
}

/// <summary>A normalized reference to one retrieved or answer-context item.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EvidenceReference
{
    /// <summary>Maximum identifier length after trimming.</summary>
    public const int MaximumIdLength = 256;

    /// <summary>Maximum content length for one item in Full mode.</summary>
    public const int MaximumContentLength = 4_096;

    /// <summary>Stable adapter-owned reference identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Positive, list-unique retrieval rank.</summary>
    public required int Rank { get; init; }

    /// <summary>Optional finite retrieval similarity.</summary>
    public double? SimilarityScore { get; init; }

    /// <summary>Optional stable source-session identifier.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Optional zero-based turn index within the source session.</summary>
    public int? SourceTurnIndex { get; init; }

    /// <summary>Optional adapter-reported source timestamp.</summary>
    public DateTimeOffset? SourceTimestamp { get; init; }

    /// <summary>Optional positive, list-unique order used in the answer model context.</summary>
    public int? AnswerContextOrder { get; init; }

    /// <summary>Optional bounded evidence text, accepted only in Full mode.</summary>
    public string? Content { get; init; }
}

/// <summary>
/// Evaluator-derived diagnostics. Gold identifiers and turn labels are never copied here.
/// Nullable fields mean the adapter did not provide enough instrumentation to observe them.
/// </summary>
public sealed class QuestionEvidenceDiagnostics
{
    /// <summary>Evidence observation state.</summary>
    public required EvidenceObservationStatus Status { get; init; }

    /// <summary>Owned safe failure code when supplied evidence was rejected.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SafeFailureCode { get; init; }

    /// <summary>Number of validated retrieved references.</summary>
    public int RetrievedReferenceCount { get; init; }

    /// <summary>Number of validated answer-context references.</summary>
    public int AnswerContextReferenceCount { get; init; }

    /// <summary>Whether a gold evidence session appeared within configured top K.</summary>
    public bool? GoldSessionPresent { get; init; }

    /// <summary>Whether a referenced session/turn pair mapped to a has-answer turn.</summary>
    public bool? HasAnswerTurnPresent { get; init; }

    /// <summary>First retrieval rank containing a gold evidence session.</summary>
    public int? FirstGoldRank { get; init; }

    /// <summary>Distinct source-session count among retrieved references.</summary>
    public int? DistinctSourceSessionCount { get; init; }

    /// <summary>Distinct source sessions divided by references carrying a session ID.</summary>
    public double? SourceSessionDiversityRatio { get; init; }

    /// <summary>Adapter-reported answer-context orders in the exact supplied list order.</summary>
    public IReadOnlyList<int> AnswerContextOrders { get; init; } = [];

    /// <summary>Answer-context references carrying a source timestamp.</summary>
    public int AnswerContextTimestampCount { get; init; }
}
