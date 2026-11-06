// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2A;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using AgentEval.Microsoft365.Protocol;
using Microsoft.Identity.Client;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// The one place that turns a structured <see cref="WorkIqFailure"/>, a turn that did not complete, or a raw
/// exception into an issue's category (who can fix it), stable code and plain-language suggested cause, so an
/// external tester reads the issue register and knows what to do. Every layer that registers an issue for a failure
/// asks this class; none keeps its own table. For a failure the precedence is: a missing token (nothing was sent),
/// HTTP 401/403 (sign-in and consent are the most actionable), then a JSON-RPC error code, then the other
/// HTTP statuses, then the failure category. A local cancellation is always <see cref="WorkIqIssueCategory.Budget"/>
/// (<c>local.canceled</c>), never the service's fault; so is a local budget that ran out (the turn deadline or the
/// recovery budget, <see cref="WorkIqFailure.LocalBudget"/>), with a cause that names it, never a network timeout. A turn
/// that did not complete is coded <c>turn.&lt;outcome&gt;</c> (<see cref="WorkIqConversation.OutcomeIssueCode"/>:
/// <c>turn.failed</c>, <c>turn.rejected</c>, <c>turn.canceled</c>, …), whatever failure it carries, both when the
/// conversation registers it and when a turn result or its exception is classified here; only a token failure keeps
/// its <c>auth.*</c> code. Messages are redacted; every correlation fact the source carried
/// is copied onto the issue. The causes of evidence and reduction issues live here too
/// (<see cref="CauseForEvidenceIssue"/>, <see cref="CauseForReduceWarning"/>).
/// </summary>
internal static partial class WorkIqIssueClassifier
{
    /// <summary>Suggested cause of an HTTP 401.</summary>
    public const string Cause401 =
        "token rejected: wrong audience/scope, expired, or the app registration is not the one consented; re-run the device-code sign-in; the scope must be " +
        WorkIqAadstsCatalog.WorkIqScope;

    /// <summary>Suggested cause of an HTTP 403.</summary>
    public const string Cause403 =
        "the signed-in user or app lacks WorkIQAgent.Ask consent, a Microsoft 365 Copilot licence, or Work IQ is not enabled on the tenant; an administrator must grant consent and enable Work IQ";

    /// <summary>Suggested cause of an HTTP 404 on the card or the endpoint.</summary>
    public const string Cause404 =
        "agent id or endpoint path wrong; the agent must be reachable by this user (check the agent id / card URL in the config; an HTTP 404 here is the endpoint, not a task)";

    /// <summary>Suggested cause of JSON-RPC <c>-32001</c>.</summary>
    public const string CauseTaskNotFound =
        "task not found: the service does not know this task id (inaccessible to this user, expired or purged — not proof it never existed); on the doctor handshake (GetTask on a fake id) this is the expected answer and proves the endpoint speaks A2A 1.0";

    /// <summary>Suggested cause of JSON-RPC <c>-32002</c>.</summary>
    public const string CauseTaskNotCancelable = "the task is already in a terminal state; CancelTask has no effect";

    /// <summary>Suggested cause of JSON-RPC <c>-32009</c> and of a 0.3-shaped reply.</summary>
    public const string CauseVersionNotSupported =
        "the service answered a version we do not speak; A2A-Version 1.0 is required (the request carried A2A-Version: 1.0 and the endpoint rejected it, or the reply has the 0.3 shape)";

    /// <summary>Suggested cause of JSON-RPC <c>-32601</c>.</summary>
    public const string CauseMethodNotFound =
        "the endpoint does not implement this JSON-RPC method; the selected interface is probably not the Work IQ A2A 1.0 JSON-RPC endpoint (wrong path or binding)";

    /// <summary>Suggested cause of a refused untrusted host.</summary>
    public const string CauseUntrustedHost =
        "the agent card or an advertised interface points at a host outside the trusted hosts; a token is never sent there — check the card URL and Microsoft365Config.TrustedHosts";

    /// <summary>Suggested cause of a send timeout.</summary>
    public const string CauseTimeout =
        "no response within SendTimeout; a blocking SendMessage can exceed 300 s on a long task — use the streaming path once it ships, or raise SendTimeout";

    /// <summary>Suggested cause of an exhausted local budget.</summary>
    public const string CauseBudget =
        "a local budget ran out (TurnDeadline or RecoveryBudget) before a terminal task state was observed; the input was not resent — raise the budget or inspect the task by id";

    /// <summary>Suggested cause of a turn stopped by its turn deadline (<see cref="WorkIqOptions.TurnDeadline"/>).</summary>
    public const string CauseTurnDeadline =
        "the turn deadline (TurnDeadline) elapsed before a terminal task state was observed; the request may have reached the service and the input was not resent — raise TurnDeadline or inspect the task by id";

    /// <summary>Suggested cause of a task still running when the recovery budget (<see cref="WorkIqOptions.RecoveryBudget"/>) ran out.</summary>
    public const string CauseRecoveryBudget =
        "the task stayed submitted/working longer than RecoveryBudget allows; the input was not resent — raise RecoveryBudget or inspect the task with GetTask/CancelTask using the handle on the result";

