// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using Xunit;
using static AgentEval.Tests.Microsoft365.Evidence.EvidenceTestKit;

namespace AgentEval.Tests.Microsoft365.Evidence;

/// <summary>
/// <see cref="InteractionDecoder"/>: the confirmation card's proposed calls become approval intents with the exact
/// argument string and a separate parsed projection, byte-identical twins merge into one proposal, differing twins
/// are both kept with an issue, nothing is de-duplicated by name, and <see cref="InteractionDecoder.TryDecode"/>
/// recognises only the fixture profile (confirmation / auth), never answering anything on its own.
/// </summary>
public sealed class InteractionDecoderTests
{
    private const string ConfirmationFixture = "envelopes/send-task-input-required-confirmation.json";
    private const string AuthFixture = "envelopes/send-task-auth-required.json";
    private const string ExpectedCallId = "call-0a1b2c3d4e5f";
    private const string ExpectedRequestId = "req-c3d1f7a9-5e2b-4d8c-9a6f-1b4e7c0d2a58";
    private const string ActionPointer = "/status/message/parts/1/data/input-required/adaptiveCard/actions/0/data/message/action";
    private const string ActionIdPointer = "/status/message/parts/1/data/input-required/adaptiveCard/actions/0/data/message/actionId";

    private static readonly SourceRef MessageSource = new("cap-1", 0, EvidenceOrigin.A2AMessage, "/status/message");

    private static WorkIqMessage FixtureStatus(string fixture) => SnapshotFromEnvelope(fixture).StatusMessage!;

