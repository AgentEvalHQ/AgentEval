// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli;

[Collection("EnvVarTests")]
public sealed class BenchLongMemEvalCommandTests
{
    [Fact]
    public async Task RunAsync_MixedOutcomes_UsesScoredDenominatorAndCorrectPercentRendering()
    {
        using var fixture = WorkspaceFixture.Create(
            Question("q-yes"),
            Question("q-no"),
            Question("q-empty"));
        using var environment = new EnvironmentVariableScope(
            LongMemEvalDataLoader.DatasetPathEnvVar,
            fixture.DatasetPath);
        var client = new SequenceChatClient(
            "agent answer one", "yes",
            "agent answer two", "no",
            "agent answer three", string.Empty, string.Empty);

        var invocation = await CaptureConsoleAsync(() =>
            BenchLongMemEvalCommand.RunAsync(
                preset: "subset",
                subject: "cli-longmemeval",
                rootOverride: fixture.Root,
                chatClientOverride: client));

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains("Overall accuracy:        50.0%", invocation.Output);
        Assert.Contains("(1/2 scored, 3 selected)", invocation.Output);
        Assert.Contains("Inconclusive judgments:  1", invocation.Output);

        // Scoped to the accuracy line rather than the whole transcript. The guard is against a
        // mis-scaled percent -- 50.0% rendering as "5000" -- but the transcript also prints the
        // workspace path, which is a randomly named temp directory. A run that drew
        // "...96f2e438390616129bc35000e..." failed this assertion on a hex coincidence rather than
        // on a rendering bug, so the substring is now checked where the rendering actually happens.
        var accuracyLine = Assert.Single(
            invocation.Output
                .Split('\n')
                .Where(line => line.Contains("Overall accuracy:", StringComparison.Ordinal)));
        Assert.DoesNotContain("5000", accuracyLine);

        var runDirectory = Assert.Single(fixture.RunDirectories());
        var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(runDirectory, "summary.json")));
        Assert.Equal("PASS", summary.RootElement.GetProperty("verdict").GetString());
        var stats = summary.RootElement.GetProperty("stats");
        Assert.Equal(3, stats.GetProperty("total").GetInt32());
        Assert.Equal(1, stats.GetProperty("passed").GetInt32());
        Assert.Equal(1, stats.GetProperty("failed").GetInt32());
        Assert.Equal(1, stats.GetProperty("warnings").GetInt32());
        var metrics = summary.RootElement.GetProperty("metrics");
        Assert.Equal(256, metrics.GetProperty("judge_max_output_tokens").GetDouble());
        Assert.Equal(0, metrics.GetProperty("judge_temperature_configured").GetDouble());
        Assert.Equal(0, metrics.GetProperty("evidence_capture_mode").GetDouble());

        var native = JsonSerializer.Deserialize<ExternalBenchmarkResult>(
            await File.ReadAllTextAsync(Path.Combine(runDirectory, "report-native.json")));
        Assert.NotNull(native);
        Assert.Equal(50, native.OverallAccuracy);
        Assert.Equal(2, native.ScoredQuestions);
        Assert.Equal(3, native.SelectedQuestions);
    }

    [Fact]
    public async Task RunAsync_ZeroScoredQuestions_ReturnsInconclusiveAndNeverPasses()
    {
        using var fixture = WorkspaceFixture.Create(Question("q-empty"));
        using var environment = new EnvironmentVariableScope(
            LongMemEvalDataLoader.DatasetPathEnvVar,
            fixture.DatasetPath);
        var client = new SequenceChatClient(
            "agent answer", string.Empty, string.Empty);

        var invocation = await CaptureConsoleAsync(() =>
            BenchLongMemEvalCommand.RunAsync(
                preset: "subset",
                subject: "cli-longmemeval-empty",
                rootOverride: fixture.Root,
                chatClientOverride: client));

        Assert.True(
            invocation.ExitCode == ExitCodes.GateInconclusive,
            $"Expected inconclusive exit, got {invocation.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{invocation.Output}{Environment.NewLine}stderr:{Environment.NewLine}{invocation.Error}");
        Assert.Contains("Overall accuracy:        n/a", invocation.Output);
        Assert.Contains("Verdict:                 INCONCLUSIVE", invocation.Output);
        Assert.DoesNotContain("Verdict:                 PASS", invocation.Output);

        var runDirectory = Assert.Single(fixture.RunDirectories());
        var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(runDirectory, "summary.json")));
        Assert.Equal("WARN", summary.RootElement.GetProperty("verdict").GetString());
        Assert.False(summary.RootElement.GetProperty("metrics").TryGetProperty(
            "overall_accuracy",
            out _));
    }

    private static object Question(string id) => new
    {
        question_id = id,
        question_type = "single-session-user",
        question = "What should be recalled?",
        answer = "gold answer",
        question_date = "2026/07/29 (Wed) 00:00",
        haystack_sessions = new[]
        {
            new object[]
            {
                new { role = "user", content = "safe history", has_answer = true },
                new { role = "assistant", content = "safe reply", has_answer = false }
            }
        },
        haystack_dates = new[] { "2026/07/01" },
        haystack_session_ids = new[] { "session-1" },
        answer_session_ids = new[] { "session-1" }
    };

    private static async Task<ConsoleInvocation> CaptureConsoleAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await action();
            return new ConsoleInvocation(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record ConsoleInvocation(int ExitCode, string Output, string Error);

    private sealed class SequenceChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public ChatClientMetadata Metadata { get; } =
            new("longmemeval-cli-test", new Uri("http://localhost"), "test-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
                throw new InvalidOperationException("No response configured.");
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, _responses.Dequeue())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        public string Root { get; }
        public string DatasetPath { get; }

        private WorkspaceFixture(string root, string datasetPath)
        {
            Root = root;
            DatasetPath = datasetPath;
        }

        public static WorkspaceFixture Create(params object[] questions)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"agenteval-lme-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "test.sln"), string.Empty);
            InitCommand.RunAsync("longmemeval-cli-test", root).GetAwaiter().GetResult();
            var datasetPath = Path.Combine(root, "longmemeval-test.json");
            File.WriteAllText(datasetPath, JsonSerializer.Serialize(questions));
            return new WorkspaceFixture(root, datasetPath);
        }

        public IReadOnlyList<string> RunDirectories()
        {
            var subjects = Path.Combine(Root, ".agenteval", "subjects");
            return Directory.Exists(subjects)
                ? Directory.EnumerateFiles(subjects, "summary.json", SearchOption.AllDirectories)
                    .Select(path => Path.GetDirectoryName(path)!)
                    .ToArray()
                : [];
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test artifacts.
            }
        }
    }
}
