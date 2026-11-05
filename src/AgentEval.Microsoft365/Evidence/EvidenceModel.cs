// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;

namespace AgentEval.Microsoft365.Evidence;

// The typed evidence contract of the Microsoft 365 (Work IQ A2A) provider, schema
// `agenteval.microsoft365.turn-evidence/v1`. Every enum and record below is the design's
// "Proposed typed evidence contract" verbatim; the only additions are documentation, the validated
// factory helpers on Observed<T> / EvidenceSet<T>, and small read-only convenience members.
// The invariants these types enforce: a missing channel is never an empty success, a derived value
// always names its rule, "complete" always names its basis, and nothing substitutes a default for
// an absent value.

/// <summary>Why a collection of evidence is, or is not, available for a turn.</summary>
public enum EvidenceAvailability
{
    /// <summary>The user or the diagnostic profile deliberately disabled capture of this channel.</summary>
    Disabled,

    /// <summary>The adapter did not request this channel (for example under <see cref="DiagnosticProfile.PublicAnswerOnly"/>).</summary>
    NotRequested,

    /// <summary>The channel was requested but the provider returned nothing for it.</summary>
    NotProvided,

    /// <summary>The provider explicitly declared the channel unsupported (capability/schema/version evidence); silence never maps here.</summary>
    Unsupported,

    /// <summary>The provider returned data for this channel; see <see cref="EvidenceValidation"/> for whether it could be interpreted.</summary>
    Provided,

    /// <summary>Local policy removed data that would otherwise have been available; the removed content is not retained.</summary>
    Redacted,
}

/// <summary>Whether a received representation could be interpreted.</summary>
public enum EvidenceValidation
{
    /// <summary>No validation applies because nothing was provided.</summary>
    NotApplicable,

    /// <summary>Every item was interpreted as declared.</summary>
    Valid,

    /// <summary>Some items were interpreted; others were malformed or incomplete and are listed as issues.</summary>
    Partial,

    /// <summary>The representation could not be interpreted at all.</summary>
    Malformed,

    /// <summary>Two or more candidate representations disagree with no declared relationship; nothing was chosen.</summary>
    Conflicting,
}

/// <summary>Whether all observations in the declared scope are represented.</summary>
public enum EvidenceCoverage
{
    /// <summary>Coverage is not known; absence of an item is not evidence of absence.</summary>
    Unknown,

    /// <summary>Some observations are known to be missing (for example an evidence gap after a recovered stream).</summary>
    Partial,

    /// <summary>All observations in the declared scope are represented; requires a <see cref="CoverageBasis"/>.</summary>
    CompleteForDeclaredScope,
}

/// <summary>Which boundary the provider or capture contract establishes for a set of evidence.</summary>
public enum EvidenceScope
{
    /// <summary>The scope was not declared.</summary>
    Unknown,

    /// <summary>One provider response.</summary>
    Response,

    /// <summary>One client request.</summary>
    Request,

    /// <summary>One A2A task.</summary>
    Task,

    /// <summary>One evaluated turn (client identity).</summary>
    Turn,

    /// <summary>The whole conversation context.</summary>
    Conversation,
}

/// <summary>Field-level representation state, independent of the value.</summary>
public enum FieldState
{
    /// <summary>The field was absent from the representation.</summary>
    Missing,

    /// <summary>The provider sent an explicit null.</summary>
    Null,

    /// <summary>A correctly typed value is present.</summary>
    Present,

    /// <summary>A value was sent but has the wrong type or shape; no substitute is exposed.</summary>
    Invalid,

    /// <summary>Local policy removed the value.</summary>
    Redacted,
}

/// <summary>Whether retained material can reproduce the normalisation.</summary>
public enum CaptureState
{
    /// <summary>Every byte/frame that fed the normalisation was retained.</summary>
    Complete,

    /// <summary>The capture stopped before the operation ended (connection loss, local cancellation).</summary>
    Interrupted,

    /// <summary>A size or frame bound cut the capture; truncation is never natural end-of-result.</summary>
    Truncated,

    /// <summary>Policy retained metadata only.</summary>
    NotRetained,
}

/// <summary>How a normalised value was obtained; non-direct values carry a <c>DerivationRuleId</c>.</summary>
public enum EvidenceDerivation
{
    /// <summary>A field the provider sent, copied as is.</summary>
    ProviderValue,

    /// <summary>A parser projection of provider content (for example a citation marker parsed from text).</summary>
    ParserProjection,

    /// <summary>A configured alias or manifest mapping.</summary>
    ConfiguredMapping,

    /// <summary>A display heuristic (for example an ordinal citation-to-source join); never metric truth by default.</summary>
    Heuristic,

