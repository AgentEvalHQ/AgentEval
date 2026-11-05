// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.Json;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>The kind of pause a suspended task is waiting on.</summary>
public enum WorkIqInteractionKind
{
    /// <summary>The pause payload was not recognised by the interaction profile; it is retained raw and never auto-answered.</summary>
    Unknown,

    /// <summary>A confirmation card proposing tool calls (<c>confirmation_required</c>); approval is a host decision.</summary>
    ConfirmationRequired,

    /// <summary>Free-form user input is required.</summary>
    InputRequired,

    /// <summary>An external authorization workflow is required; this provider cannot complete it.</summary>
    AuthenticationRequired,
}

/// <summary>
/// A tool invocation <b>proposed</b> in a confirmation card. Evidence of a request for consent, not of execution;
/// the argument text is kept verbatim and the parsed form is a separate projection.
/// </summary>
/// <param name="CallId">The proposed call id as sent, when any.</param>
/// <param name="CallType">The raw call type as sent, when any.</param>
/// <param name="FunctionName">The function name as sent, when any.</param>
/// <param name="ArgumentsRawText">The exact <c>function.arguments</c> string as sent (itself JSON-encoded), when any.</param>
/// <param name="ParsedArguments">The parsed arguments, when <paramref name="ArgumentsRawText"/> parsed; a parser projection, never regenerated into the raw text.</param>
/// <param name="Sources">Where the proposal was read.</param>
public sealed record WorkIqProposedCall(
    string? CallId,
    string? CallType,
    string? FunctionName,
    string? ArgumentsRawText,
    Observed<JsonElement> ParsedArguments,
    ImmutableArray<SourceRef> Sources);

/// <summary>
/// A suspended task's pause as surfaced to the host: which task/context, the pause's own <c>sourceRequestId</c>,
/// the recognised kind, the proposed calls and the exact option strings the card offers. No credential, and no
/// default answer: nothing is approved unless the host builds a <see cref="WorkIqInteractionResponse"/>.
/// </summary>
/// <param name="Task">The suspended task.</param>
/// <param name="Kind">The recognised kind.</param>
/// <param name="SourceRequestId">The id of <b>this</b> pause (not the task, not a previous pause), when sent.</param>
/// <param name="PromptText">The status-message text shown with the pause, under the content policy; null when not retained.</param>
/// <param name="ProposedCalls">Every proposed call, multiplicity preserved (no name de-duplication).</param>
/// <param name="RecognizedOptions">The exact option strings the recognised profile accepts (for example <c>Allow</c>, <c>Cancel</c>); empty when the kind is <see cref="WorkIqInteractionKind.Unknown"/>.</param>
/// <param name="ProfileId">The versioned interaction profile that recognised the pause.</param>
/// <param name="RawInteractionData">The pause's data part, cloned, under the content policy; null when not retained.</param>
/// <param name="Sources">Where the pause was read.</param>
public sealed record WorkIqPendingInteraction(
    WorkIqTaskHandle Task,
    WorkIqInteractionKind Kind,
    string? SourceRequestId,
    string? PromptText,
    ImmutableArray<WorkIqProposedCall> ProposedCalls,
    ImmutableArray<string> RecognizedOptions,
    string ProfileId,
    JsonElement? RawInteractionData,
    ImmutableArray<SourceRef> Sources)
{
    private readonly JsonElement? _rawInteractionData = JsonElementOwnership.Detach(RawInteractionData, nameof(RawInteractionData));

    /// <summary>The pause's data part, cloned, under the content policy; null when not retained.</summary>
    public JsonElement? RawInteractionData
    {
        get => _rawInteractionData;
        init => _rawInteractionData = JsonElementOwnership.Detach(value, nameof(RawInteractionData));
    }
}

/// <summary>
/// The host's explicit decision or input for one pause, bound to that pause's <c>sourceRequestId</c> and task and
/// validated against the recognised options. Built only through <see cref="ForOption"/> / <see cref="ForInput"/>
/// so an arbitrary adaptive-card action can never be sent.
/// </summary>
public sealed record WorkIqInteractionResponse
{
    private WorkIqInteractionResponse(string taskId, string sourceRequestId, WorkIqInteractionKind kind, string? selectedOption, string? userInput)
    {
        TaskId = taskId;
        SourceRequestId = sourceRequestId;
        Kind = kind;
        SelectedOption = selectedOption;
        UserInput = userInput;
    }

    /// <summary>The task the response continues.</summary>
    public string TaskId { get; }

    /// <summary>The pause the response answers.</summary>
    public string SourceRequestId { get; }

    /// <summary>The kind of pause being answered.</summary>
    public WorkIqInteractionKind Kind { get; }

    /// <summary>The exact option chosen (for example <c>Allow</c>); null for a free-form input response.</summary>
    public string? SelectedOption { get; }

    /// <summary>The free-form input; null for an option response.</summary>
    public string? UserInput { get; }

    /// <summary>Chooses one of the pause's <see cref="WorkIqPendingInteraction.RecognizedOptions"/> (exact, case-sensitive match).</summary>
    /// <param name="interaction">The pause being answered; must carry a <see cref="WorkIqPendingInteraction.SourceRequestId"/>.</param>
    /// <param name="option">The option string.</param>
    /// <exception cref="ArgumentException">The pause has no <c>sourceRequestId</c>, is of an unknown kind, or does not offer <paramref name="option"/>.</exception>
    public static WorkIqInteractionResponse ForOption(WorkIqPendingInteraction interaction, string option)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(option);
        RequireAnswerable(interaction);
        if (!interaction.RecognizedOptions.Contains(option, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The pause does not offer the option '{option}'. Recognized options: {string.Join(", ", interaction.RecognizedOptions)}.",
                nameof(option));
        }

        return new WorkIqInteractionResponse(interaction.Task.TaskId, interaction.SourceRequestId!, interaction.Kind, option, null);
    }

    /// <summary>Answers an <see cref="WorkIqInteractionKind.InputRequired"/> pause with free-form input.</summary>
    /// <param name="interaction">The pause being answered; must carry a <see cref="WorkIqPendingInteraction.SourceRequestId"/>.</param>
    /// <param name="input">The input text.</param>
    /// <exception cref="ArgumentException">The pause has no <c>sourceRequestId</c> or is not an input-required pause.</exception>
    public static WorkIqInteractionResponse ForInput(WorkIqPendingInteraction interaction, string input)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(input);
        RequireAnswerable(interaction);
        if (interaction.Kind != WorkIqInteractionKind.InputRequired)
        {
            throw new ArgumentException($"Free-form input answers an InputRequired pause, not {interaction.Kind}.", nameof(interaction));
        }

        return new WorkIqInteractionResponse(interaction.Task.TaskId, interaction.SourceRequestId!, interaction.Kind, null, input);
    }

    private static void RequireAnswerable(WorkIqPendingInteraction interaction)
    {
        if (string.IsNullOrWhiteSpace(interaction.SourceRequestId))
        {
            throw new ArgumentException("The pause carries no sourceRequestId; it cannot be answered.", nameof(interaction));
        }

        if (interaction.Kind == WorkIqInteractionKind.Unknown)
        {
            throw new ArgumentException("An unrecognised pause is never answered; the profile did not recognise its payload.", nameof(interaction));
        }
    }
}