    /// <summary>Suggested cause of a DNS/connection failure.</summary>
    public const string CauseTransport =
        "no response: DNS, TLS, socket or connection failure before the service answered; check connectivity and the proxy; the host must resolve and present a valid certificate";

    /// <summary>Suggested cause of a payload that is not A2A 1.0.</summary>
    public const string CauseMalformed =
        "response not in the A2A 1.0 shape (parse error or one-of violation); the endpoint may speak A2A 0.3, or a gateway returned a non-JSON page";

    /// <summary>Suggested cause of a local cancellation.</summary>
    public const string CauseLocalCanceled =
        "the caller's cancellation token fired (a harness deadline, the turn deadline or a manual stop); the remote task was not cancelled and the input was not resent";

    /// <summary>Suggested cause of an HTTP 5xx.</summary>
    public const string CauseServiceError =
        "the service failed on its side; usually transient — safe reads (card, GetTask, CancelTask) back off and retry, a send is never retried; if it persists the service is degraded, report the request-id";

    /// <summary>Suggested cause of an unclassified failure.</summary>
    public const string CauseUnknown = "not classified; the message says what is known";

    /// <summary>Suggested cause of a task the service reported failed.</summary>
    public const string CauseTaskFailed =
        "the service reported the task failed; the status message in the capture says why (a partial artifact may exist but is never an answer)";

    /// <summary>Suggested cause of a task the service rejected.</summary>
    public const string CauseTaskRejected =
        "the service refused the task (policy, content or capability); the status message in the capture says why";

    /// <summary>Suggested cause of a task the service confirmed cancelled.</summary>
    public const string CauseTaskCanceled = "the service confirmed the task was cancelled";

    /// <summary>Suggested cause of a turn that paused for input or a confirmation.</summary>
    public const string CauseInputRequired =
        "the task is suspended waiting for input or approval; nothing was approved — this provider never answers a confirmation or an input prompt, so rephrase the case so that no action is proposed (the pending interaction on the turn result shows what was asked)";

    /// <summary>Suggested cause of a turn that paused for an external authorization.</summary>
    public const string CauseAuthRequired =
        "the task is suspended waiting for an external authorization (a connector sign-in) that this provider cannot complete; no answer was fabricated — sign in to the connector the status message names in Microsoft 365 (Copilot Chat), then run the case again";

    /// <summary>Suggested cause of a turn whose remote state is unknown.</summary>
    public const string CauseIndeterminate =
        "the remote state is unknown (no terminal state observed before the transport or the budget ended); the input was not resent — inspect the task by id";

    /// <summary>Suggested cause of a token wait that ended before the sign-in finished (<c>auth.sign_in_not_completed</c>).</summary>
    public const string CauseSignInNotCompleted =
        "The sign-in did not finish before the wait ended: a device-code prompt was not completed in time (open the page it names, enter the code, sign in as the test user), or the run was stopped. " +
        "Nothing was sent to Work IQ. Sign in first with `agenteval m365 doctor --stage auth` (the token is cached), then run again.";

    /// <summary>Suggested cause of a token provider that returned an empty token (<c>auth.empty_token</c>).</summary>
    public const string CauseEmptyToken =
        "the token provider returned an empty token, so nothing was sent; re-run the device-code sign-in, or check that the host's token provider returns the Work IQ access token for the scope " +
        WorkIqAadstsCatalog.WorkIqScope;

    /// <summary>Suggested cause of a token provider that failed without an AADSTS code (<c>auth.token_acquisition_failed</c>).</summary>
    public const string CauseTokenAcquisitionFailed =
        "the token provider failed before a token was acquired, so nothing was sent; the message says why — re-run the device-code sign-in; the scope must be " +
        WorkIqAadstsCatalog.WorkIqScope;

    /// <summary>
    /// Whether <paramref name="failure"/> is a token wait stopped before the HTTP call
    /// (<see cref="WorkIqTokenFailure.SignInNotCompleted"/>): nothing was sent, so it is no rejected token and no network timeout.
    /// </summary>
    /// <param name="failure">The failure, or null.</param>
    internal static bool IsSignInNotCompleted(WorkIqFailure? failure) =>
        failure?.TokenFailure == WorkIqTokenFailure.SignInNotCompleted;