    /// <summary>A measurement taken by this client (for example wall-clock time).</summary>
    ClientMeasurement,
}

/// <summary>What supports a <see cref="EvidenceCoverage.CompleteForDeclaredScope"/> claim.</summary>
public enum CoverageBasisKind
{
    /// <summary>The provider declared completeness in the response.</summary>
    ProviderDeclaration,

    /// <summary>A documented provider contract declares completeness for this scope.</summary>
    DocumentedContract,

    /// <summary>A configured assumption; visible, and never sufficient for strict absence metrics.</summary>
    ConfiguredAssumption,
}

/// <summary>Where captured material came from.</summary>
public enum EvidenceOrigin
{
    /// <summary>An A2A message (direct reply or status message).</summary>
    A2AMessage,

    /// <summary>An A2A artifact (answer or diagnostic).</summary>
    A2AArtifact,

    /// <summary>Imported from a native evaluator run; losses are marked.</summary>
    NativeEvaluationImport,

    /// <summary>Telemetry from instrumentation this process owns.</summary>
    OwnedToolTelemetry,

    /// <summary>A marker parsed from answer text.</summary>
    TextMarker,

    /// <summary>A configured manifest mapping.</summary>
    ConfiguredManifestMapping,
}

/// <summary>Transport of a downstream tool, only when explicit provenance says so.</summary>
public enum ToolTransport
{
    /// <summary>Not stated by the provider.</summary>
    Unknown,

    /// <summary>Model Context Protocol server tool (observed, never inferred from a friendly name).</summary>
    Mcp,

    /// <summary>OpenAPI plugin.</summary>
    OpenApi,

    /// <summary>Another explicitly named transport.</summary>
    Other,
}

/// <summary>Business outcome of an execution as the provider reported it; an HTTP 200 alone is not <see cref="Succeeded"/>.</summary>
public enum ExecutionOutcome
{
    /// <summary>The provider did not report an outcome.</summary>
    Unknown,

    /// <summary>The provider reported success.</summary>
    Succeeded,

    /// <summary>The provider reported failure.</summary>
    Failed,
}

/// <summary>How a citation marker is bound to a citation source.</summary>
public enum CitationBinding
{
    /// <summary>No binding was established; the marker is preserved as is.</summary>
    Unresolved,

    /// <summary>The provider supplied an explicit reference key shared by marker and source.</summary>
    ExplicitProviderKey,

    /// <summary>Marker and source share an explicit reference URI.</summary>
    ExplicitReferenceUri,

    /// <summary>An ordinal/positional heuristic, for display only.</summary>
    HeuristicDisplayOnly,

    /// <summary>Two candidate bindings disagree.</summary>
    Conflicting,
}

/// <summary>Whether a streamed text chunk is a delta or a snapshot of the whole artifact.</summary>
public enum StreamTextSemantics
{
    /// <summary>Not declared by the protocol event.</summary>
    Unknown,

    /// <summary>The chunk appends to the artifact (<c>append: true</c>).</summary>
    Delta,

    /// <summary>The chunk replaces the artifact's content.</summary>
    Snapshot,
}

/// <summary>
/// A pointer into captured material: which capture, which event, and a JSON pointer inside it. A source
/// reference says where a value was read; it does not say how the normalised value was derived (see
/// <see cref="Observed{T}.Derivation"/>).
/// </summary>
/// <param name="CaptureId">The <see cref="RawCaptureRef.CaptureId"/> of the capture the value was read from.</param>
/// <param name="EventSequence">The client sequence of the event/frame inside that capture (0 for a non-streamed body).</param>
/// <param name="Origin">What kind of material the pointer addresses.</param>
/// <param name="JsonPointer">RFC 6901 JSON pointer inside the event payload.</param>
/// <param name="ArtifactId">The artifact id when the pointer addresses an artifact.</param>
/// <param name="ArtifactRevision">The artifact revision (reducer counter) when applicable.</param>
/// <param name="PartIndex">The part index inside the artifact/message when applicable.</param>
public sealed record SourceRef(
    string CaptureId,
    long EventSequence,
    EvidenceOrigin Origin,
    string JsonPointer,
    string? ArtifactId = null,
    int? ArtifactRevision = null,
    int? PartIndex = null);

/// <summary>A normalisation issue with a stable code and a message that contains no tenant content.</summary>
/// <param name="Code">Stable machine-readable code, for example <c>answer_ambiguous</c> or <c>malformed_rank</c>.</param>
/// <param name="SafeMessage">Human-readable summary without prompts, answers, URLs, passages or secrets.</param>
/// <param name="Sources">Where the issue was observed.</param>
public sealed record EvidenceIssue(
    string Code,
    string SafeMessage,
    ImmutableArray<SourceRef> Sources);

