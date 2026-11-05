// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>The two records' invariants: codes are non-blank on both construction paths, data is detached, nothing is stamped by <c>Create</c>.</summary>
public sealed class WorkIqTraceEventTests
{
    [Fact]
    public void Create_Always_StampsNothing()
    {
        var e = WorkIqTraceEvent.Create(WorkIqTraceStage.Auth, "auth.silent_hit", "token from cache");

        Assert.Equal(default, e.At);
        Assert.Null(e.RunId);
        Assert.Equal(WorkIqTraceLevel.Info, e.Level);
        Assert.Equal(WorkIqTraceStage.Auth, e.Stage);
        Assert.Equal("auth.silent_hit", e.Code);
        Assert.Equal("token from cache", e.Message);
        Assert.Null(e.Data);
        Assert.Null(e.ElapsedMs);
    }

    [Fact]
    public void Create_WithLevel_KeepsIt()
    {
        var e = WorkIqTraceEvent.Create(WorkIqTraceStage.Send, "rpc.error", "boom", WorkIqTraceLevel.Error);

        Assert.Equal(WorkIqTraceLevel.Error, e.Level);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankCode_Throws(string code)
    {
        Assert.Throws<ArgumentException>(() => new WorkIqTraceEvent(default, WorkIqTraceStage.Auth, WorkIqTraceLevel.Info, code, "m"));
    }

    [Fact]
    public void With_BlankCode_ThrowsToo()
    {
        var e = WorkIqTraceEvent.Create(WorkIqTraceStage.Auth, "ok", "m");

        Assert.Throws<ArgumentException>(() => e with { Code = " " });
    }

    [Fact]
    public void Constructor_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkIqTraceEvent(default, WorkIqTraceStage.Auth, WorkIqTraceLevel.Info, "c", null!));
    }

    [Fact]
    public void Data_Always_IsDetachedFromItsDocument()
    {
        var document = JsonDocument.Parse("{\"a\":1}");
        var e = WorkIqTraceEvent.Create(WorkIqTraceStage.Config, "c", "m") with { Data = document.RootElement };
        document.Dispose();

        Assert.Equal(1, e.Data!.Value.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Data_UndefinedElement_Throws()
    {
        Assert.Throws<ArgumentException>(() => WorkIqTraceEvent.Create(WorkIqTraceStage.Config, "c", "m") with { Data = default(JsonElement) });
    }

    [Fact]
    public void IssueCreate_Always_StampsNothing()
    {
        var i = WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "http.401", "Unauthorized", "The token was rejected.");

        Assert.Equal(default, i.At);
        Assert.Null(i.RunId);
        Assert.Null(i.ConfigSha256);
        Assert.Null(i.AdapterVersion);
        Assert.Null(i.A2AVersion);
        Assert.Equal(WorkIqIssueCategory.Authentication, i.Category);
        Assert.Equal("The token was rejected.", i.SuggestedCause);
    }

    [Fact]
    public void Issue_BlankCodeOrNullText_Throws()
    {
        Assert.Throws<ArgumentException>(() => WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Unknown, " ", "m", "c"));
        Assert.Throws<ArgumentNullException>(() => WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Unknown, "c", null!, "c"));
        Assert.Throws<ArgumentNullException>(() => WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Unknown, "c", "m", null!));
        var ok = WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Unknown, "c", "m", "s");
        Assert.Throws<ArgumentException>(() => ok with { Code = "" });
    }
}