    /// <summary>
    /// Classifies a structured failure: category, stable code and suggested cause from a missing token
    /// (<see cref="WorkIqFailure.TokenFailure"/>: <c>auth.sign_in_not_completed</c>, <c>auth.empty_token</c>,
    /// <c>auth.token_acquisition_failed</c>; nothing was sent), its HTTP status, JSON-RPC code and category (in that
    /// precedence), with every correlation fact copied over and an AADSTS code extracted from the safe message when one
    /// is there.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="stage">The stage to stamp; null derives it from <see cref="WorkIqFailure.Operation"/> (<see cref="StageFor"/>).</param>
    /// <param name="retryAfter">The <c>Retry-After</c> value of a 429/503, when the caller has it; null scans the safe message for <c>retry-after=&lt;seconds&gt;</c>.</param>
    public static WorkIqIssue FromFailure(WorkIqFailure failure, WorkIqTraceStage? stage = null, string? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var (category, code, cause) = ForFailure(failure, retryAfter);
        var message = $"{failure.Operation}: {failure.Category} ({failure.Certainty}): {WorkIqRedaction.Redact(failure.SafeMessage)}";
        return new WorkIqIssue(default, stage ?? StageFor(failure.Operation), category, code, message, cause)
        {
            HttpStatus = failure.HttpStatus,
            RpcErrorCode = failure.RpcErrorCode,
            A2AState = failure.RemoteTaskState,
            RequestId = failure.RequestId,
            TaskId = failure.TaskId,
            ContextId = failure.ContextId,
            AadstsCode = WorkIqAadstsCatalog.ExtractCode(failure.SafeMessage),
        };
    }

    /// <summary>
    /// The category, stable code and suggested cause of a structured failure, for a caller that builds its own issue
    /// (its own message and correlation fields): the same answer <see cref="FromFailure"/> stamps on the issue it builds.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="retryAfter">The <c>Retry-After</c> value of a 429/503, when the caller has it; null scans the safe message for <c>retry-after=&lt;seconds&gt;</c>.</param>
    public static (WorkIqIssueCategory Category, string Code, string Cause) ForFailure(WorkIqFailure failure, string? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return ClassifyFailure(failure, retryAfter ?? RetryAfterFrom(failure.SafeMessage));
    }

    /// <summary>
    /// The category, code and suggested cause of the one issue a turn that did not complete registers. The code is
    /// always <see cref="WorkIqConversation.OutcomeIssueCode"/> of the outcome (<c>turn.failed</c>, <c>turn.rejected</c>,
    /// <c>turn.canceled</c>, <c>turn.indeterminate</c>, <c>turn.input_required</c>, <c>turn.auth_required</c>). A paused
    /// turn (input or authorization required) takes the pause's category and cause. Any other turn with a structured
    /// failure takes the failure's category and cause (<see cref="ForFailure"/>), so a local cancellation is
    /// <see cref="WorkIqIssueCategory.Budget"/> and a 403 is <see cref="WorkIqIssueCategory.Permission"/>; without a
    /// failure the outcome decides.
    /// </summary>
    /// <param name="result">The turn result.</param>
    public static (WorkIqIssueCategory Category, string Code, string Cause) ForTurnOutcome(WorkIqTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var code = WorkIqConversation.OutcomeIssueCode(result.Outcome);
        if (result.Failure is { } failure && result.Outcome is not (WorkIqTurnOutcome.InputRequired or WorkIqTurnOutcome.AuthRequired))
        {
            var (category, _, cause) = ForFailure(failure);
            return (category, code, cause);
        }

        var (outcomeCategory, outcomeCause) = ForOutcome(result.Outcome);
        return (outcomeCategory, code, outcomeCause);
    }

    /// <summary>
    /// Classifies a raw exception at <paramref name="stage"/>: a provider exception by its failure or turn result,
    /// an MSAL exception as <see cref="WorkIqIssueCategory.Authentication"/> with the AADSTS code and the catalog
    /// cause, an <see cref="HttpRequestException"/> by its status (or its transport error when there is none), a
    /// timeout as <see cref="WorkIqIssueCategory.Network"/>, a cancelled token as a local stop, a
    /// <see cref="JsonException"/> as <see cref="WorkIqIssueCategory.Protocol"/>, an SDK <see cref="A2AException"/>
    /// by its JSON-RPC code, socket/TLS/IO failures as <see cref="WorkIqIssueCategory.Network"/>, and anything else
    /// as <see cref="WorkIqIssueCategory.Unknown"/> (or <see cref="WorkIqIssueCategory.Config"/> at the config stage).
    /// An <see cref="AggregateException"/> is classified by its first inner exception. The message is redacted.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="stage">The stage it was caught in.</param>
    public static WorkIqIssue FromException(Exception exception, WorkIqTraceStage stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var ex = exception is AggregateException { InnerExceptions.Count: > 0 } aggregate ? aggregate.InnerExceptions[0] : exception;

        switch (ex)
        {
            case WorkIqProtocolException protocol:
                return FromFailure(protocol.Failure, stage);

            case IWorkIqTurnResultCarrier carrier:
                return FromTurnResult(carrier.TurnResult, stage);

            case MsalException msal:
                return FromMsal(msal, stage);

            case HttpRequestException http:
                return FromHttp(http, stage);

            case TaskCanceledException { InnerException: TimeoutException } timedOut:
                return Issue(stage, WorkIqIssueCategory.Network, "transport.timeout", Describe(timedOut), CauseTimeout);

            case TimeoutException timeout:
                return Issue(stage, WorkIqIssueCategory.Network, "transport.timeout", Describe(timeout), CauseTimeout);

            case OperationCanceledException canceled:
                return canceled.CancellationToken.IsCancellationRequested
                    ? Issue(stage, WorkIqIssueCategory.Budget, "local.canceled", Describe(canceled), CauseLocalCanceled)
                    : Issue(stage, WorkIqIssueCategory.Network, "transport.timeout", Describe(canceled), CauseTimeout);

            case JsonException json:
                return Issue(
                    stage,
                    WorkIqIssueCategory.Protocol,
                    "protocol.malformed",
                    $"JSON parse failure at path {json.Path ?? "?"}, line {json.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}: {WorkIqRedaction.Redact(json.Message)}",
                    CauseMalformed);

            case A2AException a2a:
            {
                var rpcCode = (int)a2a.ErrorCode;
                var (category, code, cause) = ForRpcError(rpcCode);
                return Issue(stage, category, code, $"A2A SDK error {a2a.ErrorCode} ({rpcCode}): {WorkIqRedaction.Redact(a2a.Message)}", cause) with { RpcErrorCode = rpcCode };
            }

            case AuthenticationException tls:
                return Issue(stage, WorkIqIssueCategory.Network, "transport.tls", Describe(tls), CauseTls);

            case SocketException socket:
                return Issue(stage, WorkIqIssueCategory.Network, "transport.connect", Describe(socket), CauseConnect);

            case IOException io when stage != WorkIqTraceStage.Config:
                return io.InnerException is SocketException
                    ? Issue(stage, WorkIqIssueCategory.Network, "transport.connect", Describe(io), CauseConnect)
                    : Issue(stage, WorkIqIssueCategory.Network, "transport.io", Describe(io), CauseResponseEnded);

            default:
                if (stage == WorkIqTraceStage.Config && ex is ArgumentException or InvalidOperationException or FormatException or IOException)
                {
                    return Issue(stage, WorkIqIssueCategory.Config, "config.invalid", Describe(ex), "the configuration or the options are invalid or unreadable; the message names the field or the path");
                }

                return Issue(stage, WorkIqIssueCategory.Unknown, "exception." + ex.GetType().Name, Describe(ex), "not classified; the exception type and message say what is known");
        }
    }