/// <summary>
/// A field-level observation: the representation state, the value (meaningful only when
/// <see cref="State"/> is <see cref="FieldState.Present"/>), where it was read and how it was derived.
/// Prefer the factories (<see cref="Missing"/>, <see cref="Null"/>, <see cref="Present"/>,
/// <see cref="Invalid"/>, <see cref="Redacted"/>); the positional constructor is the design's verbatim
/// shape and does not validate.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="State">The representation state.</param>
/// <param name="Value">The value; <c>default</c> and meaningless unless <paramref name="State"/> is <see cref="FieldState.Present"/>.</param>
/// <param name="Sources">Where the value was read.</param>
/// <param name="Derivation">How the value was obtained.</param>
/// <param name="DerivationRuleId">The versioned rule id; required for every derivation other than <see cref="EvidenceDerivation.ProviderValue"/>.</param>
public sealed record Observed<T>(
    FieldState State,
    T? Value,
    ImmutableArray<SourceRef> Sources,
    EvidenceDerivation Derivation = EvidenceDerivation.ProviderValue,
    string? DerivationRuleId = null)
{
    /// <summary><see langword="true"/> when <see cref="State"/> is <see cref="FieldState.Present"/> and <see cref="Value"/> is meaningful.</summary>
    public bool IsPresent => State == FieldState.Present;

    /// <summary>The field was absent from the representation.</summary>
    public static Observed<T> Missing() => new(FieldState.Missing, default, ImmutableArray<SourceRef>.Empty);

    /// <summary>The provider sent an explicit null.</summary>
    /// <param name="sources">Where the null was read; <c>default</c> means no pointer.</param>
    public static Observed<T> Null(ImmutableArray<SourceRef> sources = default) =>
        new(FieldState.Null, default, NormalizeSources(sources));

    /// <summary>A correctly typed value is present.</summary>
    /// <param name="value">The value; a <see cref="JsonElement"/> is cloned so it never references a disposed document.</param>
    /// <param name="sources">Where the value was read.</param>
    /// <param name="derivation">How the value was obtained.</param>
    /// <param name="ruleId">The versioned rule id; required unless <paramref name="derivation"/> is <see cref="EvidenceDerivation.ProviderValue"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null — use <see cref="Null"/> for an explicit provider null.</exception>
    /// <exception cref="ArgumentException">A non-direct derivation has no rule id, or <paramref name="value"/> is an undefined <see cref="JsonElement"/>.</exception>
    public static Observed<T> Present(
        T value,
        ImmutableArray<SourceRef> sources,
        EvidenceDerivation derivation = EvidenceDerivation.ProviderValue,
        string? ruleId = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), "A Present observation needs a value; use Observed<T>.Null() for an explicit provider null.");
        }

        if (derivation != EvidenceDerivation.ProviderValue && string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException(
                $"A value derived by {derivation} must name its versioned DerivationRuleId; only ProviderValue may omit it.",
                nameof(ruleId));
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException("An undefined JsonElement is not a present value.", nameof(value));
            }

            value = (T)(object)element.Clone();
        }

        return new(FieldState.Present, value, NormalizeSources(sources), derivation, ruleId);
    }

    /// <summary>A value was sent but has the wrong type or shape; no substitute is exposed.</summary>
    /// <param name="sources">Where the invalid value was read.</param>
    public static Observed<T> Invalid(ImmutableArray<SourceRef> sources) =>
        new(FieldState.Invalid, default, NormalizeSources(sources));

    /// <summary>Local policy removed the value; the removed content is not retained.</summary>
    /// <param name="sources">Where the value would have been read; <c>default</c> means no pointer.</param>
    public static Observed<T> Redacted(ImmutableArray<SourceRef> sources = default) =>
        new(FieldState.Redacted, default, NormalizeSources(sources));

    private static ImmutableArray<SourceRef> NormalizeSources(ImmutableArray<SourceRef> sources) =>
        sources.IsDefault ? ImmutableArray<SourceRef>.Empty : sources;
}

/// <summary>What supports a <see cref="EvidenceCoverage.CompleteForDeclaredScope"/> claim.</summary>
/// <param name="Kind">The kind of basis.</param>
/// <param name="Claim">The exact claim, for example "all tool executions of this task are listed".</param>
/// <param name="RuleId">The versioned profile/rule id that made the claim.</param>
/// <param name="Sources">The raw source that supports the claim.</param>
/// <param name="ReferenceUri">Documentation reference when the basis is a documented contract.</param>
public sealed record CoverageBasis(
    CoverageBasisKind Kind,
    string Claim,
    string RuleId,
    ImmutableArray<SourceRef> Sources,
    string? ReferenceUri = null);

