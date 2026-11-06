// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using static AgentEval.Microsoft365.Evidence.Decoders.EvidenceJsonReader;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// Decodes the Work IQ interaction data part of a suspended task's status message
/// (<see cref="InteractionMediaType"/>): the <c>input-required</c> confirmation card into
/// <see cref="ApprovalIntentObservation"/>s (a proposal awaiting consent is never an execution), and — for the
/// host — the whole pause into a <see cref="WorkIqPendingInteraction"/>. Both come from one walk of the card: the
/// pause part is selected once (a message with more than one pause part is ambiguous and nothing is chosen), the
/// actions and twins are walked once, and the pending interaction's proposed calls are projected from the approval
/// intents, so the two never disagree. The card's
/// <c>adaptiveCard.actions[].data.message.action</c> and <c>.actionId</c> are JSON-encoded strings of an array of
/// calls <c>{id, type, function:{name, arguments}}</c> where <c>function.arguments</c> is itself a JSON-encoded
/// string: the exact argument string is kept verbatim, its parsed form is a separate projection, and the two twins
/// are merged only when every call is byte-identical (otherwise both are kept with an issue). Nothing is ever
/// de-duplicated by name. Only the shapes in the fixture corpus are recognised; anything else stays
/// <see cref="WorkIqInteractionKind.Unknown"/> and is never answered.
/// </summary>
internal static class InteractionDecoder
{
    /// <summary>The media type of the interaction data part.</summary>
    public const string InteractionMediaType = "application/vnd.ms-workiq.interaction+json";

    /// <summary>The versioned interaction profile this decoder recognises.</summary>
    public const string ProfileId = "workiq-interaction-profile/v1";

    /// <summary>The data key of an input-required pause.</summary>
    public const string InputRequiredKey = "input-required";

    /// <summary>The data key of an auth-required pause (inner shape invented by the fixtures, not documented).</summary>
    public const string AuthRequiredKey = "auth-required";

    /// <summary>The <c>type</c> of a confirmation pause.</summary>
    public const string ConfirmationRequiredType = "confirmation_required";

    /// <summary>The rule id of the parsed-arguments projection.</summary>
    public const string ArgumentsRuleId = "approval.arguments-json/v1";

    /// <summary>The decision of every decoded proposal: nothing is approved by decoding.</summary>
    public const string PendingDecision = "Pending";

    /// <summary>Issue: the <c>action</c> and <c>actionId</c> twins of one card action differ; both are kept.</summary>
    public const string ConflictingTwinsIssueCode = "approval.conflicting_twins";

    /// <summary>Issue: more than one interaction data part carries a pause (<c>input-required</c> or <c>auth-required</c>), at least one of them <c>input-required</c>; nothing was chosen.</summary>
    public const string AmbiguousPartIssueCode = "approval.ambiguous_interaction_part";

    /// <summary>Issue: a twin payload is not a string, not JSON, or not a JSON array.</summary>
    public const string TwinUnreadableIssueCode = "approval.twin_unreadable";

    /// <summary>Issue: a card structure element has the wrong shape.</summary>
    public const string CardMalformedIssueCode = "approval.card_malformed";

    /// <summary>Issue: a decoded call is not a JSON object; its fields are Invalid.</summary>
    public const string CallNotObjectIssueCode = "approval.call_not_object";

    /// <summary>Issue: a call field has the wrong JSON type.</summary>
    public const string FieldInvalidIssueCode = "approval.call_field_invalid";

    private const string SourceRequestIdKey = "sourceRequestId";
    private const string TypeKey = "type";
    private const string AdaptiveCardKey = "adaptiveCard";
    private const string ActionsKey = "actions";
    private const string DataKey = "data";
    private const string MessageKey = "message";
    private const string ActionKey = "action";
    private const string ActionIdKey = "actionId";
    private const string ConfirmationOptionKey = "confirmationOption";

    /// <summary>One decoded proposed call (from one twin, or merged from both when byte-identical).</summary>
    private sealed record DecodedCall(JsonElement Element, ImmutableArray<SourceRef> Sources);

    /// <summary>What the status message's interaction data parts hold.</summary>
    private enum PauseShape
    {
        /// <summary>No interaction data part at all.</summary>
        None,

        /// <summary>Interaction data parts, none of which carries a pause key.</summary>
        NoPause,

        /// <summary>Exactly one part carries a pause key.</summary>
        Single,