    /// <summary>The trace stage a failure's operation belongs to.</summary>
    /// <param name="operation">The operation.</param>
    public static WorkIqTraceStage StageFor(WorkIqOperation operation) => operation switch
    {
        WorkIqOperation.Discover => WorkIqTraceStage.Discovery,
        WorkIqOperation.Send => WorkIqTraceStage.Send,
        WorkIqOperation.Stream => WorkIqTraceStage.Stream,
        WorkIqOperation.GetTask => WorkIqTraceStage.GetTask,
        WorkIqOperation.CancelTask => WorkIqTraceStage.Cancel,
        WorkIqOperation.Subscribe => WorkIqTraceStage.Subscribe,
        WorkIqOperation.Local => WorkIqTraceStage.Reduce,
        _ => WorkIqTraceStage.Outcome,
    };

    /// <summary>Category, code and cause of an HTTP status: 401 → Authentication, 403 → Permission, 404 → Config, 429/5xx/other → Service.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="retryAfter">The <c>Retry-After</c> value, when known.</param>
    public static (WorkIqIssueCategory Category, string Code, string Cause) ForHttpStatus(int status, string? retryAfter = null) => status switch
    {
        401 => (WorkIqIssueCategory.Authentication, "http.401", Cause401),
        403 => (WorkIqIssueCategory.Permission, "http.403", Cause403),
        404 => (WorkIqIssueCategory.Config, "http.404", Cause404),
        429 => (WorkIqIssueCategory.Service, "http.429", Cause429(retryAfter)),
        >= 500 => (WorkIqIssueCategory.Service, "http." + status.ToString(CultureInfo.InvariantCulture), $"HTTP {status}: {CauseServiceError}"),
        >= 400 => (WorkIqIssueCategory.Service, "http." + status.ToString(CultureInfo.InvariantCulture), $"the service refused the request (HTTP {status}); the response body in the capture says why"),
        _ => (WorkIqIssueCategory.Service, "http." + status.ToString(CultureInfo.InvariantCulture), $"unexpected HTTP status {status} for a JSON-RPC call; a proxy or gateway may be answering instead of the service"),
    };

