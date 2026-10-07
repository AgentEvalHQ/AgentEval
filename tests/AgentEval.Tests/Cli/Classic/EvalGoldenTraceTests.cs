// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Snapshots;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// <c>eval --save-golden</c> / <c>--golden</c> / <c>--fail-on-tool-change</c>, driven through the real
/// <see cref="EvalCommand.ExecuteAsync"/> with an offline client that answers "Hi". A test case passes when the answer
/// contains its expected output, so the dataset decides whether the same answer passes or fails.
/// </summary>
[Collection("ConsoleTests")]
public sealed class EvalGoldenTraceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agenteval-golden-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private FileInfo Dataset(string expected)
    {
        var path = Path.Combine(_dir.FullName, $"cases-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, $"""
            - id: greeting
              input: "Hello"
              expected: "{expected}"
            """);
        return new FileInfo(path);
    }

    private FileInfo GoldenPath() => new(Path.Combine(_dir.FullName, "golden.json"));

    private static EvalOptions Options(FileInfo dataset, FileInfo? save = null, FileInfo? golden = null,
        bool failOnToolChange = false, int runs = 1) => new()
    {
        Dataset = dataset,
        Endpoint = "http://localhost:11434/v1",
        Model = "fake-model",
        Format = "json",
        Output = new FileInfo(Path.Combine(dataset.DirectoryName!, $"out-{Guid.NewGuid():N}.json")),
        Quiet = true,
        Runs = runs,
        SaveGolden = save,
        Golden = golden,
        FailOnToolChange = failOnToolChange,
    };

    private static async Task<(int Exit, string StdErr, HiClient Client)> RunAsync(EvalOptions opts)
    {
        var client = new HiClient();
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            var exit = await EvalCommand.ExecuteAsync(opts, default, agentClientOverride: client);
            return (exit, sw.ToString(), client);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task SaveGolden_WritesEachCaseWithItsVerdictAndOutput()
    {
        var golden = GoldenPath();

        var (exit, _, _) = await RunAsync(Options(Dataset("Hi"), save: golden));

        Assert.Equal(ExitCodes.Success, exit);
        var trace = await GoldenTrace.LoadAsync(golden.FullName);
        var c = Assert.Single(trace.Cases);
        Assert.True(c.Passed);
        Assert.Equal("Hi", c.Output);
        Assert.Equal("fake-model", trace.Model);
    }

    [Fact]
    public async Task AgainstTheGolden_APassThatNowFails_IsARegression_ExitOne()
    {
        var golden = GoldenPath();
        await RunAsync(Options(Dataset("Hi"), save: golden));

        var (exit, stderr, _) = await RunAsync(Options(Dataset("Bonjour"), golden: golden));

        Assert.Equal(ExitCodes.TestFailure, exit);
        Assert.Contains("REGRESSED", stderr, StringComparison.Ordinal);   // printed even with --quiet
        Assert.Contains("greeting", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgainstTheGolden_ATestThatWasAlreadyFailing_DoesNotFailTheRun()
    {
        // Without --golden this run exits 1 (a test fails). Against a golden trace in which it failed too, nothing
        // regressed, so the build passes.
        var golden = GoldenPath();
        await RunAsync(Options(Dataset("Bonjour"), save: golden));

        var (withoutGolden, _, _) = await RunAsync(Options(Dataset("Bonjour")));
        var (withGolden, stderr, _) = await RunAsync(Options(Dataset("Bonjour"), golden: golden));

        Assert.Equal(ExitCodes.TestFailure, withoutGolden);
        Assert.Equal(ExitCodes.Success, withGolden);
        Assert.DoesNotContain("REGRESSED", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailOnToolChange_FailsWhenTheGoldenRecordsOtherToolCalls()
    {
        // The golden trace says the test called a tool; this run calls none.
        var golden = GoldenPath();
        await new GoldenTrace(GoldenTrace.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, "fake-model",
            [new GoldenTraceCase("greeting", true, 100, "Hi", [new GoldenToolCall("lookup", null)])]).SaveAsync(golden.FullName);

        var (lenient, _, _) = await RunAsync(Options(Dataset("Hi"), golden: golden));
        var (strict, _, _) = await RunAsync(Options(Dataset("Hi"), golden: golden, failOnToolChange: true));

        Assert.Equal(ExitCodes.Success, lenient);
        Assert.Equal(ExitCodes.TestFailure, strict);
    }

    [Fact]
    public async Task GoldenAndSaveGolden_CompareFirst_ThenUpdateTheFile()
    {
        var golden = GoldenPath();
        await RunAsync(Options(Dataset("Bonjour"), save: golden));   // the test failed

        var (exit, _, _) = await RunAsync(Options(Dataset("Hi"), save: golden, golden: golden));

        Assert.Equal(ExitCodes.Success, exit);   // improved, not regressed
        Assert.True(Assert.Single((await GoldenTrace.LoadAsync(golden.FullName)).Cases).Passed);
    }

    [Fact]
    public async Task UsageErrors_AreRaisedBeforeAnyAgentCall()
    {
        var dataset = Dataset("Hi");
        var missing = new FileInfo(Path.Combine(_dir.FullName, "no-such-golden.json"));
        var notATrace = new FileInfo(Path.Combine(_dir.FullName, "not-a-trace.json"));
        await File.WriteAllTextAsync(notATrace.FullName, "{}");

        foreach (var (opts, message) in new[]
                 {
                     (Options(dataset, failOnToolChange: true), "--fail-on-tool-change needs --golden"),
                     (Options(dataset, golden: missing), "golden trace not found"),
                     (Options(dataset, golden: notATrace), "is not a golden trace"),
                     (Options(dataset, save: GoldenPath(), runs: 3), "cannot be combined with --runs above 1"),
                 })
        {
            var (exit, stderr, client) = await RunAsync(opts);

            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Contains(message, stderr, StringComparison.Ordinal);
            Assert.Equal(0, client.Calls);
        }
    }

    /// <summary>Answers "Hi" to every call.</summary>
    private sealed class HiClient : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "fake-model",
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
                yield return new ChatResponseUpdate(message.Role, message.Contents) { FinishReason = response.FinishReason };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
