// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Microsoft365;

/// <summary>
/// Implemented by the exceptions that carry the immutable <see cref="WorkIqTurnResult"/> of a turn that did not
/// complete, so a harness that keeps <c>TestResult.Error</c> can recover the typed observation without a generic
/// exception framework. A provider contract, not a core one.
/// </summary>
public interface IWorkIqTurnResultCarrier
{
    /// <summary>The immutable rich result of the turn.</summary>
    WorkIqTurnResult TurnResult { get; }
}

/// <summary>
/// The turn ended in any outcome other than <see cref="WorkIqTurnOutcome.Completed"/> (input/auth required,
/// failed, rejected, canceled by the service, or indeterminate). Thrown by <see cref="WorkIqEvaluableAgent"/> so
/// the generic harness never grades partial or blank text as an answer; the typed result (including partial
/// artifacts and the pending interaction) is on <see cref="TurnResult"/>. The message is a safe summary: outcome,
/// ids and failure category — no tenant content.
/// </summary>
public sealed class WorkIqTurnNotCompletedException : Exception, IWorkIqTurnResultCarrier
{
    /// <summary>Creates the exception for <paramref name="turnResult"/>.</summary>
    /// <param name="turnResult">The non-completed turn result.</param>
    /// <param name="innerException">The underlying exception, when any.</param>
    /// <exception cref="ArgumentException"><paramref name="turnResult"/> is a completed result.</exception>
    public WorkIqTurnNotCompletedException(WorkIqTurnResult turnResult, Exception? innerException = null)
        : base(WorkIqSafeMessages.ForTurn("Work IQ turn did not complete", RequireNotCompleted(turnResult)), innerException)
    {
        TurnResult = turnResult;
    }

    /// <inheritdoc/>
    public WorkIqTurnResult TurnResult { get; }

    private static WorkIqTurnResult RequireNotCompleted(WorkIqTurnResult turnResult)
    {
        ArgumentNullException.ThrowIfNull(turnResult);
        if (turnResult.IsCompleted)
        {
            throw new ArgumentException("A completed turn is returned as an AgentResponse, never thrown.", nameof(turnResult));
        }

        return turnResult;
    }
}

/// <summary>
/// The caller's cancellation token stopped the local wait. An <see cref="OperationCanceledException"/> so every
/// existing cancellation handler recognises it, carrying the original token and the typed result of what was
/// observed before the stop. A local stop is not a remote cancel: the remote task's state is whatever
/// <see cref="TurnResult"/> says (usually <see cref="WorkIqTurnOutcome.Indeterminate"/>).
/// </summary>
public sealed class WorkIqTurnCanceledException : OperationCanceledException, IWorkIqTurnResultCarrier
{
    /// <summary>Creates the exception for <paramref name="turnResult"/>.</summary>
    /// <param name="turnResult">The interrupted turn result.</param>
    /// <param name="cancellationToken">The caller's token that fired.</param>
    /// <param name="innerException">The underlying exception, when any.</param>
    public WorkIqTurnCanceledException(WorkIqTurnResult turnResult, CancellationToken cancellationToken, Exception? innerException = null)
        : base(WorkIqSafeMessages.ForTurn("Work IQ turn was canceled locally", Require(turnResult)), innerException, cancellationToken)
    {
        TurnResult = turnResult;
    }

    /// <inheritdoc/>
    public WorkIqTurnResult TurnResult { get; }

    private static WorkIqTurnResult Require(WorkIqTurnResult turnResult)
    {
        ArgumentNullException.ThrowIfNull(turnResult);
        return turnResult;
    }
}