    /// <summary>Category, code and cause of a JSON-RPC error code (<see cref="A2AErrorCode"/> values and the generic JSON-RPC ones).</summary>
    /// <param name="code">The JSON-RPC error code.</param>
    public static (WorkIqIssueCategory Category, string Code, string Cause) ForRpcError(int code) => code switch
    {
        (int)A2AErrorCode.VersionNotSupported => (WorkIqIssueCategory.Protocol, "rpc.version_not_supported", CauseVersionNotSupported),
        (int)A2AErrorCode.MethodNotFound => (WorkIqIssueCategory.Protocol, "rpc.method_not_found", CauseMethodNotFound),
        (int)A2AErrorCode.TaskNotFound => (WorkIqIssueCategory.Protocol, "rpc.task_not_found", CauseTaskNotFound),
        (int)A2AErrorCode.TaskNotCancelable => (WorkIqIssueCategory.Service, "rpc.task_not_cancelable", CauseTaskNotCancelable),
        (int)A2AErrorCode.InvalidRequest => (WorkIqIssueCategory.Protocol, "rpc.invalid_request", "the service rejected the JSON-RPC envelope as invalid; a client serialisation bug, or an endpoint that only speaks A2A 0.3"),
        (int)A2AErrorCode.InvalidParams => (WorkIqIssueCategory.Protocol, "rpc.invalid_params", "the service rejected the request parameters; the error message in the capture names the field"),
        (int)A2AErrorCode.ParseError => (WorkIqIssueCategory.Protocol, "rpc.parse_error", "the service could not parse the request body as JSON; a client bug or a proxy that altered the body"),
        (int)A2AErrorCode.InternalError => (WorkIqIssueCategory.Service, "rpc.internal_error", "the service hit an internal error; transient or a service bug — retry a safe read, never a send, and report the request-id"),
        (int)A2AErrorCode.PushNotificationNotSupported => (WorkIqIssueCategory.Service, "rpc.push_notification_not_supported", "the service does not support push notifications; this provider never uses them, so the request was not ours as sent"),
        (int)A2AErrorCode.UnsupportedOperation => (WorkIqIssueCategory.Service, "rpc.unsupported_operation", "the service does not support this operation; the capability census of the doctor shows what the card advertises"),
        (int)A2AErrorCode.ContentTypeNotSupported => (WorkIqIssueCategory.Service, "rpc.content_type_not_supported", "the service rejected the requested output modes; the diagnostic profile may request a mode this agent does not offer — try PublicAnswerOnly"),
        (int)A2AErrorCode.InvalidAgentResponse => (WorkIqIssueCategory.Service, "rpc.invalid_agent_response", "the agent produced a response the service could not deliver; a service-side fault, report the request-id"),
        (int)A2AErrorCode.ExtendedAgentCardNotConfigured => (WorkIqIssueCategory.Service, "rpc.extended_card_not_configured", "the service has no extended agent card; this provider does not need one"),
        (int)A2AErrorCode.ExtensionSupportRequired => (WorkIqIssueCategory.Service, "rpc.extension_support_required", "the service requires a protocol extension this provider does not declare; the error message names it"),
        _ => (WorkIqIssueCategory.Service, "rpc.error", $"JSON-RPC error {code}; the error message in the capture says why"),
    };

    private const string CauseTls = "the TLS handshake failed (certificate not trusted, TLS interception by a proxy, or a protocol mismatch); the host must present a valid certificate chain";
    private const string CauseConnect = "the connection was refused or reset before a response; check connectivity, firewall and proxy";
    private const string CauseResponseEnded = "the connection ended before the response was complete (reset or EOF mid-body); the operation is recovered by task id when one is known, never by resending";

    private static string Cause429(string? retryAfter) =>
        "rate limited by the service" +
        (string.IsNullOrWhiteSpace(retryAfter) ? "" : $"; Retry-After: {retryAfter.Trim()}") +
        "; wait before the next request — safe reads (card, GetTask, CancelTask) back off automatically with jitter, a send is never retried";

    private static (WorkIqIssueCategory Category, string Code, string Cause) ClassifyFailure(WorkIqFailure failure, string? retryAfter)
    {
        if (failure.TokenFailure is { } tokenFailure)
        {
            return ForTokenFailure(failure, tokenFailure);
        }

        if (failure.HttpStatus is 401)
        {
            return (WorkIqIssueCategory.Authentication, "http.401", Cause401);
        }

        if (failure.HttpStatus is 403)
        {
            return (WorkIqIssueCategory.Permission, "http.403", Cause403);
        }

        if (failure.RpcErrorCode is { } rpc)
        {
            return ForRpcError(rpc);
        }

        if (failure.HttpStatus is { } status && (status == 404 || status == 429 || status >= 500))
        {
            return ForHttpStatus(status, retryAfter);
        }

        switch (failure.Category)
        {
            case WorkIqFailureCategory.ProtocolVersionMismatch:
                return (WorkIqIssueCategory.Protocol, "protocol.version_mismatch", CauseVersionNotSupported);
            case WorkIqFailureCategory.Unauthorized:
                return (WorkIqIssueCategory.Authentication, "http.401", Cause401);
            case WorkIqFailureCategory.Forbidden:
                return (WorkIqIssueCategory.Permission, "http.403", Cause403);
            case WorkIqFailureCategory.RateLimited:
                return (WorkIqIssueCategory.Service, "http.429", Cause429(retryAfter));
            case WorkIqFailureCategory.NotFound:
                return failure.Operation is WorkIqOperation.GetTask or WorkIqOperation.CancelTask or WorkIqOperation.Subscribe
                    ? (WorkIqIssueCategory.Protocol, "rpc.task_not_found", CauseTaskNotFound)
                    : (WorkIqIssueCategory.Config, "http.404", Cause404);
            case WorkIqFailureCategory.UntrustedHost:
                return (WorkIqIssueCategory.Config, "discovery.untrusted_host", CauseUntrustedHost);
            case WorkIqFailureCategory.RemoteError:
                return ForRemoteError(failure);
            case WorkIqFailureCategory.Transport:
                return (WorkIqIssueCategory.Network, "transport.error", CauseTransport);
            case WorkIqFailureCategory.Timeout:
                return failure.LocalBudget switch
                {
                    WorkIqLocalBudget.TurnDeadline => (WorkIqIssueCategory.Budget, "budget.exceeded", CauseTurnDeadline),
                    WorkIqLocalBudget.RecoveryBudget => (WorkIqIssueCategory.Budget, "budget.recovery_exhausted", CauseRecoveryBudget),
                    _ when failure.Operation == WorkIqOperation.Local => (WorkIqIssueCategory.Budget, "budget.exceeded", CauseBudget),
                    _ => (WorkIqIssueCategory.Network, "transport.timeout", CauseTimeout),
                };
            case WorkIqFailureCategory.Malformed:
                return (WorkIqIssueCategory.Protocol, "protocol.malformed", CauseMalformed);
            case WorkIqFailureCategory.Canceled:
                return (WorkIqIssueCategory.Budget, "local.canceled", CauseLocalCanceled);
            default:
                return failure.HttpStatus is { } other
                    ? ForHttpStatus(other, retryAfter)
                    : (WorkIqIssueCategory.Unknown, "failure.unknown", CauseUnknown);
        }
    }