        /// <summary>More than one part carries a pause key; nothing is chosen.</summary>
        Ambiguous,
    }

    /// <summary>The one selection of the pause part: <paramref name="Parts"/> are every interaction data part; <paramref name="Pauses"/> those that carry a pause key, in order.</summary>
    private sealed record PauseSelection(
        PauseShape Shape,
        List<(int PartIndex, JsonElement Data)> Parts,
        List<(int PartIndex, JsonElement Data)> Pauses);

    /// <summary>The one walk of a status message's pause: the selection, the approval intents and, per intent, the raw call type.</summary>
    private sealed record CardDecoding(
        PauseSelection Selection,
        EvidenceSet<ApprovalIntentObservation> Intents,
        ImmutableArray<Observed<string>> CallTypes);

    /// <summary>Decodes the approval intents of a suspended task's status message.</summary>
    /// <param name="statusMessage">The status message; null when the task carried none.</param>
    /// <param name="messageSource">The source pointer of the status message.</param>
    public static EvidenceSet<ApprovalIntentObservation> DecodeApprovalIntents(WorkIqMessage? statusMessage, SourceRef messageSource)
    {
        ArgumentNullException.ThrowIfNull(messageSource);
        return DecodeCard(statusMessage, messageSource).Intents;
    }

    /// <summary>
    /// Selects the pause part once and walks its card once: approval intents only from a single <c>input-required</c>
    /// pause part; <see cref="EvidenceValidation.Conflicting"/> with no items when more than one part carries a pause and
    /// at least one of them is <c>input-required</c>; <see cref="EvidenceAvailability.NotProvided"/> otherwise.
    /// </summary>
    private static CardDecoding DecodeCard(WorkIqMessage? statusMessage, SourceRef messageSource)
    {
        if (statusMessage is null)
        {
            return new CardDecoding(
                new PauseSelection(PauseShape.None, [], []),
                EvidenceSet<ApprovalIntentObservation>.NotProvided(EvidenceScope.Unknown, One(messageSource)),
                ImmutableArray<Observed<string>>.Empty);
        }

        var selection = SelectPause(statusMessage);
        var partsSource = Child(messageSource, "parts");
        var inputPauses = selection.Pauses.Where(p => p.Data.TryGetProperty(InputRequiredKey, out _)).ToList();
        if (inputPauses.Count == 0)
        {
            return new CardDecoding(
                selection,
                EvidenceSet<ApprovalIntentObservation>.NotProvided(EvidenceScope.Unknown, One(partsSource)),
                ImmutableArray<Observed<string>>.Empty);
        }

        if (selection.Shape == PauseShape.Ambiguous)
        {
            var sources = selection.Pauses.Select(c => DataSource(partsSource, c.PartIndex)).ToImmutableArray();
            var conflicting = EvidenceSet<ApprovalIntentObservation>.Provided(
                ImmutableArray<ApprovalIntentObservation>.Empty,
                EvidenceValidation.Conflicting,
                EvidenceCoverage.Unknown,
                EvidenceScope.Unknown,
                sources,
                ImmutableArray.Create(new EvidenceIssue(
                    AmbiguousPartIssueCode,
                    FormattableString.Invariant($"{selection.Pauses.Count} interaction data parts carry a pause (input-required or auth-required); none was chosen and no proposal was read from any."),
                    sources)));
            return new CardDecoding(selection, conflicting, ImmutableArray<Observed<string>>.Empty);
        }

        var (partIndex, data) = inputPauses[0];
        var (intents, callTypes) = DecodeInputRequired(data, Child(DataSource(partsSource, partIndex), InputRequiredKey));
        return new CardDecoding(selection, intents, callTypes);
    }

    /// <summary>Every interaction data part, and those that carry a pause key (<c>input-required</c> or <c>auth-required</c>).</summary>
    private static PauseSelection SelectPause(WorkIqMessage message)
    {
        var parts = FindInteractionParts(message);
        var pauses = parts
            .Where(p => p.Data.TryGetProperty(InputRequiredKey, out _) || p.Data.TryGetProperty(AuthRequiredKey, out _))
            .ToList();
        var shape = parts.Count == 0 ? PauseShape.None
            : pauses.Count == 0 ? PauseShape.NoPause
            : pauses.Count == 1 ? PauseShape.Single
            : PauseShape.Ambiguous;
        return new PauseSelection(shape, parts, pauses);
    }

