// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>
/// <see cref="WorkIqDiagnostics"/>: stamping, redaction, fan-out that survives a failing sink, the ring buffer,
/// the issue mirror event, measured scopes, and the no-op <see cref="WorkIqDiagnostics.None"/>.
/// </summary>
public sealed class WorkIqDiagnosticsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private const string Jws = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static (WorkIqDiagnostics Diagnostics, ListTraceSink Trace, ListIssueSink Issues, FakeTimeProvider Clock) Build(
        string runId = "run-1", string? sha = "sha-1", IWorkIqTraceSink? extraTrace = null, TextWriter? fallback = null)
    {
        var clock = new FakeTimeProvider(Start);
        var trace = new ListTraceSink();
        var issues = new ListIssueSink();
        var traceSinks = extraTrace is null ? new IWorkIqTraceSink[] { trace } : [extraTrace, trace];
        var diagnostics = new WorkIqDiagnostics(runId, sha, traceSinks, [issues], clock) { FallbackWriter = fallback };
        return (diagnostics, trace, issues, clock);
    }

    // ---------------------------------------------------------------------------------------------------------
    // None
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void None_Always_IsANoOp()
    {
        var none = WorkIqDiagnostics.None;

        none.Trace(WorkIqTraceEvent.Create(WorkIqTraceStage.Auth, "auth.x", "m"));
        none.Trace(WorkIqTraceStage.Auth, "auth.y", "m", WorkIqTraceLevel.Error, httpStatus: 401);
        none.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "http.401", "m", "c"));
        using (var scope = none.Measure(WorkIqTraceStage.Send, "send", "m"))
        {
            scope.Observe(taskId: "t").Fail(new InvalidOperationException("x"));
        }

        none.Dispose();

        Assert.True(none.IsNone);
        Assert.Equal(WorkIqDiagnostics.NoneRunId, none.RunId);
        Assert.Null(none.ConfigSha256);
        Assert.Empty(none.RecentEvents);
        Assert.Equal(0, none.IssueCount);
        Assert.Same(WorkIqDiagnostics.None, WorkIqDiagnostics.None);
    }

    [Fact]
    public void None_BlankCode_StillThrows()
    {
        Assert.Throws<ArgumentException>(() => WorkIqDiagnostics.None.Trace(WorkIqTraceStage.Auth, " ", "m"));
        Assert.Throws<ArgumentException>(() => WorkIqDiagnostics.None.Measure(WorkIqTraceStage.Auth, "", "m"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_BlankRunIdOrNullSinks_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkIqDiagnostics(" ", null, [], []));
        Assert.Throws<ArgumentNullException>(() => new WorkIqDiagnostics("r", null, null!, []));
        Assert.Throws<ArgumentNullException>(() => new WorkIqDiagnostics("r", null, [], null!));
        Assert.Throws<ArgumentException>(() => new WorkIqDiagnostics("r", null, [null!], []));
    }

    [Fact]
    public void AdapterVersion_Always_IsTheAssemblyInformationalVersion()
    {
        Assert.False(string.IsNullOrWhiteSpace(WorkIqDiagnostics.AdapterVersion));
        Assert.Equal("1.0", WorkIqDiagnostics.A2AVersion);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Trace
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Trace_UnstampedEvent_StampsAtAndRunId()
    {
        var (diagnostics, trace, _, clock) = Build();
        clock.Advance(TimeSpan.FromSeconds(3));

        diagnostics.Trace(WorkIqTraceEvent.Create(WorkIqTraceStage.Auth, "auth.silent_hit", "cached"));

        var e = Assert.Single(trace.Events);
        Assert.Equal(Start.AddSeconds(3), e.At);
        Assert.Equal("run-1", e.RunId);
        Assert.Equal(e, Assert.Single(diagnostics.RecentEvents));
    }

    [Fact]
    public void Trace_StampedEvent_KeepsExplicitAtAndRunId()
    {
        var (diagnostics, trace, _, _) = Build();
        var at = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        diagnostics.Trace(new WorkIqTraceEvent(at, WorkIqTraceStage.Auth, WorkIqTraceLevel.Info, "c", "m") { RunId = "other-run" });

        var e = Assert.Single(trace.Events);
        Assert.Equal(at, e.At);
        Assert.Equal("other-run", e.RunId);
    }

    [Fact]
    public void Trace_SecretInMessageAndData_IsRedactedBeforeAnySinkSeesIt()
    {
        var (diagnostics, trace, _, _) = Build();
        var data = JsonSerializer.SerializeToElement(new { authorization = "Bearer " + Jws, note = "x" });

        diagnostics.Trace(WorkIqTraceEvent.Create(WorkIqTraceStage.Auth, "auth.header", "sending Bearer " + Jws) with { Data = data });

        var e = Assert.Single(trace.Events);
        Assert.Equal("sending Bearer [redacted]", e.Message);
        Assert.Equal("[redacted]", e.Data!.Value.GetProperty("authorization").GetString());
        Assert.Equal("x", e.Data!.Value.GetProperty("note").GetString());
        Assert.DoesNotContain(Jws, JsonSerializer.Serialize(e), StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_ThrowingSink_IsReportedOnceAndDroppedWhileOthersKeepReceiving()
    {
        var throwing = new ThrowingTraceSink();
        var fallback = new StringWriter();
        var (diagnostics, trace, _, _) = Build(extraTrace: throwing, fallback: fallback);

        diagnostics.Trace(WorkIqTraceStage.Send, "rpc.sent", "one");
        diagnostics.Trace(WorkIqTraceStage.Send, "rpc.sent", "two");

        Assert.Equal(2, trace.Count);
        Assert.Equal(1, throwing.Calls);
        var report = fallback.ToString();
        Assert.Contains("ThrowingTraceSink", report, StringComparison.Ordinal);
        Assert.Contains("dropped", report, StringComparison.Ordinal);
        Assert.Contains("Bearer [redacted]", report, StringComparison.Ordinal);
        Assert.DoesNotContain("sink-secret", report, StringComparison.Ordinal);
        Assert.Single(report.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void RecentEvents_MoreThanCapacity_KeepsTheLast200InOrder()
    {
        var (diagnostics, _, _, _) = Build();

        for (var i = 0; i < 250; i++)
        {
            diagnostics.Trace(WorkIqTraceStage.Stream, "e" + i.ToString("000"), "m");
        }

        var recent = diagnostics.RecentEvents;
        Assert.Equal(WorkIqDiagnostics.RecentEventCapacity, recent.Count);
        Assert.Equal("e050", recent[0].Code);
        Assert.Equal("e249", recent[^1].Code);
    }

    [Fact]
    public void TraceOverload_AllArguments_MapToTheEvent()
    {
        var (diagnostics, trace, _, _) = Build();

        diagnostics.Trace(
            WorkIqTraceStage.GetTask, "task.state", "working", WorkIqTraceLevel.Warning,
            caseId: "case-1", turnId: "turn-1", taskId: "task-1", contextId: "ctx-1", requestId: "req-1",
            httpStatus: 200, rpcErrorCode: -32001, a2aState: "TASK_STATE_WORKING", elapsedMs: 12.5);

        var e = Assert.Single(trace.Events);
        Assert.Equal(WorkIqTraceStage.GetTask, e.Stage);
        Assert.Equal(WorkIqTraceLevel.Warning, e.Level);
        Assert.Equal("case-1", e.CaseId);
        Assert.Equal("turn-1", e.TurnId);
        Assert.Equal("task-1", e.TaskId);
        Assert.Equal("ctx-1", e.ContextId);
        Assert.Equal("req-1", e.RequestId);
        Assert.Equal(200, e.HttpStatus);
        Assert.Equal(-32001, e.RpcErrorCode);
        Assert.Equal("TASK_STATE_WORKING", e.A2AState);
        Assert.Equal(12.5, e.ElapsedMs);
    }

    [Fact]
    public async Task Trace_Concurrently_LosesNothing()
    {
        var (diagnostics, trace, _, _) = Build();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                diagnostics.Trace(WorkIqTraceStage.Stream, "w" + worker, "m");
            }
        })));

        Assert.Equal(2000, trace.Count);
        Assert.Equal(WorkIqDiagnostics.RecentEventCapacity, diagnostics.RecentEvents.Count);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Issue
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Issue_Unstamped_StampsDefaultsAndMirrorsATraceEvent()
    {
        var (diagnostics, trace, issues, _) = Build();

        diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "http.401", "Unauthorized", "The token was rejected: sign in again.")
            with { HttpStatus = 401, RequestId = "req-9", AadstsCode = "AADSTS65001" });

        var issue = Assert.Single(issues.Issues);
        Assert.Equal(Start, issue.At);
        Assert.Equal("run-1", issue.RunId);
        Assert.Equal("sha-1", issue.ConfigSha256);
        Assert.Equal(WorkIqDiagnostics.AdapterVersion, issue.AdapterVersion);
        Assert.Equal("1.0", issue.A2AVersion);
        Assert.Equal(1, diagnostics.IssueCount);

        var e = Assert.Single(trace.Events);
        Assert.Equal("issue.authentication", e.Code);
        Assert.Equal(WorkIqTraceLevel.Error, e.Level);
        Assert.Equal(WorkIqTraceStage.Auth, e.Stage);
        Assert.Equal("Unauthorized", e.Message);
        Assert.Equal(401, e.HttpStatus);
        Assert.Equal("req-9", e.RequestId);
        Assert.Equal(Start, e.At);
        Assert.Equal("http.401", e.Data!.Value.GetProperty("issueCode").GetString());
        Assert.Equal("Authentication", e.Data!.Value.GetProperty("category").GetString());
        Assert.Equal("The token was rejected: sign in again.", e.Data!.Value.GetProperty("suggestedCause").GetString());
        Assert.Equal("AADSTS65001", e.Data!.Value.GetProperty("aadstsCode").GetString());
    }

    [Fact]
    public void Issue_Stamped_KeepsExplicitValues()
    {
        var (diagnostics, _, issues, _) = Build();
        var at = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Config, WorkIqIssueCategory.Config, "cfg", "m", "c")
            with { At = at, RunId = "r0", ConfigSha256 = "s0", AdapterVersion = "v0", A2AVersion = "0.3" });

        var issue = Assert.Single(issues.Issues);
        Assert.Equal(at, issue.At);
        Assert.Equal("r0", issue.RunId);
        Assert.Equal("s0", issue.ConfigSha256);
        Assert.Equal("v0", issue.AdapterVersion);
        Assert.Equal("0.3", issue.A2AVersion);
    }

    [Theory]
    [InlineData(WorkIqTraceLevel.Info, WorkIqTraceLevel.Warning)]
    [InlineData(WorkIqTraceLevel.Warning, WorkIqTraceLevel.Warning)]
    [InlineData(WorkIqTraceLevel.Error, WorkIqTraceLevel.Error)]
    public void Issue_Level_IsNeverMirroredBelowWarning(WorkIqTraceLevel given, WorkIqTraceLevel expected)
    {
        var (diagnostics, trace, _, _) = Build();

        diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Send, WorkIqIssueCategory.Service, "http.429", "m", "c"), given);

        Assert.Equal(expected, Assert.Single(trace.Events).Level);
    }

    [Fact]
    public void Issue_SecretInMessageOrCause_IsRedacted()
    {
        var (diagnostics, trace, issues, _) = Build();

        diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "x", "sent Bearer abc123", "retry with code=XYZ"));

        var issue = Assert.Single(issues.Issues);
        Assert.Equal("sent Bearer [redacted]", issue.Message);
        Assert.Equal("retry with code=[redacted]", issue.SuggestedCause);
        Assert.Equal("sent Bearer [redacted]", Assert.Single(trace.Events).Message);
    }

    [Fact]
    public void Issue_ThrowingIssueSink_IsReportedAndTheTraceStillHappens()
    {
        var fallback = new StringWriter();
        var clock = new FakeTimeProvider(Start);
        var trace = new ListTraceSink();
        var diagnostics = new WorkIqDiagnostics("r", null, [trace], [new ThrowingIssueSink()], clock) { FallbackWriter = fallback };

        diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Send, WorkIqIssueCategory.Service, "x", "m", "c"));
        diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Send, WorkIqIssueCategory.Service, "x", "m", "c"));

        Assert.Equal(2, diagnostics.IssueCount);
        Assert.Equal(2, trace.Count);
        Assert.Contains("issue sink ThrowingIssueSink", fallback.ToString(), StringComparison.Ordinal);
        Assert.Single(fallback.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Measure
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Measure_Disposed_TracesStartThenEndWithElapsed()
    {
        var (diagnostics, trace, _, clock) = Build();

        using (var scope = diagnostics.Measure(WorkIqTraceStage.Discovery, "discovery.card", "Fetching the agent card", caseId: "c1"))
        {
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            Assert.False(scope.IsClosed);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), scope.Elapsed);
        }

        Assert.Equal(2, trace.Count);
        var start = trace.Events[0];
        var end = trace.Events[1];
        Assert.Equal("discovery.card.start", start.Code);
        Assert.Null(start.ElapsedMs);
        Assert.Equal("c1", start.CaseId);
        Assert.Equal("discovery.card.end", end.Code);
        Assert.Equal(WorkIqTraceLevel.Info, end.Level);
        Assert.Equal(1500, end.ElapsedMs);
        Assert.Equal(Start.AddMilliseconds(1500), end.At);
        Assert.Equal("c1", end.CaseId);
    }

    [Fact]
    public void Measure_Failed_TracesFailedWithExceptionTypeInsteadOfEnd()
    {
        var (diagnostics, trace, _, clock) = Build();

        var scope = diagnostics.Measure(WorkIqTraceStage.Send, "rpc.send", "SendMessage");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        scope.Fail(new InvalidOperationException("rejected Bearer " + Jws, new TimeoutException("inner")));
        scope.Dispose();

        Assert.Equal(2, trace.Count);
        var failed = trace.Events[1];
        Assert.Equal("rpc.send.failed", failed.Code);
        Assert.Equal(WorkIqTraceLevel.Error, failed.Level);
        Assert.Equal(250, failed.ElapsedMs);
        Assert.Contains("InvalidOperationException", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Bearer [redacted]", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Jws, failed.Message, StringComparison.Ordinal);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.Data!.Value.GetProperty("exceptionType").GetString());
        Assert.Equal(typeof(TimeoutException).FullName, failed.Data!.Value.GetProperty("innerExceptionType").GetString());
        Assert.True(scope.IsFailed);
        Assert.True(scope.IsClosed);
        Assert.DoesNotContain(trace.Events, e => e.Code.EndsWith(".end", StringComparison.Ordinal));
    }

    [Fact]
    public void Measure_Observe_EnrichesTheClosingEvent()
    {
        var (diagnostics, trace, _, _) = Build();

        using (var scope = diagnostics.Measure(WorkIqTraceStage.Send, "rpc.send", "SendMessage"))
        {
            scope.Observe(taskId: "task-1", contextId: "ctx-1", requestId: "req-1", httpStatus: 200, a2aState: "TASK_STATE_SUBMITTED");
            scope.Observe(httpStatus: null, rpcErrorCode: -32009);
        }

        var end = trace.Events[1];
        Assert.Equal("task-1", end.TaskId);
        Assert.Equal("ctx-1", end.ContextId);
        Assert.Equal("req-1", end.RequestId);
        Assert.Equal(200, end.HttpStatus);
        Assert.Equal(-32009, end.RpcErrorCode);
        Assert.Equal("TASK_STATE_SUBMITTED", end.A2AState);
        Assert.Null(trace.Events[0].TaskId);
    }

    [Fact]
    public void Measure_DisposedTwice_ClosesOnce()
    {
        var (diagnostics, trace, _, _) = Build();

        var scope = diagnostics.Measure(WorkIqTraceStage.Cancel, "rpc.cancel", "CancelTask");
        scope.Dispose();
        scope.Dispose();
        scope.Fail(new Exception("late"));

        Assert.Equal(2, trace.Count);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Dispose
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dispose_Always_DisposesDisposableSinksOnce()
    {
        var traceSink = new DisposableTraceSink();
        var issueSink = new DisposableIssueSink();
        var diagnostics = new WorkIqDiagnostics("r", null, [traceSink], [issueSink]);

        diagnostics.Dispose();
        diagnostics.Dispose();

        Assert.Equal(1, traceSink.Disposals);
        Assert.Equal(1, issueSink.Disposals);
    }

    private sealed class ThrowingTraceSink : IWorkIqTraceSink
    {
        public int Calls { get; private set; }

        public void Write(WorkIqTraceEvent e)
        {
            Calls++;
            throw new IOException("disk full while writing Bearer sink-secret");
        }
    }

    private sealed class ThrowingIssueSink : IWorkIqIssueSink
    {
        public void Write(WorkIqIssue i) => throw new IOException("disk full");
    }

    private sealed class DisposableTraceSink : IWorkIqTraceSink, IDisposable
    {
        public int Disposals { get; private set; }

        public void Write(WorkIqTraceEvent e)
        {
        }

        public void Dispose() => Disposals++;
    }

    private sealed class DisposableIssueSink : IWorkIqIssueSink, IDisposable
    {
        public int Disposals { get; private set; }

        public void Write(WorkIqIssue i)
        {
        }

        public void Dispose() => Disposals++;
    }
}
