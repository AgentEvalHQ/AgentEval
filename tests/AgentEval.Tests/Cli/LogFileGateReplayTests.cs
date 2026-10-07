// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>agenteval log-file gate-replay</c>, over a capture written by the real <see cref="FixtureCapturingChatClient"/>
/// (not hand-written JSONL), so what the command reads is what <c>--capture-fixture</c> leaves on disk.
/// </summary>
[Collection("ConsoleTests")]
public sealed class LogFileGateReplayTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agenteval-gate-replay-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    /// <summary>Captures one round-trip per response, through the shipped capture client.</summary>
    private async Task<FileInfo> CaptureAsync(params ChatResponse[] responses)
    {
        var path = Path.Combine(_dir.FullName, "capture.jsonl");
        await using (var sink = new StreamWriter(path))
        {
            var queue = new Queue<ChatResponse>(responses);
            using var client = new FixtureCapturingChatClient(new ScriptedClient(queue), sink, label: "sut");
            foreach (var _ in responses)
                await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Tidy up and tell the customer.")]);
        }

        return new FileInfo(path);
    }

    private static ChatResponse Calls(params (string Name, Dictionary<string, object?> Args)[] calls) =>
        new(new ChatMessage(ChatRole.Assistant,
            calls.Select((c, i) => (AIContent)new FunctionCallContent($"call-{i}", c.Name, c.Args)).ToList()))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private FileInfo Gates(string name, string json)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, json);
        return new FileInfo(path);
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(FileInfo capture, FileInfo baseline, FileInfo candidate, bool json = false)
    {
        var (originalOut, originalErr) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await LogFileGateReplay.RunAsync(capture, baseline, candidate, json, default);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task ACandidateThatForbidsATool_BlocksOnlyThatCall()
    {
        var capture = await CaptureAsync(
            Calls(("delete_temp_files", new() { ["path"] = "/tmp/cache" })),
            Calls(("send_email", new() { ["to"] = "customer@example.com" })));
        var today = Gates("today.json", "[]");
        var proposed = Gates("proposed.json", """[{"gate": "tool:forbidden-tool", "forbidden": ["send_email"]}]""");

        var (exit, report, _) = await RunAsync(capture, today, proposed);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("2 call(s), 1 diverged", report, StringComparison.Ordinal);
        Assert.Contains("send_email: baseline=Allow  candidate=Block(", report, StringComparison.Ordinal);
        Assert.Contains("blocks 1 call(s) the baseline let through, and lets through 0", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_HasOneRowPerCall_WithBothVerdicts()
    {
        var capture = await CaptureAsync(Calls(
            ("fetch", new() { ["url"] = "https://evil.example.net/x" }),
            ("fetch", new() { ["url"] = "https://docs.example.com/y" })));
        var allowList = Gates("allow.json", """[{"gate": "tool:domain-allowlist", "allowedDomains": ["docs.example.com"]}]""");
        var none = Gates("none.json", "[]");

        var (exit, json, _) = await RunAsync(capture, allowList, none, json: true);

        Assert.Equal(ExitCodes.Success, exit);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("calls").GetInt32());
        Assert.Equal(1, root.GetProperty("loosened").GetInt32());   // dropping the allow-list lets the evil host through
        Assert.Equal(0, root.GetProperty("tightened").GetInt32());
        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Block", rows[0].GetProperty("baseline").GetProperty("action").GetString());
        Assert.Equal("Allow", rows[0].GetProperty("candidate").GetProperty("action").GetString());
        Assert.Equal("sut", rows[0].GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("""[{"gate": "tool:taint-tracking", "sourceTools": ["a"]}]""", "reads the conversation")]
    [InlineData("""[{"gate": "rendered-exfil"}]""", "is a chat gate")]
    [InlineData("""[{"gate": "tool:no-such-gate"}]""", "unknown gate")]
    [InlineData("""[{"gate": "tool:forbidden-tool", "forbiden": ["x"]}]""", "unknown parameter \"forbiden\"")]
    [InlineData("""[{"gate": "tool:forbidden-tool"}]""", "needs \"forbidden\"")]
    [InlineData("""[{"gate": "tool:argument-pattern", "pattern": "(unclosed"}]""", "rejected its parameters")]
    [InlineData("""{"gate": "tool:forbidden-tool"}""", "must be a JSON array")]
    public async Task AConfigurationItCannotReplay_IsAUsageError_BeforeAnyReplay(string config, string message)
    {
        var capture = await CaptureAsync(Calls(("send_email", new() { ["to"] = "a@b.c" })));

        var (exit, report, err) = await RunAsync(capture, Gates("ok.json", "[]"), Gates("bad.json", config));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains(message, err, StringComparison.Ordinal);
        Assert.Equal("", report);
    }

    [Fact]
    public async Task ACaptureWithNoToolCall_SaysThereIsNothingToReplay()
    {
        var capture = await CaptureAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        var (exit, _, err) = await RunAsync(capture, Gates("a.json", "[]"), Gates("b.json", "[]"));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("holds no tool call", err, StringComparison.Ordinal);
    }

    /// <summary>Returns the queued responses in order.</summary>
    private sealed class ScriptedClient(Queue<ChatResponse> responses) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(responses.Dequeue());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