    /// <summary>Walks one <c>input-required</c> pause's card: the approval intents and, per intent, the raw call type.</summary>
    private static (EvidenceSet<ApprovalIntentObservation> Intents, ImmutableArray<Observed<string>> CallTypes) DecodeInputRequired(JsonElement data, SourceRef pauseSource)
    {
        var noCallTypes = ImmutableArray<Observed<string>>.Empty;
        var pause = data.GetProperty(InputRequiredKey);
        if (pause.ValueKind != JsonValueKind.Object)
        {
            return (MalformedSet<ApprovalIntentObservation>(pauseSource, ImmutableArray.Create(Issue(
                CardMalformedIssueCode, "input-required is not a JSON object; no proposal can be read from it.", pauseSource))), noCallTypes);
        }

        var interactionId = ReadString(pause, SourceRequestIdKey, pauseSource);
        var type = ReadString(pause, TypeKey, pauseSource);
        var isConfirmation = type.IsPresent && string.Equals(type.Value, ConfirmationRequiredType, StringComparison.Ordinal);

        if (!TryGetProperty(pause, AdaptiveCardKey, out var card))
        {
            return (isConfirmation
                ? MalformedSet<ApprovalIntentObservation>(pauseSource, ImmutableArray.Create(Issue(
                    CardMalformedIssueCode, "A confirmation_required pause carries no adaptiveCard; its proposals cannot be read.", pauseSource)))
                : EvidenceSet<ApprovalIntentObservation>.NotProvided(EvidenceScope.Unknown, One(pauseSource)), noCallTypes);
        }

        var cardSource = Child(pauseSource, AdaptiveCardKey);
        if (card.ValueKind != JsonValueKind.Object)
        {
            return (MalformedSet<ApprovalIntentObservation>(cardSource, ImmutableArray.Create(Issue(
                CardMalformedIssueCode, "adaptiveCard is not a JSON object.", cardSource))), noCallTypes);
        }

        if (!TryGetProperty(card, ActionsKey, out var actions))
        {
            return (isConfirmation
                ? MalformedSet<ApprovalIntentObservation>(cardSource, ImmutableArray.Create(Issue(
                    CardMalformedIssueCode, "A confirmation_required card carries no actions; its proposals cannot be read.", cardSource)))
                : EvidenceSet<ApprovalIntentObservation>.NotProvided(EvidenceScope.Unknown, One(cardSource)), noCallTypes);
        }

        var actionsSource = Child(cardSource, ActionsKey);
        if (actions.ValueKind != JsonValueKind.Array)
        {
            return (MalformedSet<ApprovalIntentObservation>(actionsSource, ImmutableArray.Create(Issue(
                CardMalformedIssueCode, "adaptiveCard.actions is not a JSON array.", actionsSource))), noCallTypes);
        }

        var items = ImmutableArray.CreateBuilder<ApprovalIntentObservation>();
        var callTypes = ImmutableArray.CreateBuilder<Observed<string>>();
        var issues = new List<EvidenceIssue>();
        var anyInvalid = false;
        var interpreted = 0;
        var actionIndex = 0;
        foreach (var action in actions.EnumerateArray())
        {
            var actionSource = Child(actionsSource, actionIndex);
            if (!TryFindProposalMessage(action, actionSource, issues, ref anyInvalid, out var message, out var proposalSource))
            {
                actionIndex++;
                continue;
            }

            var calls = DecodeTwins(message, proposalSource, issues, ref anyInvalid);
            for (var callIndex = 0; callIndex < calls.Count; callIndex++)
            {
                var call = calls[callIndex];
                var id = ObservationId("approval-intent", actionIndex, callIndex);
                if (call.Element.ValueKind == JsonValueKind.Object)
                {
                    var (observation, callType) = ToObservation(call, id, interactionId, issues, ref anyInvalid);
                    items.Add(observation);
                    callTypes.Add(callType);
                    interpreted++;
                }
                else
                {
                    anyInvalid = true;
                    items.Add(InvalidObservation(call.Sources, id, interactionId));
                    callTypes.Add(Observed<string>.Invalid(call.Sources));
                    issues.Add(new EvidenceIssue(
                        CallNotObjectIssueCode,
                        FormattableString.Invariant($"Proposed call {callIndex} of action {actionIndex} is not a JSON object; its fields are Invalid and the call is kept."),
                        call.Sources));
                }
            }

            actionIndex++;
        }

        // No proposal and something unreadable → Malformed; proposals but none interpreted → Malformed (row rule);
        // otherwise the row rule (Valid, or Partial when a row or field was invalid).
        var validation = items.Count == 0
            ? (anyInvalid ? EvidenceValidation.Malformed : EvidenceValidation.Valid)
            : RowSetValidation(items.Count, interpreted, anyInvalid);

        var set = EvidenceSet<ApprovalIntentObservation>.Provided(
            items.ToImmutable(),
            validation,
            EvidenceCoverage.Unknown,
            EvidenceScope.Unknown,
            One(actionsSource),
            issues.ToImmutableArray());
        return (set, callTypes.ToImmutable());
    }

