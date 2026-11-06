// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using A2A;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Microsoft365.Evidence.Decoders;
using Microsoft.Identity.Client;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>
/// <see cref="WorkIqIssueClassifier"/>: every HTTP, JSON-RPC and category mapping of <c>FromFailure</c> with the
/// plain-language causes, the correlation facts copied onto the issue, and <c>FromException</c> for the exception
/// types a run can throw (provider exceptions, MSAL with and without an AADSTS code, HTTP with and without a
/// status, timeouts, a cancelled token, JSON, the SDK's A2AException, socket/TLS/IO, aggregates and unknowns).
/// </summary>
public sealed class WorkIqIssueClassifierTests
{
    private const string Jws = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static WorkIqFailure Failure(
        WorkIqFailureCategory category,
        int? httpStatus = null,
        int? rpcErrorCode = null,
        WorkIqOperation operation = WorkIqOperation.Send,
        OutcomeCertainty certainty = OutcomeCertainty.Known,
        string? remoteTaskState = null,
        string safeMessage = "safe message") =>
        new(category, safeMessage, certainty, operation, httpStatus, rpcErrorCode, remoteTaskState, "req-1", "rpc-1", "task-1", "ctx-1");

    private static WorkIqTurnResult TurnResult(WorkIqTurnOutcome outcome, WorkIqFailure? failure)
    {
        var identity = new TurnIdentity("run-1", "case-7", "turn-3", 0, 0, "rpc-1", "msg-1", null, null, null);
        var evidence = new Microsoft365TurnEvidence(
            Microsoft365Evidence.SchemaVersion,
            Microsoft365Evidence.NormalizerVersion,
            Microsoft365Evidence.ProviderProfileVersion,
            Microsoft365Evidence.A2AVersion,
            identity,
            "TASK_STATE_WORKING",
            CaptureState.NotRetained,
            null,
            EvidenceSet<ToolSelectionObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<ToolExecutionObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<ApprovalIntentObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<RetrievalExecutionObservation>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<CitationMarker>.NotRequested(EvidenceScope.Turn),
            EvidenceSet<CitationSource>.NotRequested(EvidenceScope.Turn),
            ImmutableArray<CitationLink>.Empty,
            EvidenceSet<SkillActivationObservation>.NotRequested(EvidenceScope.Turn),
            ImmutableArray<RawCaptureRef>.Empty,
            ImmutableArray<EvidenceIssue>.Empty);
        var timing = new WorkIqTurnTiming(Start, null, null, null, Start.AddSeconds(1), 0);
        return new WorkIqTurnResult(outcome, TransportOutcome.Failed, OutputCompleteness.None, TelemetryCompleteness.NotRequested, null, "ctx-9", null, evidence, null, failure, timing, identity);
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromFailure: HTTP statuses
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(401, WorkIqIssueCategory.Authentication, "http.401", "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask")]
    [InlineData(403, WorkIqIssueCategory.Permission, "http.403", "admin consent for WorkIQAgent.Ask")]
    [InlineData(404, WorkIqIssueCategory.Config, "http.404", "agent id or endpoint path wrong")]
    [InlineData(429, WorkIqIssueCategory.Service, "http.429", "rate limited")]
    [InlineData(500, WorkIqIssueCategory.Service, "http.500", "HTTP 500")]
    [InlineData(502, WorkIqIssueCategory.Service, "http.502", "degraded")]
    [InlineData(503, WorkIqIssueCategory.Service, "http.503", "a send is never retried")]
    [InlineData(400, WorkIqIssueCategory.Service, "http.400", "HTTP 400")]
    public void FromFailure_HttpStatus_MapsCategoryCodeAndCause(int status, WorkIqIssueCategory category, string code, string causeFragment)
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.Unknown, httpStatus: status));

        Assert.Equal(category, issue.Category);
        Assert.Equal(code, issue.Code);
        Assert.Contains(causeFragment, issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Equal(status, issue.HttpStatus);
    }

    [Fact]
    public void FromFailure_401_SaysReRunTheDeviceCodeSignIn()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.Unauthorized, httpStatus: 401));

        Assert.Equal(WorkIqIssueClassifier.Cause401, issue.SuggestedCause);
        Assert.Contains("device-code", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFailure_403_NamesConsentTheBillingPlanAndWorkIqEnablement()
    {
        // Microsoft's quickstart: a 403 that names the required scopes is missing consent; one without is a user
        // outside the usage-based billing plan. Work IQ not enabled also refuses the call.
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.Forbidden, httpStatus: 403));

        Assert.Equal(WorkIqIssueClassifier.Cause403, issue.SuggestedCause);
        Assert.Contains("admin consent for WorkIQAgent.Ask", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("usage-based billing plan", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("Work IQ is not enabled", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("licence", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFailure_404OnTheCard_ConfigWithTheAgentIdCause()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.NotFound, httpStatus: 404, operation: WorkIqOperation.Discover));

        Assert.Equal(WorkIqIssueCategory.Config, issue.Category);
        Assert.Equal("http.404", issue.Code);
        Assert.Equal(WorkIqTraceStage.Discovery, issue.Stage);
        Assert.Contains("reachable by this user", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFailure_404AsTaskNotFound_ProtocolExpectedOnTheDoctorHandshake()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.NotFound, httpStatus: 404, rpcErrorCode: -32001, operation: WorkIqOperation.GetTask));

        Assert.Equal(WorkIqIssueCategory.Protocol, issue.Category);
        Assert.Equal("rpc.task_not_found", issue.Code);
        Assert.Equal(WorkIqTraceStage.GetTask, issue.Stage);
        Assert.Contains("doctor handshake", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Equal(-32001, issue.RpcErrorCode);
        Assert.Equal(404, issue.HttpStatus);
    }

    [Theory]
    [InlineData("rate limited; retry-after=30", "30 s")]
    [InlineData("HTTP 429 Retry-After: 120", "120 s")]
    public void FromFailure_429WithRetryAfterInTheMessage_CauseNamesTheWait(string safeMessage, string expected)
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RateLimited, httpStatus: 429, safeMessage: safeMessage));

        Assert.Equal(WorkIqIssueCategory.Service, issue.Category);
        Assert.Contains("Retry-After: " + expected, issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFailure_429WithExplicitRetryAfter_ParameterWins()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RateLimited, httpStatus: 429, safeMessage: "retry-after=5"), retryAfter: "Wed, 05 Nov 2026 10:00:00 GMT");

        Assert.Contains("Retry-After: Wed, 05 Nov 2026 10:00:00 GMT", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry-After: 5 s", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFailure_429WithoutRetryAfter_CauseStillSaysWait()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RateLimited));

        Assert.Equal("http.429", issue.Code);
        Assert.DoesNotContain("Retry-After:", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("a send is never retried", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFailure_401WithAnRpcCode_AuthenticationWins()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RemoteError, httpStatus: 401, rpcErrorCode: -32603));

        Assert.Equal(WorkIqIssueCategory.Authentication, issue.Category);
        Assert.Equal("http.401", issue.Code);
        Assert.Equal(-32603, issue.RpcErrorCode);
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromFailure: JSON-RPC codes
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-32009, WorkIqIssueCategory.Protocol, "rpc.version_not_supported", "A2A-Version 1.0 is required")]
    [InlineData(-32601, WorkIqIssueCategory.Protocol, "rpc.method_not_found", "does not implement this JSON-RPC method")]
    [InlineData(-32001, WorkIqIssueCategory.Protocol, "rpc.task_not_found", "not proof it never existed")]
    [InlineData(-32002, WorkIqIssueCategory.Service, "rpc.task_not_cancelable", "terminal state")]
    [InlineData(-32600, WorkIqIssueCategory.Protocol, "rpc.invalid_request", "envelope")]
    [InlineData(-32602, WorkIqIssueCategory.Protocol, "rpc.invalid_params", "parameters")]
    [InlineData(-32700, WorkIqIssueCategory.Protocol, "rpc.parse_error", "parse")]
    [InlineData(-32603, WorkIqIssueCategory.Service, "rpc.internal_error", "internal error")]
    [InlineData(-32005, WorkIqIssueCategory.Service, "rpc.content_type_not_supported", "PublicAnswerOnly")]
    [InlineData(-32050, WorkIqIssueCategory.Service, "rpc.error", "JSON-RPC error -32050")]
    public void FromFailure_RpcErrorCode_MapsCategoryCodeAndCause(int rpc, WorkIqIssueCategory category, string code, string causeFragment)
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RemoteError, httpStatus: 200, rpcErrorCode: rpc));

        Assert.Equal(category, issue.Category);
        Assert.Equal(code, issue.Code);
        Assert.Contains(causeFragment, issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Equal(rpc, issue.RpcErrorCode);
    }

    [Fact]
    public void ForRpcError_EverySdkCode_HasADedicatedCode()
    {
        foreach (var code in Enum.GetValues<A2AErrorCode>())
        {
            var (category, issueCode, cause) = WorkIqIssueClassifier.ForRpcError((int)code);

            Assert.StartsWith("rpc.", issueCode, StringComparison.Ordinal);
            Assert.NotEqual("rpc.error", issueCode);
            Assert.True(category is WorkIqIssueCategory.Protocol or WorkIqIssueCategory.Service);
            Assert.False(string.IsNullOrWhiteSpace(cause));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromFailure: categories
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkIqFailureCategory.ProtocolVersionMismatch, WorkIqOperation.Send, WorkIqIssueCategory.Protocol, "protocol.version_mismatch", "A2A-Version 1.0 is required")]
    [InlineData(WorkIqFailureCategory.Unauthorized, WorkIqOperation.Send, WorkIqIssueCategory.Authentication, "http.401", "device-code")]
    [InlineData(WorkIqFailureCategory.Forbidden, WorkIqOperation.Send, WorkIqIssueCategory.Permission, "http.403", "administrator")]
    [InlineData(WorkIqFailureCategory.RateLimited, WorkIqOperation.GetTask, WorkIqIssueCategory.Service, "http.429", "rate limited")]
    [InlineData(WorkIqFailureCategory.NotFound, WorkIqOperation.Send, WorkIqIssueCategory.Config, "http.404", "endpoint path")]
    [InlineData(WorkIqFailureCategory.NotFound, WorkIqOperation.GetTask, WorkIqIssueCategory.Protocol, "rpc.task_not_found", "task not found")]
    [InlineData(WorkIqFailureCategory.NotFound, WorkIqOperation.CancelTask, WorkIqIssueCategory.Protocol, "rpc.task_not_found", "task not found")]
    [InlineData(WorkIqFailureCategory.NotFound, WorkIqOperation.Subscribe, WorkIqIssueCategory.Protocol, "rpc.task_not_found", "task not found")]
    [InlineData(WorkIqFailureCategory.UntrustedHost, WorkIqOperation.Discover, WorkIqIssueCategory.Config, "discovery.untrusted_host", "TrustedHosts")]
    [InlineData(WorkIqFailureCategory.RemoteError, WorkIqOperation.Send, WorkIqIssueCategory.Service, "remote.error", "answered with an error")]
    [InlineData(WorkIqFailureCategory.Transport, WorkIqOperation.Send, WorkIqIssueCategory.Network, "transport.error", "DNS, TLS, socket or connection")]
    [InlineData(WorkIqFailureCategory.Timeout, WorkIqOperation.Send, WorkIqIssueCategory.Network, "transport.timeout", "SendTimeout")]
    [InlineData(WorkIqFailureCategory.Timeout, WorkIqOperation.Stream, WorkIqIssueCategory.Network, "transport.timeout", "streaming path")]
    [InlineData(WorkIqFailureCategory.Timeout, WorkIqOperation.Local, WorkIqIssueCategory.Budget, "budget.exceeded", "TurnDeadline")]
    [InlineData(WorkIqFailureCategory.Malformed, WorkIqOperation.Send, WorkIqIssueCategory.Protocol, "protocol.malformed", "A2A 1.0 shape")]
    [InlineData(WorkIqFailureCategory.Canceled, WorkIqOperation.Send, WorkIqIssueCategory.Budget, "local.canceled", "not cancelled")]
    [InlineData(WorkIqFailureCategory.Unknown, WorkIqOperation.Unknown, WorkIqIssueCategory.Unknown, "failure.unknown", "not classified")]
    public void FromFailure_Category_MapsCategoryCodeAndCause(WorkIqFailureCategory category, WorkIqOperation operation, WorkIqIssueCategory expectedCategory, string code, string causeFragment)
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(category, operation: operation));

        Assert.Equal(expectedCategory, issue.Category);
        Assert.Equal(code, issue.Code);
        Assert.Contains(causeFragment, issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TASK_STATE_FAILED", "task.failed", "never an answer")]
    [InlineData("TASK_STATE_REJECTED", "task.rejected", "refused the task")]
    [InlineData("TASK_STATE_CANCELED", "task.canceled", "confirmed")]
    [InlineData("TASK_STATE_WORKING", "remote.error", "answered with an error")]
    [InlineData(null, "remote.error", "answered with an error")]
    public void FromFailure_RemoteErrorWithATerminalState_NamesTheState(string? state, string code, string causeFragment)
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RemoteError, remoteTaskState: state));

        Assert.Equal(WorkIqIssueCategory.Service, issue.Category);
        Assert.Equal(code, issue.Code);
        Assert.Contains(causeFragment, issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Equal(state, issue.A2AState);
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromFailure: correlation, stage, redaction
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void FromFailure_Always_CopiesEveryCorrelationFactAndStampsNothing()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RemoteError, httpStatus: 200, rpcErrorCode: -32603, remoteTaskState: "TASK_STATE_FAILED"));

        Assert.Equal(default, issue.At);
        Assert.Null(issue.RunId);
        Assert.Null(issue.ConfigSha256);
        Assert.Null(issue.AdapterVersion);
        Assert.Null(issue.A2AVersion);
        Assert.Equal(200, issue.HttpStatus);
        Assert.Equal(-32603, issue.RpcErrorCode);
        Assert.Equal("TASK_STATE_FAILED", issue.A2AState);
        Assert.Equal("req-1", issue.RequestId);
        Assert.Equal("task-1", issue.TaskId);
        Assert.Equal("ctx-1", issue.ContextId);
        Assert.Contains("Send: RemoteError (Known): safe message", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkIqOperation.Discover, WorkIqTraceStage.Discovery)]
    [InlineData(WorkIqOperation.Send, WorkIqTraceStage.Send)]
    [InlineData(WorkIqOperation.Stream, WorkIqTraceStage.Stream)]
    [InlineData(WorkIqOperation.GetTask, WorkIqTraceStage.GetTask)]
    [InlineData(WorkIqOperation.CancelTask, WorkIqTraceStage.Cancel)]
    [InlineData(WorkIqOperation.Subscribe, WorkIqTraceStage.Subscribe)]
    [InlineData(WorkIqOperation.Local, WorkIqTraceStage.Reduce)]
    [InlineData(WorkIqOperation.Unknown, WorkIqTraceStage.Outcome)]
    public void FromFailure_Stage_DerivesFromTheOperationUnlessOverridden(WorkIqOperation operation, WorkIqTraceStage expected)
    {
        Assert.Equal(expected, WorkIqIssueClassifier.StageFor(operation));
        Assert.Equal(expected, WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.Transport, operation: operation)).Stage);
        Assert.Equal(WorkIqTraceStage.Doctor, WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.Transport, operation: operation), WorkIqTraceStage.Doctor).Stage);
    }

    [Fact]
    public void FromFailure_SafeMessageWithAnAadstsCodeAndAToken_ExtractsTheCodeAndRedacts()
    {
        var issue = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.Unauthorized, httpStatus: 401, safeMessage: "AADSTS70011 for Bearer " + Jws));

        Assert.Equal("AADSTS70011", issue.AadstsCode);
        Assert.DoesNotContain("eyJ", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Bearer " + WorkIqRedaction.RedactedMarker, issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromFailure_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => WorkIqIssueClassifier.FromFailure(null!));

    // ---------------------------------------------------------------------------------------------------------
    // FromException: provider exceptions
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void FromException_WorkIqProtocolException_UsesItsFailureAndTheGivenStage()
    {
        var ex = new WorkIqProtocolException(Failure(WorkIqFailureCategory.UntrustedHost, operation: WorkIqOperation.Discover));

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Doctor);

        Assert.Equal(WorkIqIssueCategory.Config, issue.Category);
        Assert.Equal("discovery.untrusted_host", issue.Code);
        Assert.Equal(WorkIqTraceStage.Doctor, issue.Stage);
        Assert.Equal("req-1", issue.RequestId);
    }

    [Fact]
    public void FromException_TurnNotCompletedWithAFailure_TakesTheTurnCode_TheFailuresCategoryAndCause_AndTheTurnIds()
    {
        var failure = new WorkIqFailure(WorkIqFailureCategory.Timeout, "no reply", OutcomeCertainty.Indeterminate, WorkIqOperation.Send);
        var ex = new WorkIqTurnNotCompletedException(TurnResult(WorkIqTurnOutcome.Indeterminate, failure));

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Outcome);

        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal("turn.indeterminate", issue.Code);
        Assert.Equal(WorkIqIssueClassifier.CauseTimeout, issue.SuggestedCause);
        Assert.Contains("Send: Timeout (Indeterminate): no reply", issue.Message, StringComparison.Ordinal);
        Assert.Equal("case-7", issue.CaseId);
        Assert.Equal("turn-3", issue.TurnId);
        Assert.Equal("ctx-9", issue.ContextId);
        Assert.Equal("TASK_STATE_WORKING", issue.A2AState);
        Assert.Null(issue.TaskId);
    }

    [Theory]
    [InlineData(WorkIqTurnOutcome.Failed, "TASK_STATE_FAILED", "turn.failed")]
    [InlineData(WorkIqTurnOutcome.Rejected, "TASK_STATE_REJECTED", "turn.rejected")]
    [InlineData(WorkIqTurnOutcome.Canceled, "TASK_STATE_CANCELED", "turn.canceled")]
    public void FromException_TerminalTurnWithItsRemoteErrorFailure_IsTheConversationsTurnCode(WorkIqTurnOutcome outcome, string state, string code)
    {
        // The shape the conversation builds: a failed, rejected or canceled task always carries a RemoteError failure
        // with the task state. The doctor's probe and census classify that result; they must say what the turn registered.
        var failure = Failure(WorkIqFailureCategory.RemoteError, remoteTaskState: state);
        var result = TurnResult(outcome, failure);

        var issue = WorkIqIssueClassifier.FromException(new WorkIqTurnNotCompletedException(result), WorkIqTraceStage.Outcome);

        Assert.Equal(code, issue.Code);
        Assert.Equal(WorkIqConversation.OutcomeIssueCode(outcome), issue.Code);
        Assert.Equal(WorkIqIssueClassifier.ForTurnOutcome(result), (issue.Category, issue.Code, issue.SuggestedCause));
        Assert.Equal(WorkIqIssueCategory.Service, issue.Category);
        Assert.Equal(state, issue.A2AState);
        Assert.Equal("task-1", issue.TaskId);
        Assert.Equal("req-1", issue.RequestId);
    }

    [Fact]
    public void FromException_FailedTurnWithA403_IsTurnFailed_WithThePermissionCauseAndTheStatus()
    {
        var result = TurnResult(WorkIqTurnOutcome.Failed, Failure(WorkIqFailureCategory.Forbidden, httpStatus: 403));

        var issue = WorkIqIssueClassifier.FromException(new WorkIqTurnNotCompletedException(result), WorkIqTraceStage.Outcome);

        Assert.Equal("turn.failed", issue.Code);
        Assert.Equal(WorkIqIssueCategory.Permission, issue.Category);
        Assert.Equal(WorkIqIssueClassifier.Cause403, issue.SuggestedCause);
        Assert.Equal(403, issue.HttpStatus);
        Assert.Equal("req-1", issue.RequestId);
    }

    [Fact]
    public void FromException_TurnWithATokenFailure_KeepsTheAuthCode_TheAuthLayerRegisteredIt()
    {
        var failure = new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "sign-in not completed (nothing was sent)", OutcomeCertainty.Known, WorkIqOperation.Send)
        {
            TokenFailure = WorkIqTokenFailure.SignInNotCompleted,
        };

        var issue = WorkIqIssueClassifier.FromException(new WorkIqTurnNotCompletedException(TurnResult(WorkIqTurnOutcome.Failed, failure)), WorkIqTraceStage.Outcome);

        Assert.Equal("auth.sign_in_not_completed", issue.Code);
        Assert.Equal(WorkIqIssueCategory.Authentication, issue.Category);
        Assert.Equal("turn-3", issue.TurnId);
    }

    [Fact]
    public void ForTurnOutcome_TurnDeadlineOrRecoveryBudget_IsBudgetWithACauseThatNamesIt_NeverANetworkTimeout()
    {
        var deadline = new WorkIqFailure(WorkIqFailureCategory.Timeout, "deadline", OutcomeCertainty.Indeterminate, WorkIqOperation.Send) { LocalBudget = WorkIqLocalBudget.TurnDeadline };
        var recovery = new WorkIqFailure(WorkIqFailureCategory.Timeout, "recovery", OutcomeCertainty.Indeterminate, WorkIqOperation.GetTask) { LocalBudget = WorkIqLocalBudget.RecoveryBudget };

        var (deadlineCategory, deadlineCode, deadlineCause) = WorkIqIssueClassifier.ForTurnOutcome(TurnResult(WorkIqTurnOutcome.Indeterminate, deadline));
        var (recoveryCategory, recoveryCode, recoveryCause) = WorkIqIssueClassifier.ForTurnOutcome(TurnResult(WorkIqTurnOutcome.Indeterminate, recovery));

        Assert.Equal((WorkIqIssueCategory.Budget, "turn.indeterminate", WorkIqIssueClassifier.CauseTurnDeadline), (deadlineCategory, deadlineCode, deadlineCause));
        Assert.Equal((WorkIqIssueCategory.Budget, "turn.indeterminate", WorkIqIssueClassifier.CauseRecoveryBudget), (recoveryCategory, recoveryCode, recoveryCause));
        Assert.Contains("TurnDeadline", deadlineCause, StringComparison.Ordinal);
        Assert.Contains("RecoveryBudget", recoveryCause, StringComparison.Ordinal);
        Assert.DoesNotContain("SendTimeout", deadlineCause, StringComparison.Ordinal);
        Assert.DoesNotContain("SendTimeout", recoveryCause, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkIqTurnOutcome.InputRequired, WorkIqIssueCategory.Case, "turn.input_required", "nothing was approved")]
    [InlineData(WorkIqTurnOutcome.AuthRequired, WorkIqIssueCategory.Authentication, "turn.auth_required", "authorization")]
    [InlineData(WorkIqTurnOutcome.Failed, WorkIqIssueCategory.Service, "turn.failed", "never an answer")]
    [InlineData(WorkIqTurnOutcome.Rejected, WorkIqIssueCategory.Service, "turn.rejected", "refused")]
    [InlineData(WorkIqTurnOutcome.Canceled, WorkIqIssueCategory.Service, "turn.canceled", "confirmed")]
    [InlineData(WorkIqTurnOutcome.Indeterminate, WorkIqIssueCategory.Unknown, "turn.indeterminate", "not resent")]
    public void FromException_TurnNotCompletedWithoutAFailure_ClassifiesByOutcome(WorkIqTurnOutcome outcome, WorkIqIssueCategory category, string code, string causeFragment)
    {
        var ex = new WorkIqTurnNotCompletedException(TurnResult(outcome, null));

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Outcome);

        Assert.Equal(category, issue.Category);
        Assert.Equal(code, issue.Code);
        Assert.Contains(causeFragment, issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("outcome=" + outcome, issue.Message, StringComparison.Ordinal);
        Assert.Equal("turn-3", issue.TurnId);
    }

    [Theory]
    [InlineData(WorkIqTurnOutcome.InputRequired, "turn.input_required")]
    [InlineData(WorkIqTurnOutcome.AuthRequired, "turn.auth_required")]
    [InlineData(WorkIqTurnOutcome.Failed, "turn.failed")]
    [InlineData(WorkIqTurnOutcome.Rejected, "turn.rejected")]
    [InlineData(WorkIqTurnOutcome.Canceled, "turn.canceled")]
    [InlineData(WorkIqTurnOutcome.Indeterminate, "turn.indeterminate")]
    public void TurnOutcomeIssueCode_TheConversationAndTheClassifierSpellItTheSame(WorkIqTurnOutcome outcome, string code)
    {
        // One spelling for a turn that did not complete: the conversation's code, the classifier's issue for a turn
        // result (the doctor's probe and census use it) and the turn-level classification all say turn.<outcome>.
        var fromClassifier = WorkIqIssueClassifier.FromException(new WorkIqTurnNotCompletedException(TurnResult(outcome, null)), WorkIqTraceStage.Outcome);

        Assert.Equal(code, WorkIqConversation.OutcomeIssueCode(outcome));
        Assert.Equal(code, fromClassifier.Code);
        Assert.Equal(code, WorkIqIssueClassifier.ForTurnOutcome(TurnResult(outcome, null)).Code);
    }

    [Fact]
    public void TurnCauses_PointATesterAtWhatTheyCanDoNow_NeverAtAnUnsupportedResume()
    {
        // ContinueAsync is not supported in this preview: no cause text may send a tester to resume a task with it.
        foreach (var outcome in Enum.GetValues<WorkIqTurnOutcome>().Where(o => o != WorkIqTurnOutcome.Completed))
        {
            var cause = WorkIqIssueClassifier.ForTurnOutcome(TurnResult(outcome, null)).Cause;

            Assert.DoesNotContain("resume", cause, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ContinueAsync", cause, StringComparison.Ordinal);
            Assert.DoesNotContain("wave", cause, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("sign in to the connector", WorkIqIssueClassifier.CauseAuthRequired, StringComparison.Ordinal);
        Assert.Contains("run the case again", WorkIqIssueClassifier.CauseAuthRequired, StringComparison.Ordinal);
        Assert.Contains("rephrase the case", WorkIqIssueClassifier.CauseInputRequired, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // ForTurnOutcome: the one issue a turn that did not complete registers
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ForTurnOutcome_LocalCancel_IsBudgetWithTheLocalCause_NeverService()
    {
        var failure = new WorkIqFailure(WorkIqFailureCategory.Canceled, "stopped", OutcomeCertainty.Indeterminate, WorkIqOperation.Send);

        var (category, code, cause) = WorkIqIssueClassifier.ForTurnOutcome(TurnResult(WorkIqTurnOutcome.Indeterminate, failure));

        Assert.Equal(WorkIqIssueCategory.Budget, category);
        Assert.Equal("turn.indeterminate", code);
        Assert.Equal(WorkIqIssueClassifier.CauseLocalCanceled, cause);
    }

    [Fact]
    public void ForTurnOutcome_FailedWithA403_IsPermissionWithTheConsentCause()
    {
        var (category, code, cause) = WorkIqIssueClassifier.ForTurnOutcome(TurnResult(WorkIqTurnOutcome.Failed, Failure(WorkIqFailureCategory.Forbidden, httpStatus: 403)));

        Assert.Equal(WorkIqIssueCategory.Permission, category);
        Assert.Equal("turn.failed", code);
        Assert.Equal(WorkIqIssueClassifier.Cause403, cause);
    }

    [Theory]
    [InlineData(WorkIqTurnOutcome.InputRequired)]
    [InlineData(WorkIqTurnOutcome.AuthRequired)]
    public void ForTurnOutcome_PausedTurnWithAFailure_KeepsThePauseCause(WorkIqTurnOutcome outcome)
    {
        var (category, code, cause) = WorkIqIssueClassifier.ForTurnOutcome(TurnResult(outcome, Failure(WorkIqFailureCategory.RemoteError)));

        // A pause points at who can act on it: the case for a confirmation, the sign-in for a connector authorization.
        Assert.Equal(outcome == WorkIqTurnOutcome.AuthRequired ? WorkIqIssueCategory.Authentication : WorkIqIssueCategory.Case, category);
        Assert.Equal(WorkIqConversation.OutcomeIssueCode(outcome), code);
        Assert.Equal(outcome == WorkIqTurnOutcome.AuthRequired ? WorkIqIssueClassifier.CauseAuthRequired : WorkIqIssueClassifier.CauseInputRequired, cause);
    }

    [Fact]
    public void ForTurnOutcome_WithoutAFailure_AgreesWithTheIssueFromATurnResult()
    {
        foreach (var outcome in Enum.GetValues<WorkIqTurnOutcome>().Where(o => o != WorkIqTurnOutcome.Completed))
        {
            var (category, code, cause) = WorkIqIssueClassifier.ForTurnOutcome(TurnResult(outcome, null));
            var issue = WorkIqIssueClassifier.FromException(new WorkIqTurnNotCompletedException(TurnResult(outcome, null)), WorkIqTraceStage.Outcome);

            Assert.Equal((issue.Category, issue.Code, issue.SuggestedCause), (category, code, cause));
        }
    }

    [Fact]
    public void ForTurnOutcome_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => WorkIqIssueClassifier.ForTurnOutcome(null!));

    // ---------------------------------------------------------------------------------------------------------
    // ForFailure: the census every failure path relies on
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ForFailure_EveryCategoryAndOperation_IsClassifiedLikeFromFailure_AndALocalCancelIsAlwaysBudget()
    {
        foreach (var category in Enum.GetValues<WorkIqFailureCategory>())
        {
            foreach (var operation in Enum.GetValues<WorkIqOperation>())
            {
                var failure = new WorkIqFailure(category, "safe message", OutcomeCertainty.Indeterminate, operation);

                var (issueCategory, code, cause) = WorkIqIssueClassifier.ForFailure(failure);
                var issue = WorkIqIssueClassifier.FromFailure(failure);

                Assert.Equal((issue.Category, issue.Code, issue.SuggestedCause), (issueCategory, code, cause));
                Assert.False(string.IsNullOrWhiteSpace(code), $"{category}/{operation}");
                Assert.False(string.IsNullOrWhiteSpace(cause), $"{category}/{operation}");
                if (category == WorkIqFailureCategory.Canceled)
                {
                    Assert.Equal(WorkIqIssueCategory.Budget, issueCategory);
                    Assert.Equal("local.canceled", code);
                }
                else if (category != WorkIqFailureCategory.Unknown)
                {
                    Assert.NotEqual(WorkIqIssueCategory.Unknown, issueCategory);
                }
            }
        }
    }

    [Theory]
    [InlineData(WorkIqTokenFailure.SignInNotCompleted, "auth.sign_in_not_completed", "Nothing was sent")]
    [InlineData(WorkIqTokenFailure.EmptyToken, "auth.empty_token", "empty token, so nothing was sent")]
    [InlineData(WorkIqTokenFailure.ProviderFailed, "auth.token_acquisition_failed", "nothing was sent")]
    public void FromFailure_NoToken_IsNoRejectedToken_AndNothingWasSent(WorkIqTokenFailure tokenFailure, string code, string causeFragment)
    {
        var failure = new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "no token (nothing was sent)", OutcomeCertainty.Known, WorkIqOperation.Send)
        {
            TokenFailure = tokenFailure,
        };

        var issue = WorkIqIssueClassifier.FromFailure(failure, WorkIqTraceStage.Auth);

        Assert.Equal(WorkIqIssueCategory.Authentication, issue.Category);
        Assert.Equal(code, issue.Code);
        Assert.Contains(causeFragment, issue.SuggestedCause, StringComparison.Ordinal);
        Assert.DoesNotContain("token rejected", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Equal(tokenFailure == WorkIqTokenFailure.SignInNotCompleted, WorkIqIssueClassifier.IsSignInNotCompleted(failure));
    }

    [Fact]
    public void FromFailure_ProviderFailedWithAConsentRefusal_IsPermissionWithTheCatalogCause()
    {
        var failure = new WorkIqFailure(WorkIqFailureCategory.Forbidden, "AADSTS65001: consent missing (nothing was sent)", OutcomeCertainty.Known, WorkIqOperation.Send)
        {
            TokenFailure = WorkIqTokenFailure.ProviderFailed,
        };

        var issue = WorkIqIssueClassifier.FromFailure(failure, WorkIqTraceStage.Auth);

        Assert.Equal(WorkIqIssueCategory.Permission, issue.Category);
        Assert.Equal("auth.token_acquisition_failed", issue.Code);
        Assert.StartsWith("AADSTS65001: ", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Equal("AADSTS65001", issue.AadstsCode);
    }

    [Fact]
    public void IsSignInNotCompleted_ReadsTheStructuredTokenFailure()
    {
        Assert.False(WorkIqIssueClassifier.IsSignInNotCompleted(null));
        Assert.False(WorkIqIssueClassifier.IsSignInNotCompleted(new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "sign-in not completed", OutcomeCertainty.Known, WorkIqOperation.Send)));
        Assert.False(WorkIqIssueClassifier.IsSignInNotCompleted(new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "x", OutcomeCertainty.Known) { TokenFailure = WorkIqTokenFailure.EmptyToken }));
        Assert.True(WorkIqIssueClassifier.IsSignInNotCompleted(new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "x", OutcomeCertainty.Known) { TokenFailure = WorkIqTokenFailure.SignInNotCompleted }));
    }

    [Fact]
    public void TaskCauses_TheRemoteErrorAndTheTurnOutcomeShareOneText()
    {
        var remoteFailed = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RemoteError, remoteTaskState: "TASK_STATE_FAILED"));
        var remoteRejected = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RemoteError, remoteTaskState: "TASK_STATE_REJECTED"));
        var remoteCanceled = WorkIqIssueClassifier.FromFailure(Failure(WorkIqFailureCategory.RemoteError, remoteTaskState: "TASK_STATE_CANCELED"));

        Assert.Equal(WorkIqIssueClassifier.CauseTaskFailed, remoteFailed.SuggestedCause);
        Assert.Equal(WorkIqIssueClassifier.CauseTaskRejected, remoteRejected.SuggestedCause);
        Assert.Equal(WorkIqIssueClassifier.CauseTaskCanceled, remoteCanceled.SuggestedCause);
        Assert.Equal(WorkIqIssueClassifier.CauseTaskFailed, WorkIqIssueClassifier.ForTurnOutcome(TurnResult(WorkIqTurnOutcome.Failed, null)).Cause);
        Assert.Equal(WorkIqIssueClassifier.CauseTaskRejected, WorkIqIssueClassifier.ForTurnOutcome(TurnResult(WorkIqTurnOutcome.Rejected, null)).Cause);
        Assert.Equal(WorkIqIssueClassifier.CauseTaskCanceled, WorkIqIssueClassifier.ForTurnOutcome(TurnResult(WorkIqTurnOutcome.Canceled, null)).Cause);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Causes of evidence and reduction issues (one home for tester-facing cause text)
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkIqEvidenceNormalizer.AnswerAmbiguousIssueCode, "several answer candidates")]
    [InlineData(Native118DiagnosticsDecoder.AmbiguousArtifactIssueCode, "never picks the last one")]
    [InlineData(InteractionDecoder.ConflictingTwinsIssueCode, "both proposals are kept")]
    [InlineData(InteractionDecoder.AmbiguousPartIssueCode, "more than one pause")]
    [InlineData(Native118DiagnosticsDecoder.FieldInvalidIssueCode, "native 1.18 shapes")]
    [InlineData(InteractionDecoder.CardMalformedIssueCode, "interaction profile")]
    [InlineData("citations.malformed", "sample-observed shapes")]
    [InlineData("something.else", "source pointers")]
    public void CauseForEvidenceIssue_NamesWhatTheTesterReads(string code, string fragment) =>
        Assert.Contains(fragment, WorkIqIssueClassifier.CauseForEvidenceIssue(code), StringComparison.Ordinal);

    [Theory]
    [InlineData("status.task_id_mismatch", "different from the task")]
    [InlineData("artifact.before_task_snapshot", "before any task snapshot")]
    [InlineData("status.before_task_snapshot", "before any task snapshot")]
    [InlineData("artifact.append_after_last_chunk", "after lastChunk")]
    [InlineData("artifact.missing_id", "missing or duplicated artifactId")]
    [InlineData("message.second_direct_message", "A direct message arrived")]
    [InlineData("part.one_of", "one-of text")]
    [InlineData("task.state_unspecified", "state vocabulary")]
    [InlineData("frame.malformed", "not applied")]
    [InlineData("other", "does not repair")]
    public void CauseForReduceWarning_NamesWhatTheTesterReads(string code, string fragment) =>
        Assert.Contains(fragment, WorkIqIssueClassifier.CauseForReduceWarning(code), StringComparison.Ordinal);

    [Fact]
    public void TurnOutcomeIssueCode_EveryOutcome_IsSnakeCase()
    {
        var codes = Enum.GetValues<WorkIqTurnOutcome>().ToDictionary(o => o, WorkIqConversation.OutcomeIssueCode);

        Assert.All(codes.Values, c => Assert.Matches("^turn\\.[a-z]+(_[a-z]+)*$", c));
        Assert.Equal("turn.input_required", codes[WorkIqTurnOutcome.InputRequired]);
        Assert.Equal("turn.auth_required", codes[WorkIqTurnOutcome.AuthRequired]);
        Assert.Equal("turn.failed", codes[WorkIqTurnOutcome.Failed]);
        Assert.Equal("turn.canceled", codes[WorkIqTurnOutcome.Canceled]);
    }

    [Fact]
    public void FromException_TurnCanceledLocally_IsALocalStopNotATimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var failure = new WorkIqFailure(WorkIqFailureCategory.Canceled, "stopped", OutcomeCertainty.Indeterminate, WorkIqOperation.Send);
        var ex = new WorkIqTurnCanceledException(TurnResult(WorkIqTurnOutcome.Indeterminate, failure), cts.Token);

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Send);

        // The turn's code, as the conversation registers it, with the local stop's category and cause.
        Assert.Equal(WorkIqIssueCategory.Budget, issue.Category);
        Assert.Equal("turn.indeterminate", issue.Code);
        Assert.Equal(WorkIqIssueClassifier.CauseLocalCanceled, issue.SuggestedCause);
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromException: MSAL
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void FromException_MsalServiceExceptionWithAadsts_AuthenticationWithTheCatalogCause()
    {
        var ex = new MsalServiceException("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application with ID '00000000-0000-0000-0000-000000000000'. Trace ID: abc Correlation ID: def Timestamp: 2026-11-05 10:00:00Z");

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Auth);

        Assert.Equal(WorkIqIssueCategory.Authentication, issue.Category);
        Assert.Equal("auth.aadsts", issue.Code);
        Assert.Equal(WorkIqTraceStage.Auth, issue.Stage);
        Assert.Equal("AADSTS65001", issue.AadstsCode);
        Assert.StartsWith("AADSTS65001:", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("admin consent", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("WorkIQAgent.Ask", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("invalid_grant", issue.Message, StringComparison.Ordinal);
        Assert.Contains("AADSTS65001", issue.Message, StringComparison.Ordinal);
        Assert.Null(issue.HttpStatus);
    }

    [Fact]
    public void FromException_MsalServiceExceptionWithStatusCode_CopiesIt()
    {
        var ex = new MsalServiceException("unauthorized_client", "AADSTS7000218: The request body must contain the following parameter: 'client_assertion' or 'client_secret'.", 400);

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Auth);

        Assert.Equal(400, issue.HttpStatus);
        Assert.Equal("AADSTS7000218", issue.AadstsCode);
        Assert.Contains("Allow public client flows", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_MsalUiRequiredWithoutAadsts_DeviceCodeHint()
    {
        var ex = new MsalUiRequiredException("user_null", "No account or login hint was passed to the AcquireTokenSilent call.");

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Auth);

        Assert.Equal(WorkIqIssueCategory.Authentication, issue.Category);
        Assert.Equal("auth.msal", issue.Code);
        Assert.Null(issue.AadstsCode);
        Assert.Contains("device-code", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains("user_null", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_MsalClientExceptionWithUnknownCode_KeepsTheCodeAndPointsAtTheReference()
    {
        var ex = new MsalClientException("some_new_code", "something local went wrong");

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Auth);

        Assert.Equal("auth.msal", issue.Code);
        Assert.Contains("some_new_code", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains(WorkIqAadstsCatalog.MsalReferenceUrl, issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_MsalUnknownAadsts_FallbackKeepsTheRawCode()
    {
        var ex = new MsalServiceException("x", "AADSTS999999: something new");

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Auth);

        Assert.Equal("AADSTS999999", issue.AadstsCode);
        Assert.Contains("AADSTS999999", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Contains(WorkIqAadstsCatalog.ReferenceUrl, issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_MsalMessageWithAToken_Redacted()
    {
        var ex = new MsalClientException("code_expired", "device code expired; cached " + Jws);

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Auth);

        Assert.DoesNotContain("eyJ", issue.Message, StringComparison.Ordinal);
        Assert.Contains("expired before the sign-in", issue.SuggestedCause, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromException: HTTP, timeouts, cancellation
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, WorkIqIssueCategory.Authentication, "http.401")]
    [InlineData(HttpStatusCode.Forbidden, WorkIqIssueCategory.Permission, "http.403")]
    [InlineData(HttpStatusCode.NotFound, WorkIqIssueCategory.Config, "http.404")]
    [InlineData(HttpStatusCode.TooManyRequests, WorkIqIssueCategory.Service, "http.429")]
    [InlineData(HttpStatusCode.InternalServerError, WorkIqIssueCategory.Service, "http.500")]
    [InlineData(HttpStatusCode.ServiceUnavailable, WorkIqIssueCategory.Service, "http.503")]
    [InlineData(HttpStatusCode.BadGateway, WorkIqIssueCategory.Service, "http.502")]
    public void FromException_HttpRequestExceptionWithStatus_MapsLikeAFailure(HttpStatusCode status, WorkIqIssueCategory category, string code)
    {
        var ex = new HttpRequestException("Response status code does not indicate success", null, status);

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Send);

        Assert.Equal(category, issue.Category);
        Assert.Equal(code, issue.Code);
        Assert.Equal((int)status, issue.HttpStatus);
        Assert.Equal(WorkIqTraceStage.Send, issue.Stage);
    }

    [Fact]
    public void FromException_HttpRequestExceptionNameResolution_NetworkDns()
    {
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.");

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Discovery);

        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal("transport.dns", issue.Code);
        Assert.Contains("DNS", issue.SuggestedCause, StringComparison.Ordinal);
        Assert.Null(issue.HttpStatus);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, "transport.connect")]
    [InlineData(HttpRequestError.SecureConnectionError, "transport.tls")]
    [InlineData(HttpRequestError.ResponseEnded, "transport.response_ended")]
    [InlineData(HttpRequestError.ProxyTunnelError, "transport.proxy")]
    [InlineData(HttpRequestError.Unknown, "transport.error")]
    public void FromException_HttpRequestExceptionWithoutStatus_MapsTheTransportError(HttpRequestError error, string code)
    {
        var issue = WorkIqIssueClassifier.FromException(new HttpRequestException(error, "x"), WorkIqTraceStage.Send);

        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal(code, issue.Code);
    }

    [Fact]
    public void FromException_HttpRequestExceptionWithSocketInner_NetworkConnect()
    {
        var ex = new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused));

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Send);

        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal("transport.connect", issue.Code);
        Assert.Contains("SocketException", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_HttpRequestExceptionWithTlsInner_NetworkTls()
    {
        var ex = new HttpRequestException("ssl", new AuthenticationException("The remote certificate is invalid"));

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Send);

        Assert.Equal("transport.tls", issue.Code);
        Assert.Contains("certificate", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_TaskCanceledWithTimeoutInner_NetworkTimeout()
    {
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.", new TimeoutException());

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Send);

        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal("transport.timeout", issue.Code);
        Assert.Equal(WorkIqIssueClassifier.CauseTimeout, issue.SuggestedCause);
        Assert.Contains("SendTimeout", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_TaskCanceledWithoutAToken_NetworkTimeout()
    {
        var issue = WorkIqIssueClassifier.FromException(new TaskCanceledException(), WorkIqTraceStage.Send);

        Assert.Equal(WorkIqIssueCategory.Network, issue.Category);
        Assert.Equal("transport.timeout", issue.Code);
    }

    [Fact]
    public void FromException_TimeoutException_NetworkTimeout()
    {
        var issue = WorkIqIssueClassifier.FromException(new TimeoutException("slow"), WorkIqTraceStage.GetTask);

        Assert.Equal("transport.timeout", issue.Code);
        Assert.Equal(WorkIqTraceStage.GetTask, issue.Stage);
    }

    [Fact]
    public void FromException_OperationCanceledWithACancelledToken_LocalCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var issue = WorkIqIssueClassifier.FromException(new OperationCanceledException("stop", cts.Token), WorkIqTraceStage.Stream);

        Assert.Equal(WorkIqIssueCategory.Budget, issue.Category);
        Assert.Equal("local.canceled", issue.Code);
        Assert.Contains("not cancelled", issue.SuggestedCause, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_TaskCanceledWithACancelledTokenAndNoTimeoutInner_LocalCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var issue = WorkIqIssueClassifier.FromException(new TaskCanceledException("stop", null, cts.Token), WorkIqTraceStage.Stream);

        Assert.Equal("local.canceled", issue.Code);
    }

    // ---------------------------------------------------------------------------------------------------------
    // FromException: JSON, SDK, sockets, aggregates, unknowns
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void FromException_JsonException_ProtocolMalformedWithThePosition()
    {
        JsonException caught;
        try
        {
            JsonDocument.Parse("{\"a\":").Dispose();
            throw new InvalidOperationException("parse should have failed");
        }
        catch (JsonException ex)
        {
            caught = ex;
        }

        var issue = WorkIqIssueClassifier.FromException(caught, WorkIqTraceStage.Send);

        Assert.Equal(WorkIqIssueCategory.Protocol, issue.Category);
        Assert.Equal("protocol.malformed", issue.Code);
        Assert.Equal(WorkIqIssueClassifier.CauseMalformed, issue.SuggestedCause);
        Assert.Contains("JSON parse failure at path", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(A2AErrorCode.VersionNotSupported, WorkIqIssueCategory.Protocol, "rpc.version_not_supported", -32009)]
    [InlineData(A2AErrorCode.MethodNotFound, WorkIqIssueCategory.Protocol, "rpc.method_not_found", -32601)]
    [InlineData(A2AErrorCode.TaskNotFound, WorkIqIssueCategory.Protocol, "rpc.task_not_found", -32001)]
    [InlineData(A2AErrorCode.TaskNotCancelable, WorkIqIssueCategory.Service, "rpc.task_not_cancelable", -32002)]
    [InlineData(A2AErrorCode.InternalError, WorkIqIssueCategory.Service, "rpc.internal_error", -32603)]
    public void FromException_A2AException_MapsByErrorCode(A2AErrorCode code, WorkIqIssueCategory category, string issueCode, int rpc)
    {
        var issue = WorkIqIssueClassifier.FromException(new A2AException("sdk said no", code), WorkIqTraceStage.Send);

        Assert.Equal(category, issue.Category);
        Assert.Equal(issueCode, issue.Code);
        Assert.Equal(rpc, issue.RpcErrorCode);
        Assert.Contains(code.ToString(), issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_SocketTlsAndIo_Network()
    {
        Assert.Equal("transport.connect", WorkIqIssueClassifier.FromException(new SocketException((int)SocketError.ConnectionReset), WorkIqTraceStage.Stream).Code);
        Assert.Equal("transport.tls", WorkIqIssueClassifier.FromException(new AuthenticationException("bad cert"), WorkIqTraceStage.Discovery).Code);
        Assert.Equal("transport.io", WorkIqIssueClassifier.FromException(new IOException("unexpected EOF"), WorkIqTraceStage.Stream).Code);
        Assert.Equal("transport.connect", WorkIqIssueClassifier.FromException(new IOException("reset", new SocketException((int)SocketError.ConnectionReset)), WorkIqTraceStage.Stream).Code);
        Assert.All(
            new[] { WorkIqTraceStage.Stream, WorkIqTraceStage.Discovery },
            stage => Assert.Equal(WorkIqIssueCategory.Network, WorkIqIssueClassifier.FromException(new IOException("x"), stage).Category));
    }

    [Fact]
    public void FromException_AggregateException_UsesTheFirstInner()
    {
        var ex = new AggregateException(new TimeoutException("slow"), new InvalidOperationException("other"));

        var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Send);

        Assert.Equal("transport.timeout", issue.Code);
    }

    [Fact]
    public void FromException_ConfigStage_ConfigCategoryForConfigShapedExceptions()
    {
        Assert.Equal("config.invalid", WorkIqIssueClassifier.FromException(new InvalidOperationException("WorkIqOptions has negative value(s)"), WorkIqTraceStage.Config).Code);
        Assert.Equal("config.invalid", WorkIqIssueClassifier.FromException(new ArgumentException("tenantId"), WorkIqTraceStage.Config).Code);
        Assert.Equal("config.invalid", WorkIqIssueClassifier.FromException(new FileNotFoundException("m365.json"), WorkIqTraceStage.Config).Code);
        Assert.Equal(WorkIqIssueCategory.Config, WorkIqIssueClassifier.FromException(new FormatException("bad guid"), WorkIqTraceStage.Config).Category);
    }

    [Fact]
    public void FromException_Unknown_UnknownCategoryNamingTheType()
    {
        var issue = WorkIqIssueClassifier.FromException(new InvalidOperationException("Bearer " + Jws), WorkIqTraceStage.Send);

        Assert.Equal(WorkIqIssueCategory.Unknown, issue.Category);
        Assert.Equal("exception.InvalidOperationException", issue.Code);
        Assert.DoesNotContain("eyJ", issue.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromException_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => WorkIqIssueClassifier.FromException(null!, WorkIqTraceStage.Send));

    [Fact]
    public void FromException_Always_ProducesAnIssueTheDiagnosticsAccept()
    {
        var issues = new ListIssueSink();
        var trace = new ListTraceSink();
        using var diagnostics = new WorkIqDiagnostics("run-1", "sha", [trace], [issues]);

        diagnostics.Issue(WorkIqIssueClassifier.FromException(new MsalServiceException("x", "AADSTS50058: silent"), WorkIqTraceStage.Auth));

        var issue = Assert.Single(issues.Issues);
        Assert.Equal("run-1", issue.RunId);
        Assert.Equal("AADSTS50058", issue.AadstsCode);
        var mirrored = Assert.Single(trace.Events);
        Assert.Equal("issue.authentication", mirrored.Code);
        Assert.Equal("auth.aadsts", mirrored.Data!.Value.GetProperty("issueCode").GetString());
    }
}
