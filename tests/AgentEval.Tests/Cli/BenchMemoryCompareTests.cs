// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Two <c>bench memory</c> runs can be compared. A memory run used to write no <c>scenarios/</c>, so
/// <c>agenteval compare</c> read its manifest and summary as scenario files and crashed with "Value cannot be null"
/// (exit 2 for the wrong reason). Offline: a stub chat client stands in for both the agent and the judge.
/// </summary>
[Collection("ConsoleTests")]
public sealed class BenchMemoryCompareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-memcompare-" + Guid.NewGuid().ToString("N"));

    public BenchMemoryCompareTests()
    {
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "solution.json"), JsonSerializer.Serialize(
            new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "MemoryCompareTest" },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Stands in for both the agent and the judge; its reply carries a score, so every question is measured.</summary>
    private sealed class FixedReplyChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "{\"found_facts\":[],\"missing_facts\":[],\"forbidden_found\":[],\"score\":80,\"explanation\":\"I remember that.\"}")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private string[] RunDirectories() =>
        Directory.GetDirectories(_root, "scenarios", SearchOption.AllDirectories)
            .Select(d => Path.GetDirectoryName(d)!)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task TwoMemoryRuns_WriteScenarioResults_AndCompareReadsThem()
    {
        var first = await BenchMemoryCommand.RunAsync("quick", "mem-compare", _root, new FixedReplyChatClient());
        var second = await BenchMemoryCommand.RunAsync("quick", "mem-compare", _root, new FixedReplyChatClient());
        Assert.NotEqual(ExitCodes.UsageError, first);
        Assert.NotEqual(ExitCodes.UsageError, second);

        var runs = RunDirectories();
        Assert.Equal(2, runs.Length);
        foreach (var run in runs)
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(run, "scenarios"), "*.json"));

        var exit = CompareCommand.Run(runs[0], runs[1]);

        Assert.NotEqual(ExitCodes.UsageError, exit);
        Assert.NotEqual(ExitCodes.RuntimeError, exit);
    }

    private sealed class NoScoreChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I remember that. The answer is 42.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task ARunWhoseJudgeNeverScores_IsIncomplete_NotAFail()
    {
        // Through 0.42 every question here scored 50 (the default for a reply with no score) and the run read WARN.
        var previous = Console.Out;
        using var stdout = new StringWriter();
        Console.SetOut(stdout);
        int exit;
        try
        {
            exit = await BenchMemoryCommand.RunAsync("quick", "mem-incomplete", _root, new NoScoreChatClient());
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(ExitCodes.GateIndeterminate, exit);
        Assert.Contains("INCOMPLETE", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Grade:", stdout.ToString(), StringComparison.Ordinal);
        var summary = Directory.GetFiles(_root, "summary.json", SearchOption.AllDirectories).Single();
        Assert.Contains("\"WARN\"", File.ReadAllText(summary));
    }

    [Fact]
    public void Compare_OnARunWithNoScenarios_RefusesByName_InsteadOfCrashing()
    {
        var run = Path.Combine(_root, "run-without-scenarios");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "manifest.json"), "{\"schemaVersion\":\"1.0\",\"run\":{\"runId\":\"r1\"}}");
        File.WriteAllText(Path.Combine(run, "summary.json"), "{\"schemaVersion\":\"1.0\",\"runId\":\"r1\",\"verdict\":\"PASS\"}");

        var previous = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        int exit;
        try
        {
            exit = CompareCommand.Run(run, run);
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("is not a scenario result", stderr.ToString(), StringComparison.Ordinal);
    }
}