    /// <summary>
    /// Decodes the pause of a suspended task for the host from the same card walk as
    /// <see cref="DecodeApprovalIntents"/>, so the two never disagree. The single pause part (the one interaction data
    /// part that carries <c>input-required</c> or <c>auth-required</c>) becomes a <see cref="WorkIqPendingInteraction"/>:
    /// <c>input-required</c> with <c>type: confirmation_required</c> → <see cref="WorkIqInteractionKind.ConfirmationRequired"/>
    /// with the proposed calls projected from the approval intents and the exact <c>confirmationOption</c> strings;
    /// <c>auth-required</c> → <see cref="WorkIqInteractionKind.AuthenticationRequired"/>; anything else →
    /// <see cref="WorkIqInteractionKind.Unknown"/>. When more than one part carries a pause, nothing is chosen: the
    /// interaction is <see cref="WorkIqInteractionKind.Unknown"/> with no proposed call and no option (the approval
    /// intents are then <see cref="EvidenceValidation.Conflicting"/> or not provided), the first pause part's data is
    /// retained raw and every pause part is a source. Interaction data parts without any pause yield an
    /// <see cref="WorkIqInteractionKind.Unknown"/> interaction from the first part. Null when the message carries no
    /// interaction data part — a text-only <c>INPUT_REQUIRED</c> status is the caller's decision. The caller applies the
    /// content policy to <see cref="WorkIqPendingInteraction.PromptText"/> and <see cref="WorkIqPendingInteraction.RawInteractionData"/>.
    /// </summary>
    /// <param name="statusMessage">The suspended task's status message.</param>
    /// <param name="task">The suspended task.</param>
    /// <param name="captureId">The capture the message was read from (for source pointers).</param>
    public static WorkIqPendingInteraction? TryDecode(WorkIqMessage statusMessage, WorkIqTaskHandle task, string captureId)
    {
        ArgumentNullException.ThrowIfNull(statusMessage);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);

        var messageSource = new SourceRef(captureId, 0, EvidenceOrigin.A2AMessage, WorkIqEvidenceNormalizer.TaskStatusMessagePointer);
        var decoding = DecodeCard(statusMessage, messageSource);
        var selection = decoding.Selection;
        if (selection.Shape == PauseShape.None)
        {
            return null;
        }

        var prompt = PromptTextOf(statusMessage);
        SourceRef PartSource(int index) => Child(Child(messageSource, "parts"), index) with { PartIndex = index };

        if (selection.Shape == PauseShape.Ambiguous)
        {
            return new WorkIqPendingInteraction(
                task,
                WorkIqInteractionKind.Unknown,
                null,
                prompt,
                ImmutableArray<WorkIqProposedCall>.Empty,
                ImmutableArray<string>.Empty,
                ProfileId,
                selection.Pauses[0].Data,
                selection.Pauses.Select(p => PartSource(p.PartIndex)).ToImmutableArray());
        }

        var (partIndex, data) = selection.Shape == PauseShape.Single ? selection.Pauses[0] : selection.Parts[0];
        var partSource = PartSource(partIndex);
        var dataSource = Child(partSource, DataKey);

