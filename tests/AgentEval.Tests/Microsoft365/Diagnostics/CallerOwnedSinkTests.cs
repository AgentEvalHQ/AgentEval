// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;
using AgentEval.Tests.Microsoft365.HarnessBridge;
using AgentEval.Tests.Microsoft365.Readiness;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>
/// The public sink seam —<see cref="WorkIqOptions.TraceSinks"/> / <see cref="WorkIqOptions.IssueSinks"/> on a live
/// agent and <see cref="ReadinessCheckOptions.TraceSinks"/> / <see cref="ReadinessCheckOptions.IssueSinks"/> on the
/// readiness check. A caller's sink receives exactly what the JSONL files receive (same events and issues, same order,
/// after the same redaction), the caller owns its lifetime (never disposed by the run), and a failing sink is dropped
/// alone with its own type named.
/// </summary>
public sealed class CallerOwnedSinkTests
{
    private const string JwtLike = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlc2lnbmF0dXJl";

    // ---------------------------------------------------------------------------------------------------------
    // BuildLive
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BuildLive_ExtraSinks_ReceiveTheSameEventsAndIssuesAsTheJsonlFiles()
    {
        var trace = new OwnedTraceSink();
        var issues = new OwnedIssueSink();
        var rig = LiveAgentRig.Create(
            LiveAgentRig.Scripted(LiveAgentRig.FailedFixture),
            new WorkIqOptions { TraceSinks = [trace], IssueSinks = [issues] });
        await using (rig)
        {
            await Assert.ThrowsAsync<WorkIqTurnNotCompletedException>(() => rig.Agent.InvokeAsync("Summarize the status of Project Atlas."));

            var runId = rig.Agent.Diagnostics.RunId;
            var traceLines = ReadLines(Path.Combine(rig.DiagnosticsDirectory, $"m365-trace-{runId}.jsonl"));
            var issueLines = ReadLines(Path.Combine(rig.DiagnosticsDirectory, "m365-issues.jsonl"));

            Assert.NotEmpty(issueLines);   // the failed turn registered issues: the comparison below is not of two empty lists
            Assert.Equal(Codes(traceLines), trace.Inner.Events.Select(e => e.Code).ToArray());
            Assert.Equal(Codes(issueLines), issues.Inner.Issues.Select(i => i.Code).ToArray());
            Assert.Equal(WorkIqDiagnosticsFactory.ReadyEventCode, trace.Inner.Events[0].Code);
            Assert.Contains(trace.Inner.Events, e => e.Code == "turn.outcome");
            Assert.All(trace.Inner.Events, e => Assert.Equal(runId, e.RunId));
            Assert.All(issues.Inner.Issues, i => Assert.Equal(runId, i.RunId));
        }

        // The caller owns the sinks: the agent's disposal closed its own files, never them.
        Assert.False(trace.Disposed);
        Assert.False(issues.Disposed);
    }