    /// <summary>A JSON-encoded string of an array of calls, as the card carries it.</summary>
    private static string Calls(params (string Id, string Name, string Arguments)[] calls)
    {
        var array = new JsonArray();
        foreach (var (id, name, arguments) in calls)
        {
            array.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
            });
        }

        return array.ToJsonString();
    }

    /// <summary>A status message with a confirmation card whose Allow action carries the given twins (null omits one).</summary>
    private static WorkIqMessage Confirmation(string? action, string? actionId, string type = InteractionDecoder.ConfirmationRequiredType, bool withAllow = true)
    {
        var message = new JsonObject();
        if (action is not null)
        {
            message["action"] = action;
        }

        if (actionId is not null)
        {
            message["actionId"] = actionId;
        }

        var actions = new JsonArray();
        if (withAllow)
        {
            actions.Add(new JsonObject
            {
                ["type"] = "Action.Submit",
                ["title"] = "Allow",
                ["data"] = new JsonObject { ["message"] = message, ["confirmationOption"] = "Allow" },
            });
        }

        actions.Add(new JsonObject
        {
            ["type"] = "Action.Submit",
            ["title"] = "Cancel",
            ["data"] = new JsonObject { ["confirmationOption"] = "Cancel" },
        });

        var data = new JsonObject
        {
            ["input-required"] = new JsonObject
            {
                ["type"] = type,
                ["sourceRequestId"] = "req-test-1",
                ["adaptiveCard"] = new JsonObject { ["type"] = "AdaptiveCard", ["version"] = "1.5", ["actions"] = actions },
            },
        };

        return MessageWithParts(Text("Confirm?"), Data(data.ToJsonString(), InteractionDecoder.InteractionMediaType));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Approval intents
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void DecodeApprovalIntents_ConfirmationFixture_ByteIdenticalTwinsMergeIntoOneProposalWithRawArguments()
    {
        var set = InteractionDecoder.DecodeApprovalIntents(FixtureStatus(ConfirmationFixture), MessageSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Valid, set.Validation);
        Assert.Equal(EvidenceCoverage.Unknown, set.Coverage);
        Assert.Empty(set.Issues);
        var intent = Assert.Single(set.Items);

        Assert.Equal("approval-intent-0-0", intent.ObservationId);
        Assert.Equal(InteractionDecoder.PendingDecision, intent.Decision);
        Assert.Equal(ExpectedRequestId, intent.ProviderInteractionId.Value);
        Assert.Equal(ExpectedCallId, intent.ProposedProviderInvocationId.Value);
        Assert.Equal(FieldState.Missing, intent.Tool.PluginName.State);
        Assert.Equal("CreateDemoEvent", intent.Tool.FunctionDisplayName.Value);
        Assert.Equal(FieldState.Missing, intent.Tool.ProviderToolId.State);
        Assert.Equal(ToolTransport.Unknown, intent.Tool.Transport.Value);
        Assert.Equal(EvidenceDerivation.ParserProjection, intent.Tool.Transport.Derivation);
        Assert.Equal(Native118DiagnosticsDecoder.TransportRuleId, intent.Tool.Transport.DerivationRuleId);

        Assert.Equal(FieldState.Present, intent.ProposedArgumentsRawText.State);
        Assert.StartsWith("{\"subject\":\"Atlas pilot kickoff\",\"start\":\"2026-11-17T09:00:00+01:00\"", intent.ProposedArgumentsRawText.Value, StringComparison.Ordinal);
        Assert.Equal(EvidenceDerivation.ProviderValue, intent.ProposedArgumentsRawText.Derivation);
        Assert.Equal(FieldState.Present, intent.ProposedArguments.State);
        Assert.Equal(EvidenceDerivation.ParserProjection, intent.ProposedArguments.Derivation);
        Assert.Equal(InteractionDecoder.ArgumentsRuleId, intent.ProposedArguments.DerivationRuleId);
        Assert.Equal("Atlas pilot kickoff", intent.ProposedArguments.Value.GetProperty("subject").GetString());
        Assert.Equal("Europe/Zurich", intent.ProposedArguments.Value.GetProperty("timeZone").GetString());

        Assert.Equal(2, intent.Sources.Length);
        Assert.Equal(ActionPointer, intent.Sources[0].JsonPointer);
        Assert.Equal(ActionIdPointer, intent.Sources[1].JsonPointer);
        Assert.Equal(1, intent.Sources[0].PartIndex);
        Assert.Equal(EvidenceOrigin.A2AMessage, intent.Sources[0].Origin);
        Assert.Equal("/status/message/parts/1/data/input-required/adaptiveCard/actions", Assert.Single(set.Sources).JsonPointer);
    }

    [Fact]
    public void DecodeApprovalIntents_DifferingTwins_KeepsBothWithAConflictIssue()
    {
        var status = Confirmation(Calls(("call-1", "CreateDemoEvent", "{\"a\":1}")), Calls(("call-2", "CreateDemoEvent", "{\"a\":1}")));

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceValidation.Partial, set.Validation);
        Assert.Equal(2, set.Items.Length);
        Assert.Equal("call-1", set.Items[0].ProposedProviderInvocationId.Value);
        Assert.Equal("call-2", set.Items[1].ProposedProviderInvocationId.Value);
        Assert.EndsWith("/action", Assert.Single(set.Items[0].Sources).JsonPointer, StringComparison.Ordinal);
        Assert.EndsWith("/actionId", Assert.Single(set.Items[1].Sources).JsonPointer, StringComparison.Ordinal);
        var issue = Assert.Single(set.Issues);
        Assert.Equal(InteractionDecoder.ConflictingTwinsIssueCode, issue.Code);
        Assert.Equal(2, issue.Sources.Length);
    }

    [Fact]
    public void DecodeApprovalIntents_SameNameTwice_IsNeverDeduplicated()
    {
        var status = Confirmation(Calls(("call-1", "Send", "{}"), ("call-2", "Send", "{\"to\":\"x\"}")), null);

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceValidation.Valid, set.Validation);
        Assert.Equal(2, set.Items.Length);
        Assert.Equal("approval-intent-0-0", set.Items[0].ObservationId);
        Assert.Equal("approval-intent-0-1", set.Items[1].ObservationId);
        Assert.Equal("{}", set.Items[0].ProposedArgumentsRawText.Value);
        Assert.Equal("{\"to\":\"x\"}", set.Items[1].ProposedArgumentsRawText.Value);
        Assert.All(set.Items, i => Assert.Equal("Send", i.Tool.FunctionDisplayName.Value));
        Assert.All(set.Items, i => Assert.Single(i.Sources));
    }

    [Fact]
    public void DecodeApprovalIntents_TwinsDifferingOnlyOutsideTheCalls_AreMerged()
    {
        // The array framing differs by whitespace; every call element is byte-identical, so the rule merges them.
        var status = Confirmation("[{\"id\":\"c\",\"function\":{\"name\":\"F\",\"arguments\":\"{}\"}}]", "[ {\"id\":\"c\",\"function\":{\"name\":\"F\",\"arguments\":\"{}\"}} ]");

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Single(set.Items);
        Assert.Equal(2, set.Items[0].Sources.Length);
        Assert.Empty(set.Issues);
    }

    [Fact]
    public void DecodeApprovalIntents_TwinsDifferingInsideACall_AreNotMerged()
    {
        // Same call identity, same payload, but the bytes of the call element differ (a space): not byte-identical, so both are kept.
        var status = Confirmation("[{\"id\":\"c\",\"function\":{\"name\":\"F\",\"arguments\":\"{}\"}}]", "[{\"id\": \"c\",\"function\":{\"name\":\"F\",\"arguments\":\"{}\"}}]");

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(2, set.Items.Length);
        Assert.Equal(InteractionDecoder.ConflictingTwinsIssueCode, Assert.Single(set.Issues).Code);
    }

    [Fact]
    public void DecodeApprovalIntents_ArgumentsNotJson_KeepsTheRawStringAndMarksTheProjectionInvalid()
    {
        var status = Confirmation(Calls(("call-1", "F", "not json")), null);

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceValidation.Partial, set.Validation);
        var intent = Assert.Single(set.Items);
        Assert.Equal("not json", intent.ProposedArgumentsRawText.Value);
        Assert.Equal(FieldState.Invalid, intent.ProposedArguments.State);
        Assert.Contains(set.Issues, i => i.Code == InteractionDecoder.FieldInvalidIssueCode);
    }

    [Fact]
    public void DecodeApprovalIntents_ArgumentFieldStates_AreMirrored()
    {
        var status = Confirmation(
            "[{\"id\":\"c1\",\"function\":{\"name\":\"F\"}},{\"id\":\"c2\",\"function\":{\"name\":\"F\",\"arguments\":null}},{\"id\":\"c3\",\"function\":{\"name\":\"F\",\"arguments\":{\"x\":1}}},{\"id\":4,\"function\":null}]",
            null);

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(4, set.Items.Length);
        Assert.Equal(FieldState.Missing, set.Items[0].ProposedArgumentsRawText.State);
        Assert.Equal(FieldState.Missing, set.Items[0].ProposedArguments.State);
        Assert.Equal(FieldState.Null, set.Items[1].ProposedArgumentsRawText.State);
        Assert.Equal(FieldState.Null, set.Items[1].ProposedArguments.State);
        Assert.Equal(FieldState.Invalid, set.Items[2].ProposedArgumentsRawText.State);
        Assert.Equal(FieldState.Invalid, set.Items[2].ProposedArguments.State);
        Assert.Equal(FieldState.Invalid, set.Items[3].ProposedProviderInvocationId.State);
        Assert.Equal(FieldState.Null, set.Items[3].Tool.FunctionDisplayName.State);
        Assert.Equal(EvidenceValidation.Partial, set.Validation);
    }

    [Fact]
    public void DecodeApprovalIntents_TwinNotJson_IsReportedAndYieldsNothing()
    {
        var status = Confirmation("[oops", null);

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceAvailability.Provided, set.Availability);
        Assert.Equal(EvidenceValidation.Malformed, set.Validation);
        Assert.Empty(set.Items);
        Assert.Equal(InteractionDecoder.TwinUnreadableIssueCode, Assert.Single(set.Issues).Code);
    }

    [Fact]
    public void DecodeApprovalIntents_NonObjectCall_IsKeptInvalid()
    {
        var status = Confirmation("[\"call\", 7]", null);

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceValidation.Malformed, set.Validation);
        Assert.Equal(2, set.Items.Length);
        Assert.All(set.Items, i => Assert.Equal(FieldState.Invalid, i.ProposedArgumentsRawText.State));
        Assert.Equal(2, set.Issues.Count(i => i.Code == InteractionDecoder.CallNotObjectIssueCode));
    }

    [Fact]
    public void DecodeApprovalIntents_CancelOnlyCard_IsObservedEmpty()
    {
        var status = Confirmation(null, null, withAllow: false);

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.True(set.IsObservedEmpty);
    }

    [Fact]
    public void DecodeApprovalIntents_NoInteractionPart_IsNotProvided()
    {
        var set = InteractionDecoder.DecodeApprovalIntents(MessageWithParts(Text("Working on it")), MessageSource);

        Assert.Equal(EvidenceAvailability.NotProvided, set.Availability);
        Assert.Equal("/status/message/parts", Assert.Single(set.Sources).JsonPointer);
    }

    [Fact]
    public void DecodeApprovalIntents_NullMessage_IsNotProvidedPointingAtTheMessage()
    {
        var set = InteractionDecoder.DecodeApprovalIntents(null, MessageSource);

        Assert.Equal(EvidenceAvailability.NotProvided, set.Availability);
        Assert.Equal("/status/message", Assert.Single(set.Sources).JsonPointer);
    }

    [Fact]
    public void DecodeApprovalIntents_AuthRequiredFixture_IsNotProvided()
    {
        var set = InteractionDecoder.DecodeApprovalIntents(FixtureStatus(AuthFixture), MessageSource);

        Assert.Equal(EvidenceAvailability.NotProvided, set.Availability);
    }

    [Fact]
    public void DecodeApprovalIntents_InputRequiredWithoutCardAndNotAConfirmation_IsNotProvided()
    {
        var status = MessageWithParts(Data("""{"input-required":{"type":"free_text","sourceRequestId":"req-9"}}""", InteractionDecoder.InteractionMediaType));

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceAvailability.NotProvided, set.Availability);
    }

    [Fact]
    public void DecodeApprovalIntents_ConfirmationWithoutCard_IsMalformed()
    {
        var status = MessageWithParts(Data("""{"input-required":{"type":"confirmation_required","sourceRequestId":"req-9"}}""", InteractionDecoder.InteractionMediaType));

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceValidation.Malformed, set.Validation);
        Assert.Equal(InteractionDecoder.CardMalformedIssueCode, Assert.Single(set.Issues).Code);
    }

    [Fact]
    public void DecodeApprovalIntents_TwoInputRequiredParts_IsConflicting()
    {
        var part = Data("""{"input-required":{"type":"confirmation_required","adaptiveCard":{"actions":[]}}}""", InteractionDecoder.InteractionMediaType);
        var status = MessageWithParts(part, part);

        var set = InteractionDecoder.DecodeApprovalIntents(status, MessageSource);

        Assert.Equal(EvidenceValidation.Conflicting, set.Validation);
        Assert.Empty(set.Items);
        Assert.Equal(2, set.Sources.Length);
        Assert.Equal(InteractionDecoder.AmbiguousPartIssueCode, Assert.Single(set.Issues).Code);
    }

    // ---------------------------------------------------------------------------------------------------------
    // TryDecode (the host's view of the pause)
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TryDecode_ConfirmationFixture_IsRecognisedWithOptionsAndCalls()
    {
        var task = SnapshotFromEnvelope(ConfirmationFixture);

        var pending = InteractionDecoder.TryDecode(task.StatusMessage!, task.Handle, CaptureId);

        Assert.NotNull(pending);
        Assert.Same(task.Handle, pending.Task);
        Assert.Equal(WorkIqInteractionKind.ConfirmationRequired, pending.Kind);
        Assert.Equal(ExpectedRequestId, pending.SourceRequestId);
        Assert.Equal("Copilot needs your confirmation before creating the calendar event.", pending.PromptText);
        Assert.Equal(new[] { "Allow", "Cancel" }, pending.RecognizedOptions);
        Assert.Equal(InteractionDecoder.ProfileId, pending.ProfileId);
        Assert.NotNull(pending.RawInteractionData);
        Assert.True(pending.RawInteractionData.Value.TryGetProperty("input-required", out _));
        var source = Assert.Single(pending.Sources);
        Assert.Equal("/status/message/parts/1", source.JsonPointer);
        Assert.Equal(1, source.PartIndex);

        var call = Assert.Single(pending.ProposedCalls);
        Assert.Equal(ExpectedCallId, call.CallId);
        Assert.Equal("function", call.CallType);
        Assert.Equal("CreateDemoEvent", call.FunctionName);
        Assert.StartsWith("{\"subject\":\"Atlas pilot kickoff\"", call.ArgumentsRawText, StringComparison.Ordinal);
        Assert.Equal(FieldState.Present, call.ParsedArguments.State);
        Assert.Equal(2, call.Sources.Length);

        var response = WorkIqInteractionResponse.ForOption(pending, "Allow");
        Assert.Equal(ExpectedRequestId, response.SourceRequestId);
        Assert.Equal(task.Handle.TaskId, response.TaskId);
    }

    [Fact]
    public void TryDecode_AuthRequiredFixture_IsAuthenticationRequiredWithoutCredentials()
    {
        var task = SnapshotFromEnvelope(AuthFixture);

        var pending = InteractionDecoder.TryDecode(task.StatusMessage!, task.Handle, CaptureId);

        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.AuthenticationRequired, pending.Kind);
        Assert.Equal("req-9b4e2d7c-1f6a-4c3e-8d5b-7a0f2e9c4b16", pending.SourceRequestId);
        Assert.Equal("Sign in to the Demo Ticketing connector to continue.", pending.PromptText);
        Assert.Empty(pending.ProposedCalls);
        Assert.Empty(pending.RecognizedOptions);
        Assert.Throws<ArgumentException>(() => WorkIqInteractionResponse.ForOption(pending, "Allow"));
    }

    [Fact]
    public void TryDecode_TextOnlyMessage_ReturnsNull()
    {
        var pending = InteractionDecoder.TryDecode(MessageWithParts(Text("What date?")), Handle(), CaptureId);

        Assert.Null(pending);
    }

    [Fact]
    public void TryDecode_UnrecognisedType_IsUnknownAndNeverAnswerable()
    {
        var status = Confirmation(Calls(("call-1", "F", "{}")), null, type: "something_else");

        var pending = InteractionDecoder.TryDecode(status, Handle(), CaptureId);

        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.Unknown, pending.Kind);
        Assert.Equal("req-test-1", pending.SourceRequestId);
        Assert.Empty(pending.ProposedCalls);
        Assert.Empty(pending.RecognizedOptions);
        Assert.Throws<ArgumentException>(() => WorkIqInteractionResponse.ForOption(pending, "Allow"));
    }

    [Fact]
    public void TryDecode_InteractionPartWithNeitherKey_IsUnknown()
    {
        var status = MessageWithParts(Data("""{"something":{}}""", InteractionDecoder.InteractionMediaType));

        var pending = InteractionDecoder.TryDecode(status, Handle(), CaptureId);

        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.Unknown, pending.Kind);
        Assert.Null(pending.SourceRequestId);
        Assert.Null(pending.PromptText);
    }

    [Fact]
    public void TryDecode_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => InteractionDecoder.TryDecode(null!, Handle(), CaptureId));
        Assert.Throws<ArgumentNullException>(() => InteractionDecoder.TryDecode(MessageWithParts(), null!, CaptureId));
        Assert.Throws<ArgumentException>(() => InteractionDecoder.TryDecode(MessageWithParts(), Handle(), " "));
    }

    // ---------------------------------------------------------------------------------------------------------
    // One card walk: the evidence's approval intents and the host's pending interaction always agree
    // ---------------------------------------------------------------------------------------------------------

    private static readonly SourceRef StatusMessageSource = new(CaptureId, 0, EvidenceOrigin.A2AMessage, "/status/message");

    /// <summary>The interaction data part of a one-action confirmation card.</summary>
    private static WorkIqPart ConfirmationPart(string action) => Confirmation(action, null).Parts[1];

    [Fact]
    public void TwoInputRequiredParts_BothProjectionsRefuseToChoose()
    {
        var part = ConfirmationPart(Calls(("call-1", "CreateDemoEvent", "{}")));
        var status = MessageWithParts(Text("Confirm?"), part, part);

        var intents = InteractionDecoder.DecodeApprovalIntents(status, StatusMessageSource);
        var pending = InteractionDecoder.TryDecode(status, Handle(), CaptureId);

        Assert.Equal(EvidenceValidation.Conflicting, intents.Validation);
        Assert.Empty(intents.Items);
        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.Unknown, pending.Kind);
        Assert.Empty(pending.ProposedCalls);
        Assert.Empty(pending.RecognizedOptions);
        Assert.Null(pending.SourceRequestId);
        Assert.Equal(new[] { "/status/message/parts/1", "/status/message/parts/2" }, pending.Sources.Select(s => s.JsonPointer));
        Assert.Throws<ArgumentException>(() => WorkIqInteractionResponse.ForOption(pending, "Allow"));
    }

    [Fact]
    public void AuthPartThenInputRequiredPart_BothProjectionsRefuseToChoose()
    {
        var auth = Data("""{"auth-required":{"sourceRequestId":"req-auth"}}""", InteractionDecoder.InteractionMediaType);
        var status = MessageWithParts(Text("Confirm?"), auth, ConfirmationPart(Calls(("call-1", "CreateDemoEvent", "{}"))));

        var intents = InteractionDecoder.DecodeApprovalIntents(status, StatusMessageSource);
        var pending = InteractionDecoder.TryDecode(status, Handle(), CaptureId);

        Assert.Equal(EvidenceValidation.Conflicting, intents.Validation);
        Assert.Empty(intents.Items);
        Assert.Equal(InteractionDecoder.AmbiguousPartIssueCode, Assert.Single(intents.Issues).Code);
        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.Unknown, pending.Kind);
        Assert.Empty(pending.ProposedCalls);
    }

    [Fact]
    public void TwoAuthRequiredParts_NoApprovalIntentAndAnUnknownPause()
    {
        var auth = Data("""{"auth-required":{"sourceRequestId":"req-auth"}}""", InteractionDecoder.InteractionMediaType);
        var status = MessageWithParts(Text("Sign in"), auth, auth);

        var intents = InteractionDecoder.DecodeApprovalIntents(status, StatusMessageSource);
        var pending = InteractionDecoder.TryDecode(status, Handle(), CaptureId);

        Assert.Equal(EvidenceAvailability.NotProvided, intents.Availability);
        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.Unknown, pending.Kind);
        Assert.Null(pending.SourceRequestId);
    }

    [Fact]
    public void NonPausePartBeforeTheCard_BothProjectionsReadTheCard()
    {
        var other = Data("""{"something":{}}""", InteractionDecoder.InteractionMediaType);
        var status = MessageWithParts(Text("Confirm?"), other, ConfirmationPart(Calls(("call-1", "CreateDemoEvent", "{\"a\":1}"))));

        var intents = InteractionDecoder.DecodeApprovalIntents(status, StatusMessageSource);
        var pending = InteractionDecoder.TryDecode(status, Handle(), CaptureId);

        Assert.Single(intents.Items);
        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.ConfirmationRequired, pending.Kind);
        Assert.Equal("req-test-1", pending.SourceRequestId);
        Assert.Equal("/status/message/parts/2", Assert.Single(pending.Sources).JsonPointer);
        AssertProjectionsAgree(status);
    }

    public static TheoryData<string> CardShapes => new()
    {
        "fixture",
        "differing-twins",
        "same-name-twice",
        "non-object-call",
        "arguments-not-json",
    };

    [Theory]
    [MemberData(nameof(CardShapes))]
    public void EveryProposedCall_IsTheApprovalIntentOfTheSameCardWalk(string shape)
    {
        var status = shape switch
        {
            "fixture" => FixtureStatus(ConfirmationFixture),
            "differing-twins" => Confirmation(Calls(("call-1", "F", "{}")), Calls(("call-2", "G", "{}"))),
            "same-name-twice" => Confirmation(Calls(("call-1", "F", "{}"), ("call-2", "F", "{}")), null),
            "non-object-call" => Confirmation("[1]", null),
            _ => Confirmation(Calls(("call-1", "F", "not json")), null),
        };

        AssertProjectionsAgree(status);
    }

    private static void AssertProjectionsAgree(WorkIqMessage status)
    {
        var intents = InteractionDecoder.DecodeApprovalIntents(status, StatusMessageSource);
        var pending = InteractionDecoder.TryDecode(status, Handle(), CaptureId);

        Assert.NotNull(pending);
        Assert.Equal(WorkIqInteractionKind.ConfirmationRequired, pending.Kind);
        Assert.NotEmpty(intents.Items);
        Assert.Equal(intents.Items.Length, pending.ProposedCalls.Length);
        for (var i = 0; i < intents.Items.Length; i++)
        {
            var intent = intents.Items[i];
            var call = pending.ProposedCalls[i];
            Assert.Equal(intent.ProposedProviderInvocationId.IsPresent ? intent.ProposedProviderInvocationId.Value : null, call.CallId);
            Assert.Equal(intent.Tool.FunctionDisplayName.IsPresent ? intent.Tool.FunctionDisplayName.Value : null, call.FunctionName);
            Assert.Equal(intent.ProposedArgumentsRawText.IsPresent ? intent.ProposedArgumentsRawText.Value : null, call.ArgumentsRawText);
            Assert.Equal(intent.ProposedArguments.State, call.ParsedArguments.State);
            // Element by element: ImmutableArray's own Equals compares array references, and the two projections are decoded separately.
            Assert.Equal(intent.Sources.ToArray(), call.Sources.ToArray());
        }
    }
}
