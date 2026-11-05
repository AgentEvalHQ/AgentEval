// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>
/// The JSONL sinks: parseable camelCase lines with enums as strings and nulls omitted, directories created,
/// append across instances, readable while open, and the 64 KiB line bound that keeps every line parseable.
/// </summary>
public sealed class JsonlSinkTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 10, 11, 12, 345, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-tests", "jsonl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private FileInfo FileIn(params string[] segments) => new(Path.Combine([_root, .. segments]));

    private static WorkIqTraceEvent Event(string code = "auth.silent_hit", string message = "token from cache") =>
        new(At, WorkIqTraceStage.Auth, WorkIqTraceLevel.Info, code, message) { RunId = "run-1" };

    [Fact]
    public void TraceSink_Write_ProducesParseableCamelCaseLinesWithoutNulls()
    {
        var file = FileIn("nested", "deeper", "trace.jsonl");

        using (var sink = new JsonlTraceSink(file))
        {
            sink.Write(Event());
            sink.Write(Event("rpc.error", "boom") with
            {
                Level = WorkIqTraceLevel.Error,
                Stage = WorkIqTraceStage.Send,
                HttpStatus = 401,
                RpcErrorCode = -32009,
                A2AState = "TASK_STATE_FAILED",
                ElapsedMs = 12.5,
                TaskId = "task-1",
                Data = JsonSerializer.SerializeToElement(new { count = 2 }),
            });
        }

        Assert.True(file.Directory!.Exists);
        var lines = File.ReadAllLines(file.FullName);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("2026-11-05T10:11:12.345+00:00", first.RootElement.GetProperty("at").GetString());
        Assert.Equal("Auth", first.RootElement.GetProperty("stage").GetString());
        Assert.Equal("Info", first.RootElement.GetProperty("level").GetString());
        Assert.Equal("auth.silent_hit", first.RootElement.GetProperty("code").GetString());
        Assert.Equal("token from cache", first.RootElement.GetProperty("message").GetString());
        Assert.Equal("run-1", first.RootElement.GetProperty("runId").GetString());
        Assert.False(first.RootElement.TryGetProperty("taskId", out _), "an unknown fact must be absent, not null");
        Assert.False(first.RootElement.TryGetProperty("httpStatus", out _));
        Assert.False(first.RootElement.TryGetProperty("data", out _));

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("Error", second.RootElement.GetProperty("level").GetString());
        Assert.Equal("Send", second.RootElement.GetProperty("stage").GetString());
        Assert.Equal(401, second.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Equal(-32009, second.RootElement.GetProperty("rpcErrorCode").GetInt32());
        // One spelling for the A2A fields across the trace, the issue register and readiness.json.
        Assert.Equal("TASK_STATE_FAILED", second.RootElement.GetProperty("a2aState").GetString());
        Assert.False(second.RootElement.TryGetProperty("a2AState", out _));
        Assert.Equal(12.5, second.RootElement.GetProperty("elapsedMs").GetDouble());
        Assert.Equal("task-1", second.RootElement.GetProperty("taskId").GetString());
        Assert.Equal(2, second.RootElement.GetProperty("data").GetProperty("count").GetInt32());
    }

    [Fact]
    public void TraceSink_TwoInstances_AppendToTheSameFile()
    {
        var file = FileIn("trace.jsonl");

        using (var first = new JsonlTraceSink(file))
        {
            first.Write(Event("one", "1"));
        }

        using (var second = new JsonlTraceSink(file))
        {
            second.Write(Event("two", "2"));
        }

        var lines = File.ReadAllLines(file.FullName);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"code\":\"one\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"code\":\"two\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TraceSink_WhileOpen_IsReadableByAnotherReader()
    {
        var file = FileIn("trace.jsonl");
        using var sink = new JsonlTraceSink(file);

        sink.Write(Event());

        // A tester tails the file during the run: the sink flushes per write and shares the file for reading.
        using var reader = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);
        var content = text.ReadToEnd();
        Assert.Contains("\"code\":\"auth.silent_hit\"", content, StringComparison.Ordinal);
        Assert.EndsWith("\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceSink_OversizedMessage_IsCutWithAMarkerAndStillParses()
    {
        var file = FileIn("trace.jsonl");
        var message = new string('x', 100_000);

        using (var sink = new JsonlTraceSink(file))
        {
            sink.Write(Event("big", message));
        }

        var line = Assert.Single(File.ReadAllLines(file.FullName));
        Assert.True(line.Length <= WorkIqDiagnosticsJson.MaxLineLength, $"line is {line.Length} chars");
        using var document = JsonDocument.Parse(line);
        var stored = document.RootElement.GetProperty("message").GetString()!;
        Assert.EndsWith(WorkIqDiagnosticsJson.TruncationMarker, stored, StringComparison.Ordinal);
        Assert.StartsWith("xxxx", stored, StringComparison.Ordinal);
        Assert.Equal("big", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void TraceSink_OversizedData_IsReplacedByTheMarkerBeforeTheMessageIsTouched()
    {
        var file = FileIn("trace.jsonl");
        var data = JsonSerializer.SerializeToElement(new { payload = new string('y', 100_000) });

        using (var sink = new JsonlTraceSink(file))
        {
            sink.Write(Event("big-data", "short message") with { Data = data });
        }

        var line = Assert.Single(File.ReadAllLines(file.FullName));
        Assert.True(line.Length <= WorkIqDiagnosticsJson.MaxLineLength);
        using var document = JsonDocument.Parse(line);
        Assert.Equal("short message", document.RootElement.GetProperty("message").GetString());
        Assert.Equal(WorkIqDiagnosticsJson.TruncationMarker, document.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public void TraceSink_AfterDispose_Throws()
    {
        var sink = new JsonlTraceSink(FileIn("trace.jsonl"));
        sink.Dispose();
        sink.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sink.Write(Event()));
    }

    [Fact]
    public void TraceSink_PathUnderAFile_ThrowsNamingThePath()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var file = new FileInfo(Path.Combine(blocker, "trace.jsonl"));

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonlTraceSink(file));

        Assert.Contains(file.FullName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueSink_Write_ProducesParseableLinesWithCategoryAsString()
    {
        var file = FileIn("issues", "m365-issues.jsonl");

        using (var sink = new JsonlIssueSink(file))
        {
            sink.Write(new WorkIqIssue(At, WorkIqTraceStage.Discovery, WorkIqIssueCategory.Permission, "http.403", "Forbidden", "Admin consent for WorkIQAgent.Ask is missing.")
            {
                RunId = "run-1",
                HttpStatus = 403,
                RequestId = "req-1",
                ConfigSha256 = "abc",
                AdapterVersion = "1.0.0-rc.1",
                A2AVersion = "1.0",
            });
        }

        var line = Assert.Single(File.ReadAllLines(file.FullName));
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("Discovery", root.GetProperty("stage").GetString());
        Assert.Equal("Permission", root.GetProperty("category").GetString());
        Assert.Equal("http.403", root.GetProperty("code").GetString());
        Assert.Equal("Admin consent for WorkIQAgent.Ask is missing.", root.GetProperty("suggestedCause").GetString());
        Assert.Equal(403, root.GetProperty("httpStatus").GetInt32());
        Assert.Equal("abc", root.GetProperty("configSha256").GetString());
        Assert.Equal("1.0", root.GetProperty("a2aVersion").GetString());
        Assert.False(root.TryGetProperty("a2AVersion", out _));
        Assert.False(root.TryGetProperty("aadstsCode", out _));
        Assert.False(root.TryGetProperty("taskId", out _));
    }

    [Fact]
    public void IssueSink_OversizedMessage_IsCutAndStillParses()
    {
        var file = FileIn("m365-issues.jsonl");

        using (var sink = new JsonlIssueSink(file))
        {
            sink.Write(new WorkIqIssue(At, WorkIqTraceStage.Send, WorkIqIssueCategory.Service, "x", new string('m', 100_000), "cause"));
        }

        var line = Assert.Single(File.ReadAllLines(file.FullName));
        Assert.True(line.Length <= WorkIqDiagnosticsJson.MaxLineLength);
        using var document = JsonDocument.Parse(line);
        Assert.EndsWith(WorkIqDiagnosticsJson.TruncationMarker, document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("cause", document.RootElement.GetProperty("suggestedCause").GetString());
    }
}