/// <summary>
/// A collection of evidence with orthogonal availability, validation, coverage and scope. Prefer the
/// factories; the positional constructor is the design's verbatim shape and does not validate.
/// </summary>
/// <typeparam name="T">The observation type.</typeparam>
/// <param name="Availability">Why the collection is or is not available.</param>
/// <param name="Validation">Whether the received representation could be interpreted.</param>
/// <param name="Coverage">Whether all observations in <paramref name="Scope"/> are represented.</param>
/// <param name="Scope">The boundary the provider or capture contract establishes.</param>
/// <param name="Items">The interpreted observations (empty unless <paramref name="Availability"/> is <see cref="EvidenceAvailability.Provided"/>).</param>
/// <param name="Sources">Where the collection was read.</param>
/// <param name="Issues">Interpretation issues.</param>
/// <param name="CoverageBasis">Required when <paramref name="Coverage"/> is <see cref="EvidenceCoverage.CompleteForDeclaredScope"/>.</param>
public sealed record EvidenceSet<T>(
    EvidenceAvailability Availability,
    EvidenceValidation Validation,
    EvidenceCoverage Coverage,
    EvidenceScope Scope,
    ImmutableArray<T> Items,
    ImmutableArray<SourceRef> Sources,
    ImmutableArray<EvidenceIssue> Issues,
    CoverageBasis? CoverageBasis = null)
{
    /// <summary><see langword="true"/> when the provider returned data for this channel.</summary>
    public bool IsProvided => Availability == EvidenceAvailability.Provided;

    /// <summary>
    /// The derived "observed empty" condition: a provided, valid, empty collection for the declared scope. It
    /// means zero rows in this diagnostic snapshot — not that the service did nothing internally.
    /// </summary>
    public bool IsObservedEmpty => IsProvided && Validation == EvidenceValidation.Valid && Items.IsDefaultOrEmpty;

    /// <summary>The adapter did not request this channel.</summary>
    /// <param name="scope">The scope the channel would have had.</param>
    public static EvidenceSet<T> NotRequested(EvidenceScope scope) =>
        Unavailable(EvidenceAvailability.NotRequested, scope, default);

    /// <summary>The channel was requested but the provider returned nothing for it.</summary>
    /// <param name="scope">The declared scope.</param>
    /// <param name="sources">Where the absence was observed (for example the terminal status message).</param>
    public static EvidenceSet<T> NotProvided(EvidenceScope scope, ImmutableArray<SourceRef> sources) =>
        Unavailable(EvidenceAvailability.NotProvided, scope, sources);

    /// <summary>The provider explicitly declared the channel unsupported; at least one source pointer is required because silence is not sufficient.</summary>
    /// <param name="scope">The declared scope.</param>
    /// <param name="sources">The capability/schema/version response that declared it.</param>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
    public static EvidenceSet<T> Unsupported(EvidenceScope scope, ImmutableArray<SourceRef> sources)
    {
        if (sources.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Unsupported requires an explicit capability/schema/version source; silence maps to NotProvided or NotRequested.",
                nameof(sources));
        }

        return Unavailable(EvidenceAvailability.Unsupported, scope, sources);
    }

    /// <summary>The user or profile deliberately disabled this channel.</summary>
    /// <param name="scope">The scope the channel would have had.</param>
    public static EvidenceSet<T> Disabled(EvidenceScope scope) =>
        Unavailable(EvidenceAvailability.Disabled, scope, default);

    /// <summary>Local policy removed this channel; nothing of it is retained.</summary>
    /// <param name="scope">The scope the channel had.</param>
    public static EvidenceSet<T> Redacted(EvidenceScope scope) =>
        Unavailable(EvidenceAvailability.Redacted, scope, default);

    /// <summary>The provider returned data for this channel.</summary>
    /// <param name="items">The interpreted observations (may be empty — see <see cref="IsObservedEmpty"/>).</param>
    /// <param name="validation">Whether the representation could be interpreted; never <see cref="EvidenceValidation.NotApplicable"/>.</param>
    /// <param name="coverage">Coverage of the declared scope.</param>
    /// <param name="scope">The declared scope.</param>
    /// <param name="sources">Where the collection was read.</param>
    /// <param name="issues">Interpretation issues.</param>
    /// <param name="basis">Required when <paramref name="coverage"/> is <see cref="EvidenceCoverage.CompleteForDeclaredScope"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="validation"/> is <see cref="EvidenceValidation.NotApplicable"/>, or completeness is claimed without a basis.</exception>
    public static EvidenceSet<T> Provided(
        ImmutableArray<T> items,
        EvidenceValidation validation,
        EvidenceCoverage coverage,
        EvidenceScope scope,
        ImmutableArray<SourceRef> sources,
        ImmutableArray<EvidenceIssue> issues = default,
        CoverageBasis? basis = null)
    {
        if (validation == EvidenceValidation.NotApplicable)
        {
            throw new ArgumentException("A provided collection has a validation result; NotApplicable is for absent channels.", nameof(validation));
        }

        if (coverage == EvidenceCoverage.CompleteForDeclaredScope && basis is null)
        {
            throw new ArgumentException("CompleteForDeclaredScope requires a CoverageBasis stating the exact claim and its provenance.", nameof(basis));
        }

        return new(
            EvidenceAvailability.Provided,
            validation,
            coverage,
            scope,
            items.IsDefault ? ImmutableArray<T>.Empty : items,
            sources.IsDefault ? ImmutableArray<SourceRef>.Empty : sources,
            issues.IsDefault ? ImmutableArray<EvidenceIssue>.Empty : issues,
            basis);
    }

    private static EvidenceSet<T> Unavailable(EvidenceAvailability availability, EvidenceScope scope, ImmutableArray<SourceRef> sources) =>
        new(
            availability,
            EvidenceValidation.NotApplicable,
            EvidenceCoverage.Unknown,
            scope,
            ImmutableArray<T>.Empty,
            sources.IsDefault ? ImmutableArray<SourceRef>.Empty : sources,
            ImmutableArray<EvidenceIssue>.Empty);
}