    private static (WorkIqIssueCategory Category, string Code, string Cause) ForRemoteError(WorkIqFailure failure) =>
        WorkIqOutcomeClassifier.ToTaskState(failure.RemoteTaskState) switch
        {
            WorkIqTaskState.Failed => (WorkIqIssueCategory.Service, "task.failed", CauseTaskFailed),
            WorkIqTaskState.Rejected => (WorkIqIssueCategory.Service, "task.rejected", CauseTaskRejected),
            WorkIqTaskState.Canceled => (WorkIqIssueCategory.Service, "task.canceled", CauseTaskCanceled),
            _ => (WorkIqIssueCategory.Service, "remote.error", "the service answered with an error; the HTTP status, the JSON-RPC code and the capture say why"),
        };

    /// <summary>
    /// A failure with no bearer token, so nothing was sent: a sign-in that never finished, an empty token, or a token
    /// provider that failed (a consent or permission refusal is <see cref="WorkIqIssueCategory.Permission"/>; an AADSTS
    /// code in the message gets the catalog's cause).
    /// </summary>
    private static (WorkIqIssueCategory Category, string Code, string Cause) ForTokenFailure(WorkIqFailure failure, WorkIqTokenFailure tokenFailure) => tokenFailure switch
    {
        WorkIqTokenFailure.SignInNotCompleted => (WorkIqIssueCategory.Authentication, "auth.sign_in_not_completed", CauseSignInNotCompleted),
        WorkIqTokenFailure.EmptyToken => (WorkIqIssueCategory.Authentication, "auth.empty_token", CauseEmptyToken),
        _ => (
            failure.Category == WorkIqFailureCategory.Forbidden ? WorkIqIssueCategory.Permission : WorkIqIssueCategory.Authentication,
            "auth.token_acquisition_failed",
            WorkIqAadstsCatalog.ExtractCode(failure.SafeMessage) is { } aadsts ? WorkIqAadstsCatalog.Describe(aadsts) : CauseTokenAcquisitionFailed),
    };

    /// <summary>The category and cause of a turn outcome when no structured failure says more.</summary>
    private static (WorkIqIssueCategory Category, string Cause) ForOutcome(WorkIqTurnOutcome outcome) => outcome switch
    {
        WorkIqTurnOutcome.InputRequired => (WorkIqIssueCategory.Service, CauseInputRequired),
        WorkIqTurnOutcome.AuthRequired => (WorkIqIssueCategory.Service, CauseAuthRequired),
        WorkIqTurnOutcome.Failed => (WorkIqIssueCategory.Service, CauseTaskFailed),
        WorkIqTurnOutcome.Rejected => (WorkIqIssueCategory.Service, CauseTaskRejected),
        WorkIqTurnOutcome.Canceled => (WorkIqIssueCategory.Service, CauseTaskCanceled),
        WorkIqTurnOutcome.Indeterminate => (WorkIqIssueCategory.Unknown, CauseIndeterminate),
        _ => (WorkIqIssueCategory.Unknown, "the turn did not complete; the outcome says what is known"),
    };