/// <summary>
/// The agent hit its <see cref="WorkIqOptions.MaxTurns"/> cap: the turn that would have crossed it was refused
/// <b>before</b> any send. Turns are counted; consumption is not metered — Work IQ exposes no cost, and Copilot
/// Credits are metered elsewhere. Extends <see cref="FatalEvaluationException"/> so every orchestration loop
/// (eval dataset, bench scenarios) stops once instead of repeating the identical error per remaining item.
/// </summary>
public sealed class WorkIqTurnBudgetExceededException : FatalEvaluationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="turnsUsed">Turns counted so far on this agent: every turn the conversation started, completed or not (<see cref="WorkIqEvaluableAgent.TurnsUsed"/>).</param>
    /// <param name="maxTurns">The configured cap.</param>
    public WorkIqTurnBudgetExceededException(int turnsUsed, int maxTurns)
        : base(
            $"Work IQ evaluation stopped: {turnsUsed} turn(s) already used; the next turn would exceed MaxTurns ({maxTurns}). " +
            "Turns are counted, consumption is not metered — the Work IQ A2A service exposes no per-turn cost, so this cap " +
            "bounds the number of sends, not spend. Check the tenant's own usage reporting before raising the cap.")
    {
        TurnsUsed = turnsUsed;
        MaxTurns = maxTurns;
    }

    /// <summary>Turns counted so far on this agent: every turn the conversation started, completed or not (<see cref="WorkIqEvaluableAgent.TurnsUsed"/>).</summary>
    public int TurnsUsed { get; }

    /// <summary>The configured cap.</summary>
    public int MaxTurns { get; }
}

/// <summary>
/// The conversation refused a turn or subscription before it started, because another one still owns the conversation.
/// Nothing was sent. An <see cref="InvalidOperationException"/>, so a caller that catches that type still sees it;
/// <see cref="WorkIqEvaluableAgent"/> reads this type so the refused turn does not count toward
/// <see cref="WorkIqOptions.MaxTurns"/>.
/// </summary>
internal sealed class WorkIqTurnRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the turn was refused.</param>
    public WorkIqTurnRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A protocol-level failure raised by the transport layer (discovery, trust policy, a request, or a stream)
/// before a turn result exists, carrying the structured <see cref="WorkIqFailure"/>. The message is the failure's
/// safe message with its category and operation.
/// </summary>
public sealed class WorkIqProtocolException : Exception
{
    /// <summary>Creates the exception for <paramref name="failure"/>.</summary>
    /// <param name="failure">The structured failure.</param>
    /// <param name="innerException">The underlying exception, when any.</param>
    public WorkIqProtocolException(WorkIqFailure failure, Exception? innerException = null)
        : base(WorkIqSafeMessages.ForFailure(Require(failure)), innerException)
    {
        Failure = failure;
    }

    /// <summary>The structured failure.</summary>
    public WorkIqFailure Failure { get; }

    private static WorkIqFailure Require(WorkIqFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure;
    }
}

/// <summary>Builds exception messages that carry ids and categories only — never prompts, answers, tokens or auth URLs.</summary>
internal static class WorkIqSafeMessages
{
    /// <summary>Summarises a turn result: outcome, transport, answer completeness, task/context ids and the failure summary.</summary>
    public static string ForTurn(string prefix, WorkIqTurnResult result)
    {
        var message =
            $"{prefix}: outcome={result.Outcome}, transport={result.Transport}, answer={result.AnswerCompleteness}, " +
            $"task={result.Task?.TaskId ?? "(none)"}, context={result.ContextId ?? "(none)"}, turn={result.Identity.TurnId}";
        if (result.Failure is not null)
        {
            message += $"; failure={result.Failure.Category}/{result.Failure.Certainty}: {result.Failure.SafeMessage}";
        }

        return message + ". The input was not resent; the typed result is on TurnResult.";
    }

    /// <summary>Summarises a failure: category, operation, HTTP/RPC codes and correlation ids.</summary>
    public static string ForFailure(WorkIqFailure failure)
    {
        var message = $"Work IQ {failure.Operation} failed: {failure.Category} ({failure.Certainty}): {failure.SafeMessage}";
        if (failure.HttpStatus is not null) { message += $"; http={failure.HttpStatus}"; }
        if (failure.RpcErrorCode is not null) { message += $"; rpcError={failure.RpcErrorCode}"; }
        if (failure.RequestId is not null) { message += $"; request-id={failure.RequestId}"; }
        if (failure.TaskId is not null) { message += $"; task={failure.TaskId}"; }
        if (failure.ContextId is not null) { message += $"; context={failure.ContextId}"; }
        return message;
    }
}