/// <summary>
/// The identities of one evaluated turn. They are not interchangeable: the RPC request id correlates the
/// initial JSON-RPC send, the client message id is its outbound message, provider ids arrive from the
/// service. Retries/resumptions keep <paramref name="TurnId"/> and <paramref name="TurnIndex"/> and increment
/// <paramref name="AttemptIndex"/>.
/// </summary>
/// <param name="RunId">Local run identity (one per evaluation run).</param>
/// <param name="CaseId">Local benchmark case identity.</param>
/// <param name="TurnId">Stable client turn identity shared with <see cref="WorkIqTurnRequest.TurnId"/>.</param>
/// <param name="TurnIndex">The turn's order inside its scenario (0-based).</param>
/// <param name="AttemptIndex">The attempt number for this turn (0 for the first send).</param>
/// <param name="RpcRequestId">The JSON-RPC id of the initial send; recovery RPC ids stay in the capture.</param>
/// <param name="ClientMessageId">The <c>messageId</c> of the outbound user message.</param>
/// <param name="ProviderMessageId">The inbound reply message id, when any.</param>
/// <param name="ProviderTaskId">The server task id, when a task was created.</param>
/// <param name="ProviderContextId">The server conversation context id, when returned.</param>
public sealed record TurnIdentity(
    string RunId,
    string CaseId,
    string TurnId,
    int TurnIndex,
    int AttemptIndex,
    string RpcRequestId,
    string ClientMessageId,
    string? ProviderMessageId,
    string? ProviderTaskId,
    string? ProviderContextId);

/// <summary>
/// A reference to retained raw material (request/response bytes or SSE frames). A hash establishes
/// correspondence to captured bytes, not server authenticity or completeness; a sanitised capture's hash
/// describes the sanitised capture.
/// </summary>
/// <param name="CaptureId">Stable id of the capture inside the turn.</param>
/// <param name="State">Whether the capture can reproduce the normalisation.</param>
/// <param name="PolicyId">The versioned content/redaction policy that produced it.</param>
/// <param name="ProtectedLocation">Where the bytes were written under <see cref="EvidenceContentPolicy.FullEvidence"/>; null when not retained.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the retained bytes; null when not retained.</param>
/// <param name="ByteCount">Retained byte count; null when not retained.</param>
public sealed record RawCaptureRef(
    string CaptureId,
    CaptureState State,
    string PolicyId,
    string? ProtectedLocation,
    string? Sha256,
    long? ByteCount);

/// <summary>The selected final answer, in raw and display projections.</summary>
/// <param name="RawText">The answer text with its original citation markers.</param>
/// <param name="DisplayText">The answer rendered for display (markers rendered only when resolvable); never alters <paramref name="RawText"/>.</param>
/// <param name="IsFinal">Whether the text is the authoritative final answer (a completed task or direct message).</param>
/// <param name="SelectionPolicyVersion">The versioned answer-selection policy that chose it.</param>
/// <param name="Sources">The artifact/message parts the answer was assembled from.</param>
public sealed record AnswerEvidence(
    Observed<string> RawText,
    Observed<string> DisplayText,
    bool IsFinal,
    string SelectionPolicyVersion,
    ImmutableArray<SourceRef> Sources);