    /// <summary>
    /// The issue of a turn result, with the code the conversation registers for it: <see cref="ForTurnOutcome"/>
    /// (<c>turn.&lt;outcome&gt;</c>) with the failure's correlation facts copied over. A failed, rejected or canceled
    /// turn always carries a <see cref="WorkIqFailureCategory.RemoteError"/> failure, and a turn ended by an HTTP error
    /// carries that status; neither changes the code. A token failure keeps its own <c>auth.*</c> code: the auth layer
    /// registered that issue, and the conversation registers no turn issue for it.
    /// </summary>
    private static WorkIqIssue FromTurnResult(WorkIqTurnResult result, WorkIqTraceStage stage)
    {
        var taskId = result.Task?.TaskId;
        var a2aState = result.Evidence?.TaskState;
        var failure = result.Failure;
        if (failure is { TokenFailure: not null })
        {
            var fromFailure = FromFailure(failure, stage);
            return fromFailure with
            {
                CaseId = result.Identity?.CaseId,
                TurnId = result.Identity?.TurnId,
                TaskId = fromFailure.TaskId ?? taskId,
                ContextId = fromFailure.ContextId ?? result.ContextId,
                A2AState = fromFailure.A2AState ?? a2aState,
            };
        }

        var (category, code, cause) = ForTurnOutcome(result);
        var message = failure is null
            ? $"turn {result.Identity?.TurnId ?? "?"}: outcome={result.Outcome}, transport={result.Transport}, answer={result.AnswerCompleteness}"
            : $"{failure.Operation}: {failure.Category} ({failure.Certainty}): {WorkIqRedaction.Redact(failure.SafeMessage)}";
        return new WorkIqIssue(default, stage, category, code, message, cause)
        {
            CaseId = result.Identity?.CaseId,
            TurnId = result.Identity?.TurnId,
            HttpStatus = failure?.HttpStatus,
            RpcErrorCode = failure?.RpcErrorCode,
            RequestId = failure?.RequestId,
            TaskId = failure?.TaskId ?? taskId,
            ContextId = failure?.ContextId ?? result.ContextId,
            A2AState = failure?.RemoteTaskState ?? a2aState,
            AadstsCode = failure is null ? null : WorkIqAadstsCatalog.ExtractCode(failure.SafeMessage),
        };
    }

    private static WorkIqIssue FromMsal(MsalException msal, WorkIqTraceStage stage)
    {
        var aadsts = WorkIqAadstsCatalog.ExtractCode(msal.Message) ?? WorkIqAadstsCatalog.ExtractCode(msal.ErrorCode);
        var cause = aadsts is not null
            ? WorkIqAadstsCatalog.Describe(aadsts)
            : WorkIqAadstsCatalog.DescribeMsalError(msal.ErrorCode, msal is MsalUiRequiredException);
        var code = aadsts is not null ? "auth.aadsts" : "auth.msal";
        int? httpStatus = msal is MsalServiceException { StatusCode: > 0 } service ? service.StatusCode : null;
        var message = $"MSAL {msal.GetType().Name} ({msal.ErrorCode ?? "no error code"}): {WorkIqRedaction.Redact(msal.Message)}";
        if (!string.IsNullOrWhiteSpace(msal.CorrelationId))
        {
            message += $"; correlationId={msal.CorrelationId}";
        }

        return new WorkIqIssue(default, stage, WorkIqIssueCategory.Authentication, code, message, cause)
        {
            HttpStatus = httpStatus,
            AadstsCode = aadsts,
        };
    }

    private static WorkIqIssue FromHttp(HttpRequestException http, WorkIqTraceStage stage)
    {
        if (http.StatusCode is { } status)
        {
            var numeric = (int)status;
            var (category, code, cause) = ForHttpStatus(numeric);
            return Issue(stage, category, code, $"HTTP {numeric}: {WorkIqRedaction.Redact(http.Message)}", cause) with { HttpStatus = numeric };
        }

        var (networkCategory, networkCode, networkCause) = http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => (WorkIqIssueCategory.Network, "transport.dns", "the host name does not resolve (DNS); check the endpoint / card URL and the network or proxy"),
            HttpRequestError.ConnectionError => (WorkIqIssueCategory.Network, "transport.connect", CauseConnect),
            HttpRequestError.SecureConnectionError => (WorkIqIssueCategory.Network, "transport.tls", CauseTls),
            HttpRequestError.ResponseEnded => (WorkIqIssueCategory.Network, "transport.response_ended", CauseResponseEnded),
            HttpRequestError.InvalidResponse => (WorkIqIssueCategory.Protocol, "transport.invalid_response", "the server's response violated HTTP; a proxy or gateway may be answering instead of the service"),
            HttpRequestError.ProxyTunnelError => (WorkIqIssueCategory.Network, "transport.proxy", "the proxy refused the tunnel; check the proxy configuration"),
            _ => http.InnerException switch
            {
                AuthenticationException => (WorkIqIssueCategory.Network, "transport.tls", CauseTls),
                SocketException => (WorkIqIssueCategory.Network, "transport.connect", CauseConnect),
                _ => (WorkIqIssueCategory.Network, "transport.error", CauseTransport),
            },
        };