    [Fact]
    public async Task BuildLive_ExtraSinks_ReceiveRedactedEventsAndIssues()
    {
        var trace = new OwnedTraceSink();
        var issues = new OwnedIssueSink();
        var rig = LiveAgentRig.Create(LiveAgentRig.Scripted(LiveAgentRig.CompletedFixture), new WorkIqOptions { TraceSinks = [trace], IssueSinks = [issues] });
        await using (rig)
        {
            rig.Agent.Diagnostics.Trace(WorkIqTraceStage.Auth, "test.secret", $"header Authorization: Bearer {JwtLike} and a bare {JwtLike}");
            rig.Agent.Diagnostics.Issue(WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "test.secret", $"Bearer {JwtLike}", $"token {JwtLike}"));

            var traced = Assert.Single(trace.Inner.Events, e => e.Code == "test.secret");
            Assert.DoesNotContain("eyJ", traced.Message, StringComparison.Ordinal);
            Assert.Contains("Bearer " + WorkIqRedaction.RedactedMarker, traced.Message, StringComparison.Ordinal);
            var issue = Assert.Single(issues.Inner.Issues);
            Assert.DoesNotContain("eyJ", issue.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("eyJ", issue.SuggestedCause, StringComparison.Ordinal);
            Assert.All(trace.Inner.Events, e => Assert.DoesNotContain(LiveAgentRig.FakeToken, e.Message, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WorkIqOptions_Validate_NullSinkEntry_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new WorkIqOptions { TraceSinks = [null!] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new WorkIqOptions { IssueSinks = [null!] }.Validate());
        new WorkIqOptions { TraceSinks = [], IssueSinks = [] }.Validate();
    }

    [Fact]
    public void Diagnostics_FailingCallerOwnedSink_IsDroppedAloneAndNamedAndNeverDisposed()
    {
        var failing = new ThrowingOwnedTraceSink();
        var survivor = new ListTraceSink();
        var fallback = new StringWriter();
        var diagnostics = new WorkIqDiagnostics("run-owned", null, [new CallerOwnedTraceSink(failing), survivor], [])
        {
            FallbackWriter = fallback,
        };

        diagnostics.Trace(WorkIqTraceStage.Config, "a.first", "first");
        diagnostics.Trace(WorkIqTraceStage.Config, "a.second", "second");
        diagnostics.Dispose();

        Assert.Equal(1, failing.Calls);   // dropped after its first failure
        Assert.Equal(2, survivor.Count);  // the other sink kept receiving
        Assert.Contains("ThrowingOwnedTraceSink (caller-owned)", fallback.ToString(), StringComparison.Ordinal);
        Assert.False(failing.Disposed);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Readiness check
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadinessCheck_ExtraSinks_ReceiveTheSameEventsAndIssuesAsTheFiles_ExpectedRepliesHeldBackFromBoth()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint().Handler.OnRpc("SendMessage", "envelopes/send-task-input-required-confirmation.json");
        var trace = new OwnedTraceSink();
        var issues = new OwnedIssueSink();

        var report = await rig.RunAsync(rig.Options(probe: true, consent: true) with { TraceSinks = [trace], IssueSinks = [issues] });

        Assert.Equal(ReadinessResult.Pass, report.Find(ReadinessStage.Protocol)!.Result);
        Assert.Equal(ReadinessResult.Fail, report.Find(ReadinessStage.Probe)!.Result);
        var issueLines = ReadinessTestRig.ReadLines(report.IssueFile);
        Assert.NotEmpty(issueLines);
        Assert.Equal(ReadinessTestRig.Codes(report.TraceFile), trace.Inner.Events.Select(e => e.Code).ToArray());
        Assert.Equal(ReadinessTestRig.Codes(report.IssueFile), issues.Inner.Issues.Select(i => i.Code).ToArray());
        // The paused probe turn registered its outcome; the stage traced its verdict instead of registering it again.
        Assert.Contains(issues.Inner.Issues, i => i.Code == "turn.input_required");
        Assert.DoesNotContain(issues.Inner.Issues, i => i.Code == "doctor.probe.failed");
        Assert.Contains(trace.Inner.Events, e => e.Code == "doctor.probe.failed");

        // The TaskNotFound the protocol stage provokes on purpose reaches neither the file nor the caller's sink.
        Assert.DoesNotContain(issues.Inner.Issues, i => i.RpcErrorCode == -32001);
        Assert.False(trace.Disposed);
        Assert.False(issues.Disposed);
    }

    [Fact]
    public void ReadinessCheckOptions_Validate_NullSinkEntry_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ReadinessCheckOptions { TraceSinks = [null!] }.Validate());
        Assert.Throws<ArgumentException>(() => new ReadinessCheckOptions { IssueSinks = [null!] }.Validate());
    }

    // ---------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] Codes(IEnumerable<string> lines) => lines.Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("code").GetString() ?? "";
    }).ToArray();

    /// <summary>A caller's trace sink that records whether anybody disposed it.</summary>
    private sealed class OwnedTraceSink : IWorkIqTraceSink, IDisposable
    {
        public ListTraceSink Inner { get; } = new();

        public bool Disposed { get; private set; }

        public void Write(WorkIqTraceEvent e) => Inner.Write(e);

        public void Dispose() => Disposed = true;
    }

    /// <summary>A caller's issue sink that records whether anybody disposed it.</summary>
    private sealed class OwnedIssueSink : IWorkIqIssueSink, IDisposable
    {
        public ListIssueSink Inner { get; } = new();

        public bool Disposed { get; private set; }

        public void Write(WorkIqIssue i) => Inner.Write(i);

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingOwnedTraceSink : IWorkIqTraceSink, IDisposable
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Disposed { get; private set; }

        public void Write(WorkIqTraceEvent e)
        {
            Interlocked.Increment(ref _calls);
            throw new IOException("the caller's sink is broken");
        }

        public void Dispose() => Disposed = true;
    }
}