/// <summary>The identity of a downstream tool as reported; identifiers stay opaque and are never normalised by case or punctuation.</summary>
/// <param name="PluginName">The plugin name as reported.</param>
/// <param name="FunctionDisplayName">The function display name as reported (not a stable id).</param>
/// <param name="ProviderToolId">A stable provider tool id, when reported.</param>
/// <param name="CanonicalName">A canonical name from a configured mapping, when one applies.</param>
/// <param name="Transport">The transport, only from explicit provenance.</param>
/// <param name="McpServerIdentity">The MCP server identity, only from explicit provenance.</param>
/// <param name="McpToolName">The MCP tool name, only from explicit provenance.</param>
/// <param name="AliasMappingVersion">The alias mapping version when <paramref name="CanonicalName"/> was mapped.</param>
public sealed record ToolIdentity(
    Observed<string> PluginName,
    Observed<string> FunctionDisplayName,
    Observed<string> ProviderToolId,
    Observed<string> CanonicalName,
    Observed<ToolTransport> Transport,
    Observed<string> McpServerIdentity,
    Observed<string> McpToolName,
    string? AliasMappingVersion);

/// <summary>A reported tool <b>selection</b>; selection is not execution.</summary>
/// <param name="ObservationId">Local evidence id; never a provider call id.</param>
/// <param name="ProviderInvocationId">The provider invocation id, when reported.</param>
/// <param name="Tool">The selected tool.</param>
/// <param name="Sources">Where the selection was read.</param>
public sealed record ToolSelectionObservation(
    string ObservationId,
    Observed<string> ProviderInvocationId,
    ToolIdentity Tool,
    ImmutableArray<SourceRef> Sources);

/// <summary>A reported tool <b>execution</b> row; fields the provider did not send stay <see cref="FieldState.Missing"/>, never a zero or an empty object.</summary>
/// <param name="ObservationId">Local evidence id; never a provider call id.</param>
/// <param name="ProviderInvocationId">The provider invocation id, when reported.</param>
/// <param name="Tool">The executed tool.</param>
/// <param name="Arguments">The arguments, when reported.</param>
/// <param name="Result">The result, when reported.</param>
/// <param name="ResponseStatus">HTTP-style response status (integer only); transport success, not business success.</param>
/// <param name="Outcome">The business outcome as reported.</param>
/// <param name="StartedAt">Provider-reported start time; never an artifact arrival time.</param>
/// <param name="EndedAt">Provider-reported end time.</param>
/// <param name="DurationMilliseconds">Provider-reported duration.</param>
/// <param name="Sources">Where the row was read.</param>
public sealed record ToolExecutionObservation(
    string ObservationId,
    Observed<string> ProviderInvocationId,
    ToolIdentity Tool,
    Observed<JsonElement> Arguments,
    Observed<JsonElement> Result,
    Observed<int> ResponseStatus,
    Observed<ExecutionOutcome> Outcome,
    Observed<DateTimeOffset> StartedAt,
    Observed<DateTimeOffset> EndedAt,
    Observed<double> DurationMilliseconds,
    ImmutableArray<SourceRef> Sources);

/// <summary>A tool invocation <b>proposed</b> for consent; approval is not execution and proposed arguments are not executed arguments.</summary>
/// <param name="ObservationId">Local evidence id.</param>
/// <param name="ProviderInteractionId">The pause's <c>sourceRequestId</c>, when reported.</param>
/// <param name="ProposedProviderInvocationId">The proposed call id, when reported.</param>
/// <param name="Tool">The proposed tool.</param>
/// <param name="ProposedArgumentsRawText">The exact argument string as supplied; never regenerated from the parsed object.</param>
/// <param name="ProposedArguments">A parser projection of the raw text, with its own validity.</param>
/// <param name="Decision">"Pending", "Approved", "Denied" or "Unknown".</param>
/// <param name="Sources">Where the proposal was read.</param>
public sealed record ApprovalIntentObservation(
    string ObservationId,
    Observed<string> ProviderInteractionId,
    Observed<string> ProposedProviderInvocationId,
    ToolIdentity Tool,
    Observed<string> ProposedArgumentsRawText,
    Observed<JsonElement> ProposedArguments,
    string Decision, // "Pending", "Approved", "Denied", "Unknown"
    ImmutableArray<SourceRef> Sources);