        return Issue(stage, networkCategory, networkCode, Describe(http), networkCause);
    }

    /// <summary>
    /// The plain-language cause of an evidence normalisation issue (registered as <see cref="WorkIqIssueCategory.Protocol"/>
    /// at the <see cref="WorkIqTraceStage.Normalize"/> stage), keyed by the issue code or its prefix.
    /// </summary>
    /// <param name="code">The evidence issue code (for example <c>answer.ambiguous</c> or <c>diagnostics.field_invalid</c>).</param>
    public static string CauseForEvidenceIssue(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (string.Equals(code, WorkIqEvidenceNormalizer.AnswerAmbiguousIssueCode, StringComparison.Ordinal))
        {
            return "The service returned several answer candidates and the selection policy chose none; inspect the artifacts in the capture and report the shape.";
        }

        if (string.Equals(code, Native118DiagnosticsDecoder.AmbiguousArtifactIssueCode, StringComparison.Ordinal))
        {
            return "Two distinct artifacts play the same diagnostic role; the decoder never picks the last one. Report the capture so the artifact relationship can be decoded.";
        }

        if (string.Equals(code, InteractionDecoder.ConflictingTwinsIssueCode, StringComparison.Ordinal))
        {
            return "The confirmation card's action and actionId payloads differ; both proposals are kept and nothing was approved.";
        }

        if (string.Equals(code, InteractionDecoder.AmbiguousPartIssueCode, StringComparison.Ordinal))
        {
            return "The status message carries more than one pause (input-required or auth-required data parts); none was chosen, no proposal is read from any, and nothing was approved. Report the capture.";
        }

        if (code.StartsWith("diagnostics.", StringComparison.Ordinal))
        {
            return "The diagnostic artifact does not match the native 1.18 shapes this decoder knows (an experimental, undocumented format); the service may have changed it. The raw data stays in the capture.";
        }

        if (code.StartsWith("citations.", StringComparison.Ordinal))
        {
            return "The attribution metadata or the citation markers do not match the sample-observed shapes; the raw data stays in the capture.";
        }

        if (code.StartsWith("approval.", StringComparison.Ordinal))
        {
            return "The confirmation card does not match the interaction profile; the pause is retained raw and is never auto-answered.";
        }

        return "See the capture at the issue's source pointers.";
    }

    /// <summary>
    /// The plain-language cause of a stream deviation the task reducer records (<c>reduce.&lt;code&gt;</c>, registered as
    /// <see cref="WorkIqIssueCategory.Protocol"/>), keyed by the warning code or its prefix/suffix.
    /// </summary>
    /// <param name="code">The warning code without the <c>reduce.</c> prefix (for example <c>status.task_id_mismatch</c>).</param>
    public static string CauseForReduceWarning(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.EndsWith(".task_id_mismatch", StringComparison.Ordinal) || code.EndsWith(".id_mismatch", StringComparison.Ordinal) || code.EndsWith(".context_id_mismatch", StringComparison.Ordinal))
        {
            return "A frame carried a task or context id different from the task this turn is tracking; the service may be multiplexing, the subscription may target another task, or the reply belongs to a previous turn. Check the ids in the raw capture.";
        }

        if (code.EndsWith(".before_task_snapshot", StringComparison.Ordinal))
        {
            return "A status or artifact update arrived before any task snapshot. A conforming stream starts with the task; identity was taken from the update, so earlier state (if any) may be missing.";
        }

        if (code.StartsWith("artifact.append_after_last_chunk", StringComparison.Ordinal))
        {
            return "The service kept sending chunks for an artifact after lastChunk: true. The chunks were applied in order and never de-duplicated; the artifact content may be longer than the service intended.";
        }

        if (code.StartsWith("artifact.", StringComparison.Ordinal))
        {
            return "An artifact in the stream deviates from A2A 1.0 (missing or duplicated artifactId). Nothing was merged by guess; the raw capture holds the exact frames.";
        }

        if (code.StartsWith("message.", StringComparison.Ordinal))
        {
            return "A direct message arrived where exactly one (or none, in a task stream) was expected, or it carries an unexpected role or id. The first message is the reply; later ones are retained separately and never merged into it.";
        }

        if (code.StartsWith("part.", StringComparison.Ordinal))
        {
            return "A part did not follow the one-of text | raw | url | data rule. Content was mapped by the SDK's content case; the raw capture holds the original part.";
        }

        if (code.StartsWith("task.", StringComparison.Ordinal) || code.StartsWith("status.", StringComparison.Ordinal))
        {
            return "The task or status deviates from the expected identity or state vocabulary (unspecified state, missing or changed id). An unknown state never counts as completed.";
        }

        if (code.StartsWith("frame.", StringComparison.Ordinal))
        {
            return "A stream frame violated the A2A 1.0 shape and was not applied; the run is marked interrupted so it cannot pass silently. The raw capture holds the exact bytes.";
        }

        return "The stream deviates from A2A 1.0 in a way this provider records but does not repair; the raw capture holds the exact bytes.";
    }

    private static WorkIqIssue Issue(WorkIqTraceStage stage, WorkIqIssueCategory category, string code, string message, string cause) =>
        new(default, stage, category, code, message, cause);

    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}" +
        (ex.InnerException is { } inner ? $" (inner: {inner.GetType().Name}: {WorkIqRedaction.Redact(inner.Message)})" : "");

    private static string? RetryAfterFrom(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = RetryAfterPattern().Match(text);
        return match.Success ? match.Groups[1].Value + " s" : null;
    }

    [GeneratedRegex(@"retry-?after\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryAfterPattern();
}
