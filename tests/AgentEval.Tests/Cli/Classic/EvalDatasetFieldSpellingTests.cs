// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// <c>eval</c> on a dataset written in camelCase, through the real <see cref="EvalCommand.ExecuteAsync"/> with an
/// offline client that answers "Hi". The loaders used to drop <c>expectedOutput</c>, and a test case with no expected
/// output passes any non-empty answer: this dataset passed although "Hi" does not contain "Bonjour".
/// </summary>
[Collection("ConsoleTests")]
public sealed class EvalDatasetFieldSpellingTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agenteval-eval-spelling-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Theory]
    [InlineData(".yaml", "- id: greeting\n  input: Hello\n  expectedOutput: Bonjour\n")]
    [InlineData(".json", """[{"id": "greeting", "input": "Hello", "expectedOutput": "Bonjour"}]""")]
    public async Task Eval_CamelCaseExpectedOutput_IsChecked_SoAWrongAnswerFails(string extension, string content)
    {
        var dataset = new FileInfo(Path.Combine(_dir.FullName, "cases" + extension));
        await File.WriteAllTextAsync(dataset.FullName, content);
        var opts = new EvalOptions
        {
            Dataset = dataset,
            Endpoint = "http://localhost:11434/v1",
            Model = "fake-model",
            Format = "json",
            Output = new FileInfo(Path.Combine(_dir.FullName, $"out-{Guid.NewGuid():N}.json")),
            Quiet = true,
        };

        var originalErr = Console.Error;
        Console.SetError(TextWriter.Null);
        int exit;
        try
        {
            exit = await EvalCommand.ExecuteAsync(opts, default, agentClientOverride: new HiClient());
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Equal(ExitCodes.TestFailure, exit);
        using var export = JsonDocument.Parse(await File.ReadAllTextAsync(opts.Output.FullName));
        var result = Assert.Single(export.RootElement.GetProperty("results").EnumerateArray().ToList());
        Assert.False(result.GetProperty("passed").GetBoolean());
        Assert.Equal(0, result.GetProperty("score").GetDouble());
    }

    /// <summary>Answers "Hi" to every call.</summary>
    private sealed class HiClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "fake-model",
            });

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