/// <summary>A retrieved passage (extract) as observed; offsets and scores stay missing unless reported.</summary>
/// <param name="ObservationId">Local evidence id.</param>
/// <param name="Text">The passage text.</param>
/// <param name="StartOffset">Start offset inside the source document, when reported.</param>
/// <param name="EndOffset">End offset inside the source document, when reported.</param>
/// <param name="RelevanceScore">Relevance score, when reported.</param>
/// <param name="Sources">Where the passage was read.</param>
public sealed record RetrievalPassage(
    string ObservationId,
    Observed<string> Text,
    Observed<int> StartOffset,
    Observed<int> EndOffset,
    Observed<double> RelevanceScore,
    ImmutableArray<SourceRef> Sources);

/// <summary>A retrieval hit; array position is not rank.</summary>
/// <param name="ObservationId">Local evidence id.</param>
/// <param name="ProviderDocumentId">The provider document id, when reported.</param>
/// <param name="WebUrl">The raw URL, when reported (untrusted content).</param>
/// <param name="Rank">The reported rank; a missing/invalid rank keeps the hit.</param>
/// <param name="Passages">The observed passages.</param>
/// <param name="ResourceMetadata">Structured resource metadata, under data policy.</param>
/// <param name="Sources">Where the hit was read.</param>
public sealed record RetrievedHit(
    string ObservationId,
    Observed<string> ProviderDocumentId,
    Observed<string> WebUrl,
    Observed<int> Rank,
    EvidenceSet<RetrievalPassage> Passages,
    Observed<JsonElement> ResourceMetadata,
    ImmutableArray<SourceRef> Sources);

/// <summary>A retrieval query as observed; missing debug info yields missing fields, not an empty query.</summary>
/// <param name="QueryText">The query string.</param>
/// <param name="FilterExpression">The filter expression.</param>
/// <param name="MaximumNumberOfResults">The requested maximum.</param>
public sealed record RetrievalQuery(
    Observed<string> QueryText,
    Observed<string> FilterExpression,
    Observed<int> MaximumNumberOfResults);

/// <summary>A reported retrieval execution; a missing status is not success and the total count is not the returned count.</summary>
/// <param name="ObservationId">Local evidence id.</param>
/// <param name="ProviderInvocationId">The provider invocation id, when reported.</param>
/// <param name="Capability">The retrieval capability name as reported.</param>
/// <param name="ProviderStatus">The raw provider status string.</param>
/// <param name="Outcome">The outcome mapped through a versioned rule.</param>
/// <param name="Queries">The observed queries.</param>
/// <param name="LatencyMilliseconds">Provider-reported latency (finite, non-negative).</param>
/// <param name="TotalResultCount">Provider-reported total (may exceed returned hits).</param>
/// <param name="Hits">The returned hits in reported order.</param>
/// <param name="Sources">Where the execution was read.</param>
public sealed record RetrievalExecutionObservation(
    string ObservationId,
    Observed<string> ProviderInvocationId,
    Observed<string> Capability,
    Observed<string> ProviderStatus,
    Observed<ExecutionOutcome> Outcome,
    EvidenceSet<RetrievalQuery> Queries,
    Observed<double> LatencyMilliseconds,
    Observed<long> TotalResultCount,
    EvidenceSet<RetrievedHit> Hits,
    ImmutableArray<SourceRef> Sources);

/// <summary>A citation marker parsed from the answer text; both turn and search components of a key are kept.</summary>
/// <param name="ObservationId">Local evidence id.</param>
/// <param name="MarkerFormat">The marker format, for example <c>workiq-unicode-cite</c> or <c>markdown-numeric</c>.</param>
/// <param name="RawMarker">The marker exactly as it appears in <see cref="AnswerEvidence.RawText"/>.</param>
/// <param name="ReferenceKeys">The reference keys inside the marker (for example <c>turn0search3</c>), every occurrence.</param>
/// <param name="Utf16Start">Start index in <see cref="AnswerEvidence.RawText"/> (UTF-16 code units).</param>
/// <param name="Utf16Length">Length in UTF-16 code units.</param>
/// <param name="Sources">Where the marker was read.</param>
public sealed record CitationMarker(
    string ObservationId,
    string MarkerFormat,
    string RawMarker,
    ImmutableArray<string> ReferenceKeys,
    int Utf16Start,
    int Utf16Length,
    ImmutableArray<SourceRef> Sources);

