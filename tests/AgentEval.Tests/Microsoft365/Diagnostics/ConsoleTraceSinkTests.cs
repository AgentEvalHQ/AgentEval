// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>The one-line stderr format of the verbose view: every known fact in a fixed order, nothing invented for an unknown one.</summary>
public sealed class ConsoleTraceSinkTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 10, 11, 12, 345, TimeSpan.Zero);

    [Fact]
    public void Format_EveryFactKnown_PrintsAllSegmentsOnOneLine()
    {
        var e = new WorkIqTraceEvent(At, WorkIqTraceStage.Discovery, WorkIqTraceLevel.Info, "discovery.card_fetched", "Card fetched\r\nsecond line")
        {
            CaseId = "c1",
            TurnId = "t1",
            RpcId = "rpc-1",
            MessageId = "msg-1",
            TaskId = "task-1",
            ContextId = "ctx-1",
            RequestId = "req-1",
            HttpStatus = 200,
            RpcErrorCode = -32001,
            A2AState = "TASK_STATE_COMPLETED",
            ElapsedMs = 12.34,
            Data = JsonSerializer.SerializeToElement(new { n = 1 }),
        };

        var line = ConsoleTraceSink.Format(e);

        Assert.Equal(
            "[m365 10:11:12.345 Discovery/Info discovery.card_fetched] Card fetched second line | " +
            "ids=case:c1,turn:t1,rpc:rpc-1,msg:msg-1,task:task-1,ctx:ctx-1,req:req-1 http=200 rpc=-32001 state=TASK_STATE_COMPLETED ms=12.3 data={\"n\":1}",
            line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Format_NothingKnown_PrintsOnlyTheHeadAndMessage()
    {
        var e = new WorkIqTraceEvent(At, WorkIqTraceStage.Auth, WorkIqTraceLevel.Warning, "auth.device_code_prompted", "To sign in, open https://microsoft.com/devicelogin and enter the code ABCD1234");

        var line = ConsoleTraceSink.Format(e);

        Assert.Equal("[m365 10:11:12.345 Auth/Warning auth.device_code_prompted] To sign in, open https://microsoft.com/devicelogin and enter the code ABCD1234", line);
        Assert.DoesNotContain(" | ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OversizedData_IsCutWithTheMarker()
    {
        var e = new WorkIqTraceEvent(At, WorkIqTraceStage.Stream, WorkIqTraceLevel.Info, "frame", "m")
        {
            Data = JsonSerializer.SerializeToElement(new { big = new string('z', 10_000) }),
        };

        var line = ConsoleTraceSink.Format(e);

        Assert.Contains("data=", line, StringComparison.Ordinal);
        Assert.EndsWith(WorkIqDiagnosticsJson.TruncationMarker, line, StringComparison.Ordinal);
        Assert.True(line.Length < 3000);
    }

    [Fact]
    public void Write_Always_WritesOneTerminatedLineAndFlushes()
    {
        var writer = new StringWriter();
        var sink = new ConsoleTraceSink(writer);

        sink.Write(new WorkIqTraceEvent(At, WorkIqTraceStage.Send, WorkIqTraceLevel.Info, "rpc.sent", "SendMessage") { HttpStatus = 200 });
        sink.Write(new WorkIqTraceEvent(At, WorkIqTraceStage.Send, WorkIqTraceLevel.Info, "rpc.response", "reply"));

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("[m365 10:11:12.345 Send/Info rpc.sent] SendMessage | http=200", lines[0]);
        Assert.Equal("[m365 10:11:12.345 Send/Info rpc.response] reply", lines[1]);
    }
}