        if (TryGetProperty(data, InputRequiredKey, out var pause) && pause.ValueKind == JsonValueKind.Object)
        {
            var pauseSource = Child(dataSource, InputRequiredKey);
            var sourceRequestId = ReadString(pause, SourceRequestIdKey, pauseSource);
            var type = ReadString(pause, TypeKey, pauseSource);
            var isConfirmation = type.IsPresent && string.Equals(type.Value, ConfirmationRequiredType, StringComparison.Ordinal);
            var kind = isConfirmation ? WorkIqInteractionKind.ConfirmationRequired : WorkIqInteractionKind.Unknown;
            var calls = isConfirmation ? ProposedCallsOf(decoding) : ImmutableArray<WorkIqProposedCall>.Empty;
            var options = isConfirmation ? OptionsOf(pause) : ImmutableArray<string>.Empty;
            return new WorkIqPendingInteraction(
                task,
                kind,
                sourceRequestId.IsPresent ? sourceRequestId.Value : null,
                prompt,
                calls,
                options,
                ProfileId,
                data,
                One(partSource));
        }

        if (TryGetProperty(data, AuthRequiredKey, out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            var authSource = Child(dataSource, AuthRequiredKey);
            var sourceRequestId = ReadString(auth, SourceRequestIdKey, authSource);
            return new WorkIqPendingInteraction(
                task,
                WorkIqInteractionKind.AuthenticationRequired,
                sourceRequestId.IsPresent ? sourceRequestId.Value : null,
                prompt,
                ImmutableArray<WorkIqProposedCall>.Empty,
                ImmutableArray<string>.Empty,
                ProfileId,
                data,
                One(partSource));
        }

        return new WorkIqPendingInteraction(
            task,
            WorkIqInteractionKind.Unknown,
            null,
            prompt,
            ImmutableArray<WorkIqProposedCall>.Empty,
            ImmutableArray<string>.Empty,
            ProfileId,
            data,
            One(partSource));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Locating
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Every data part of the message whose media type is the interaction type and whose data is an object, with its part index.</summary>
    private static List<(int PartIndex, JsonElement Data)> FindInteractionParts(WorkIqMessage message)
    {
        var found = new List<(int, JsonElement)>();
        for (var i = 0; i < message.Parts.Length; i++)
        {
            var part = message.Parts[i];
            if (part.Kind == WorkIqPartKind.Data
                && part.MediaType is not null
                && string.Equals(part.MediaType, InteractionMediaType, StringComparison.OrdinalIgnoreCase)
                && part.Data is { ValueKind: JsonValueKind.Object } data)
            {
                found.Add((i, data));
            }
        }

        return found;
    }

    private static SourceRef DataSource(SourceRef partsSource, int partIndex) =>
        Child(Child(partsSource, partIndex) with { PartIndex = partIndex }, DataKey);

    private static string? PromptTextOf(WorkIqMessage message)
    {
        var texts = message.Parts.Where(p => p.Kind == WorkIqPartKind.Text && p.Text is not null).Select(p => p.Text!).ToList();
        return texts.Count == 0 ? null : string.Concat(texts);
    }

    /// <summary>Finds <c>data.message</c> of a card action; false (without an issue) for an action that proposes nothing, such as Cancel.</summary>
    private static bool TryFindProposalMessage(
        JsonElement action,
        SourceRef actionSource,
        List<EvidenceIssue> issues,
        ref bool anyInvalid,
        out JsonElement message,
        out SourceRef messageSource)
    {
        message = default;
        messageSource = actionSource;
        if (action.ValueKind != JsonValueKind.Object)
        {
            anyInvalid = true;
            issues.Add(Issue(CardMalformedIssueCode, "A card action is not a JSON object.", actionSource));
            return false;
        }

        if (!TryGetProperty(action, DataKey, out var actionData) || actionData.ValueKind != JsonValueKind.Object
            || !TryGetProperty(actionData, MessageKey, out message))
        {
            return false;
        }

        messageSource = Child(Child(actionSource, DataKey), MessageKey);
        if (message.ValueKind != JsonValueKind.Object)
        {
            anyInvalid = true;
            issues.Add(Issue(CardMalformedIssueCode, "A card action's data.message is not a JSON object.", messageSource));
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Twins
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Decodes the <c>action</c> / <c>actionId</c> twins of one proposal message, merging them only when byte-identical call by call.</summary>
    private static List<DecodedCall> DecodeTwins(JsonElement message, SourceRef messageSource, List<EvidenceIssue> issues, ref bool anyInvalid)
    {
        var (actionCalls, actionSource) = DecodeTwin(message, ActionKey, messageSource, issues, ref anyInvalid);
        var (actionIdCalls, actionIdSource) = DecodeTwin(message, ActionIdKey, messageSource, issues, ref anyInvalid);

        if (actionCalls is null && actionIdCalls is null)
        {
            return [];
        }

        if (actionCalls is null)
        {
            return actionIdCalls!.Select(c => new DecodedCall(c, One(actionIdSource))).ToList();
        }

        if (actionIdCalls is null)
        {
            return actionCalls.Select(c => new DecodedCall(c, One(actionSource))).ToList();
        }

        if (actionCalls.Count == actionIdCalls.Count
            && actionCalls.Zip(actionIdCalls).All(pair => string.Equals(pair.First.GetRawText(), pair.Second.GetRawText(), StringComparison.Ordinal)))
        {
            return actionCalls.Select(c => new DecodedCall(c, ImmutableArray.Create(actionSource, actionIdSource))).ToList();
        }

        anyInvalid = true;
        issues.Add(new EvidenceIssue(
            ConflictingTwinsIssueCode,
            "The action and actionId payloads of one card action are not byte-identical; the proposals of both are kept and none was merged.",
            ImmutableArray.Create(actionSource, actionIdSource)));
        var kept = actionCalls.Select(c => new DecodedCall(c, One(actionSource))).ToList();
        kept.AddRange(actionIdCalls.Select(c => new DecodedCall(c, One(actionIdSource))));
        return kept;
    }

    /// <summary>Decodes one twin: a JSON-encoded string of an array of calls. Null when absent or unreadable (with an issue when unreadable).</summary>
    private static (List<JsonElement>? Calls, SourceRef Source) DecodeTwin(
        JsonElement message,
        string key,
        SourceRef messageSource,
        List<EvidenceIssue> issues,
        ref bool anyInvalid)
    {
        var source = Child(messageSource, key);
        if (!TryGetProperty(message, key, out var value))
        {
            return (null, source);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            anyInvalid = true;
            issues.Add(Issue(TwinUnreadableIssueCode, FormattableString.Invariant($"'{key}' is not a JSON-encoded string."), source));
            return (null, source);
        }

        try
        {
            using var document = JsonDocument.Parse(value.GetString()!);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                anyInvalid = true;
                issues.Add(Issue(TwinUnreadableIssueCode, FormattableString.Invariant($"'{key}' decodes to JSON that is not an array of calls."), source));
                return (null, source);
            }

            return (document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList(), source);
        }
        catch (JsonException)
        {
            anyInvalid = true;
            issues.Add(Issue(TwinUnreadableIssueCode, FormattableString.Invariant($"'{key}' is not valid JSON."), source));
            return (null, source);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Calls
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Reads a call field; every state points at the twin string(s) the call was decoded from (a pointer cannot descend into a JSON string).</summary>
    private static Observed<string> ReadCallString(JsonElement parent, string name, ImmutableArray<SourceRef> sources)
    {
        if (!TryGetProperty(parent, name, out var value))
        {
            return Observed<string>.Missing();
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => Observed<string>.Null(sources),
            JsonValueKind.String => Observed<string>.Present(value.GetString()!, sources),
            _ => Observed<string>.Invalid(sources),
        };
    }

    /// <summary>The call's name and raw argument string from <c>function</c>; a missing/null/invalid container is mirrored on both fields.</summary>
    private static (Observed<string> Name, Observed<string> ArgumentsRaw, Observed<string> CallType, Observed<string> CallId) ReadCall(JsonElement call, ImmutableArray<SourceRef> sources)
    {
        var callId = ReadCallString(call, "id", sources);
        var callType = ReadCallString(call, "type", sources);
        if (!TryGetProperty(call, "function", out var function))
        {
            return (Observed<string>.Missing(), Observed<string>.Missing(), callType, callId);
        }

        return function.ValueKind switch
        {
            JsonValueKind.Null => (Observed<string>.Null(sources), Observed<string>.Null(sources), callType, callId),
            JsonValueKind.Object => (ReadCallString(function, "name", sources), ReadCallString(function, "arguments", sources), callType, callId),
            _ => (Observed<string>.Invalid(sources), Observed<string>.Invalid(sources), callType, callId),
        };
    }

    /// <summary>The parser projection of the raw argument string: valid JSON → Present (detached), otherwise Invalid; a non-present raw text is mirrored.</summary>
    private static Observed<JsonElement> ParseArguments(Observed<string> argumentsRaw, ImmutableArray<SourceRef> sources)
    {
        switch (argumentsRaw.State)
        {
            case FieldState.Present:
                try
                {
                    using var document = JsonDocument.Parse(argumentsRaw.Value!);
                    return Observed<JsonElement>.Present(document.RootElement, sources, EvidenceDerivation.ParserProjection, ArgumentsRuleId);
                }
                catch (JsonException)
                {
                    return Observed<JsonElement>.Invalid(sources);
                }

            case FieldState.Null:
                return Observed<JsonElement>.Null(sources);
            case FieldState.Invalid:
                return Observed<JsonElement>.Invalid(sources);
            default:
                return Observed<JsonElement>.Missing();
        }
    }

    private static (ApprovalIntentObservation Observation, Observed<string> CallType) ToObservation(DecodedCall call, string id, Observed<string> interactionId, List<EvidenceIssue> issues, ref bool anyInvalid)
    {
        var (name, argumentsRaw, callType, callId) = ReadCall(call.Element, call.Sources);
        var parsed = ParseArguments(argumentsRaw, call.Sources);
        NoteInvalid(issues, FieldInvalidIssueCode, "id", callId);
        NoteInvalid(issues, FieldInvalidIssueCode, "function.name", name);
        NoteInvalid(issues, FieldInvalidIssueCode, "function.arguments", argumentsRaw);
        if (argumentsRaw.IsPresent && IsInvalid(parsed))
        {
            issues.Add(new EvidenceIssue(FieldInvalidIssueCode, "'function.arguments' is a string but not valid JSON; the exact string is kept and the parsed projection is Invalid.", call.Sources));
        }

        anyInvalid |= IsInvalid(callId) || IsInvalid(name) || IsInvalid(argumentsRaw) || IsInvalid(parsed);
        var observation = new ApprovalIntentObservation(
            id,
            interactionId,
            callId,
            ToolIdentity.FromReportedNames(Observed<string>.Missing(), name, call.Sources),
            argumentsRaw,
            parsed,
            PendingDecision,
            call.Sources);
        return (observation, callType);
    }

    private static ApprovalIntentObservation InvalidObservation(ImmutableArray<SourceRef> sources, string id, Observed<string> interactionId)
    {
        var invalid = Observed<string>.Invalid(sources);
        return new ApprovalIntentObservation(
            id,
            interactionId,
            invalid,
            ToolIdentity.FromReportedNames(Observed<string>.Missing(), invalid, sources),
            invalid,
            Observed<JsonElement>.Invalid(sources),
            PendingDecision,
            sources);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Pending-interaction projection (for the host)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The proposed calls of the pending interaction: one per approval intent of the same card walk, in order; a field that is not present is null.</summary>
    private static ImmutableArray<WorkIqProposedCall> ProposedCallsOf(CardDecoding decoding)
    {
        var intents = decoding.Intents.Items;
        var calls = ImmutableArray.CreateBuilder<WorkIqProposedCall>(intents.Length);
        for (var i = 0; i < intents.Length; i++)
        {
            var intent = intents[i];
            var callType = decoding.CallTypes[i];
            calls.Add(new WorkIqProposedCall(
                intent.ProposedProviderInvocationId.IsPresent ? intent.ProposedProviderInvocationId.Value : null,
                callType.IsPresent ? callType.Value : null,
                intent.Tool.FunctionDisplayName.IsPresent ? intent.Tool.FunctionDisplayName.Value : null,
                intent.ProposedArgumentsRawText.IsPresent ? intent.ProposedArgumentsRawText.Value : null,
                intent.ProposedArguments,
                intent.Sources));
        }

        return calls.MoveToImmutable();
    }

    /// <summary>The exact <c>data.confirmationOption</c> strings of the card's actions, in order, without duplicates.</summary>
    private static ImmutableArray<string> OptionsOf(JsonElement pause)
    {
        if (!TryGetProperty(pause, AdaptiveCardKey, out var card) || !TryGetProperty(card, ActionsKey, out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return ImmutableArray<string>.Empty;
        }

        var options = ImmutableArray.CreateBuilder<string>();
        foreach (var action in actions.EnumerateArray())
        {
            if (TryGetProperty(action, DataKey, out var data)
                && TryGetProperty(data, ConfirmationOptionKey, out var option)
                && option.ValueKind == JsonValueKind.String)
            {
                var text = option.GetString()!;
                if (!options.Contains(text))
                {
                    options.Add(text);
                }
            }
        }

        return options.ToImmutable();
    }
}