/// <summary>A citation source (attribution object) as the provider sent it; the raw object stays in the capture.</summary>
/// <param name="ObservationId">Local evidence id.</param>
/// <param name="ProviderReferenceKey">An explicit provider reference key, when one exists.</param>
/// <param name="Url">The <c>seeMoreWebUrl</c> or equivalent, when reported.</param>
/// <param name="DisplayName">The provider display name (not a document title).</param>
/// <param name="ProviderType">The attribution type as reported (an attribution can be an annotation rather than a citation).</param>
/// <param name="Sources">Where the source was read.</param>
public sealed record CitationSource(
    string ObservationId,
    Observed<string> ProviderReferenceKey,
    Observed<string> Url,
    Observed<string> DisplayName,
    Observed<string> ProviderType,
    ImmutableArray<SourceRef> Sources);

/// <summary>A binding between a marker and a source, labelled by how it was established.</summary>
/// <param name="MarkerObservationId">The <see cref="CitationMarker.ObservationId"/>.</param>
/// <param name="SourceObservationId">The <see cref="CitationSource.ObservationId"/>, or null when unresolved.</param>
/// <param name="Binding">How the link was established.</param>
/// <param name="Sources">Where the binding evidence was read.</param>
public sealed record CitationLink(
    string MarkerObservationId,
    string? SourceObservationId,
    CitationBinding Binding,
    ImmutableArray<SourceRef> Sources);

/// <summary>A positive skill activation observation; advertised <c>AgentCard.skills</c> are not activations.</summary>
/// <param name="ObservationId">Local evidence id.</param>
/// <param name="SkillId">The skill id as reported.</param>
/// <param name="ProviderInvocationId">The provider invocation id, when reported.</param>
/// <param name="Sources">Where the activation was read.</param>
public sealed record SkillActivationObservation(
    string ObservationId,
    Observed<string> SkillId,
    Observed<string> ProviderInvocationId,
    ImmutableArray<SourceRef> Sources);

/// <summary>
/// The versioned evidence document of one evaluated turn (schema <see cref="Microsoft365Evidence.SchemaVersion"/>).
/// Carried on <c>AgentResponse.AdditionalProperties[<see cref="Microsoft365Evidence.Key"/>]</c> and in
/// <see cref="WorkIqTurnResult.Evidence"/>. Same capture + same versions ⇒ same document.
/// </summary>
/// <param name="SchemaVersion">The document schema id (<see cref="Microsoft365Evidence.SchemaVersion"/>).</param>
/// <param name="NormalizerVersion">The normaliser version that produced it (<see cref="Microsoft365Evidence.NormalizerVersion"/>).</param>
/// <param name="ProviderProfileVersion">The provider profile/decoder version (<see cref="Microsoft365Evidence.ProviderProfileVersion"/>).</param>
/// <param name="A2AVersion">The A2A protocol version negotiated (<see cref="Microsoft365Evidence.A2AVersion"/>).</param>
/// <param name="Identity">The turn's identities.</param>
/// <param name="TaskState">The raw final task state (for example <c>TASK_STATE_COMPLETED</c>); null for a direct message with no task.</param>
/// <param name="CaptureState">Whether the retained material can reproduce this document.</param>
/// <param name="Answer">The selected final answer; null when no valid answer exists.</param>
/// <param name="ToolSelections">Reported tool selections.</param>
/// <param name="ToolExecutions">Reported tool executions.</param>
/// <param name="ApprovalIntents">Proposed invocations awaiting consent.</param>
/// <param name="RetrievalExecutions">Reported retrieval executions.</param>
/// <param name="CitationMarkers">Markers parsed from the answer text.</param>
/// <param name="CitationSources">Attribution objects as sent.</param>
/// <param name="CitationLinks">Marker-to-source bindings, labelled by method.</param>
/// <param name="SkillActivations">Positive skill activation observations.</param>
/// <param name="RawCaptures">The raw captures that fed this document.</param>
/// <param name="Issues">Document-level normalisation issues.</param>
public sealed record Microsoft365TurnEvidence(
    string SchemaVersion,
    string NormalizerVersion,
    string ProviderProfileVersion,
    string A2AVersion,
    TurnIdentity Identity,
    string? TaskState,
    CaptureState CaptureState,
    AnswerEvidence? Answer,
    EvidenceSet<ToolSelectionObservation> ToolSelections,
    EvidenceSet<ToolExecutionObservation> ToolExecutions,
    EvidenceSet<ApprovalIntentObservation> ApprovalIntents,
    EvidenceSet<RetrievalExecutionObservation> RetrievalExecutions,
    EvidenceSet<CitationMarker> CitationMarkers,
    EvidenceSet<CitationSource> CitationSources,
    ImmutableArray<CitationLink> CitationLinks,
    EvidenceSet<SkillActivationObservation> SkillActivations,
    ImmutableArray<RawCaptureRef> RawCaptures,
    ImmutableArray<EvidenceIssue> Issues);
