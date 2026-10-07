// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.CopilotStudio;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF.CopilotStudio;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// <c>eval --temperature</c> and <c>eval --runs</c>, driven through the real <see cref="EvalCommand.ExecuteAsync"/>
/// with an offline chat client (the <c>agentClientOverride</c> seam for the <c>--endpoint</c> path, and the existing
/// <c>sutOverride</c> seam for <c>--sut</c>). Each test asserts on what reached the model client, what was printed,
/// or what was written to disk — never on a copy of the command's logic.
/// </summary>
[Collection("ConsoleTests")]
public class EvalCommandTemperatureAndRunsTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  --temperature: omitted = not sent, given (including 0) = sent
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Eval_TemperatureZero_ReachesTheModelClientAsZero()
    {
        // Old behaviour: `if (opts.Temperature != 0f)` skipped 0, so the client saw Temperature == null and the
        // provider's default applied — the opposite of what "--temperature 0" asks for.
        var (exit, client, _) = await RunClassicAsync(temperature: 0f);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.NotEmpty(client.OptionsSeen);
        Assert.All(client.OptionsSeen, o => Assert.Equal(0f, o?.Temperature));
    }

    [Fact]
    public async Task Eval_TemperatureOmitted_IsNotSentToTheModelClient()
    {
        var (exit, client, _) = await RunClassicAsync(temperature: null);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.NotEmpty(client.OptionsSeen);
        Assert.All(client.OptionsSeen, o => Assert.Null(o?.Temperature));
    }

    [Fact]
    public async Task Eval_TemperatureGiven_ReachesTheModelClient_AndTheSingleRunExportIsStillWritten()
    {
        var (exit, client, outputExisted) = await RunClassicAsync(temperature: 0.4f);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.All(client.OptionsSeen, o => Assert.Equal(0.4f, o?.Temperature));
        Assert.True(outputExisted, "a single run must still write its -o export");
    }

    [Fact]
    public async Task Eval_Sut_WithAgentOptions_WarnsThatTheyAreNotApplied()
    {
        // Old behaviour: --temperature/--max-tokens/--system-prompt were accepted with --sut and silently dropped —
        // the --sut branch never builds ChatOptions or reads the system prompt.
        var dataset = CreateTempDataset();
        var cfg = WriteValidCopilotStudioConfig();
        var output = TempPath(".json");
        try
        {
            var plain = SutOptions(dataset, cfg, output);
            var withAgentOptions = new EvalOptions
            {
                Dataset = plain.Dataset,
                Sut = plain.Sut,
                TargetOptions = plain.TargetOptions,
                Format = "json",
                Output = output,
                Temperature = 0.2f,
                MaxTokens = 100,
                SystemPrompt = "Be terse.",
            };

            var (exit, stderr) = await CaptureStdErrAsync(
                () => EvalCommand.ExecuteAsync(withAgentOptions, default, sutOverride: BenignSut()));

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains("--sut copilot-studio configures its own model", stderr);
            Assert.Contains("--temperature, --max-tokens, --system-prompt are not applied", stderr);
        }
        finally { TryDelete(dataset); TryDelete(cfg); TryDelete(output); }
    }

    [Fact]
    public async Task Eval_Sut_WithoutAgentOptions_DoesNotWarn()
    {
        var dataset = CreateTempDataset();
        var cfg = WriteValidCopilotStudioConfig();
        var output = TempPath(".json");
        try
        {
            var (exit, stderr) = await CaptureStdErrAsync(
                () => EvalCommand.ExecuteAsync(SutOptions(dataset, cfg, output), default, sutOverride: BenignSut()));

            Assert.Equal(ExitCodes.Success, exit);
            Assert.DoesNotContain("not applied", stderr);
        }
        finally { TryDelete(dataset); TryDelete(cfg); TryDelete(output); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  --runs validation: a usage error (exit 2), before any agent call
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Eval_RunsBelowOne_IsAUsageError_BeforeAnythingElseIsChecked(int runs)
    {
        // No --endpoint/--azure/--model: the old code fell through to the single-run path and threw
        // "Specify --endpoint <url> or --azure." (exit 3) — or, fully configured, silently ran once.
        var opts = new EvalOptions { Dataset = new FileInfo("/nonexistent/dataset.yaml"), Format = "json", Runs = runs };

        var (exit, stderr) = await CaptureStdErrAsync(() => EvalCommand.ExecuteAsync(opts, default));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains($"--runs must be at least 1 (got {runs})", stderr);
    }

    [Fact]
    public async Task Eval_RunsTwo_IsAUsageError_AndTheModelIsNeverCalled()
    {
        // Old behaviour: 2 > 1 entered stochastic mode, loaded the dataset, printed the header, then
        // StochasticOptions.Validate() threw ("Minimum 3 runs required…") and the command exited 3.
        var client = new RecordingChatClient();
        var dataset = CreateTempDataset();
        try
        {
            var runsTwo = Copy(ClassicOptions(dataset, output: null), runs: 2);

            var (exit, stderr) = await CaptureStdErrAsync(
                () => EvalCommand.ExecuteAsync(runsTwo, default, agentClientOverride: client));

            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Contains("--runs 2 is not accepted in stochastic mode", stderr);
            Assert.Contains("Minimum 3 runs", stderr);              // the runner's own reason, not a restatement
            Assert.DoesNotContain("(Parameter 'Runs')", stderr);    // exception plumbing is not shown to the user
            Assert.Equal(0, client.Calls);
        }
        finally { TryDelete(dataset); }
    }

    [Fact]
    public async Task Eval_StochasticThresholdOutOfRange_IsAUsageError()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("/nonexistent/dataset.yaml"),
            Format = "json",
            Runs = 3,
            SuccessThreshold = 1.5,
        };

        var (exit, stderr) = await CaptureStdErrAsync(() => EvalCommand.ExecuteAsync(opts, default));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--success-threshold 1.5 is not accepted in stochastic mode", stderr);
    }

    [Fact]
    public void StochasticRunnerMinimum_IsTheThreeRunsTheHelpTextAndDocsState()
    {
        // The --runs help text and docs/cli.md say stochastic mode starts at 3 runs. That number is the library's
        // (StochasticOptions.Validate), not the CLI's: if it moves, this fails and both texts need updating.
        Assert.Throws<ArgumentOutOfRangeException>(() => new StochasticOptions(Runs: 2).Validate());
        new StochasticOptions(Runs: 3).Validate();

        Assert.NotNull(EvalCommand.ValidateRuns(Copy(Minimal(), runs: 2)));
        Assert.Null(EvalCommand.ValidateRuns(Copy(Minimal(), runs: 3)));
        Assert.Null(EvalCommand.ValidateRuns(Copy(Minimal(), runs: 1)));

        var help = EvalCommand.Create().Options.Single(o => o.Name == "--runs").Description;
        Assert.Contains("from 3 runs", help);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  --runs > 1: the export has one entry per test case, with its runs
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Eval_RunsAboveOne_WritesTheExport_OneEntryPerTestCaseWithItsRuns()
    {
        // Through 0.43 the stochastic path wrote no export: "-o results.xml --format junit" produced no file, and a
        // stale file from an earlier run stayed in place, looking current.
        var client = new RecordingChatClient();
        var dataset = CreateTempDataset();
        var output = TempPath(".xml");
        var outputDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "agenteval-eval-runs-dir-" + Guid.NewGuid().ToString("N")));
        try
        {
            File.WriteAllText(output.FullName, "stale export from an earlier run");
            var opts = new EvalOptions
            {
                Dataset = dataset,
                Endpoint = "http://localhost:11434/v1",
                Model = "fake-model",
                Format = "junit",
                Output = output,
                OutputDir = outputDir,
                Runs = 3,
                Quiet = true,
            };

            var (exit, _) = await CaptureStdErrAsync(
                () => EvalCommand.ExecuteAsync(opts, default, agentClientOverride: client));

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(3, client.Calls);   // one test case × 3 runs: the stochastic path really ran

            var junit = File.ReadAllText(output.FullName);
            Assert.DoesNotContain("stale export", junit);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(junit, "<testcase "));   // one entry, not one per run
            Assert.Contains(StochasticReport.RunsMetric, junit);
            Assert.Contains("3 runs, ", junit);
            Assert.Contains("Run 3: ", junit);

            outputDir.Refresh();
            Assert.True(outputDir.Exists);
        }
        finally
        {
            TryDelete(dataset);
            TryDelete(output);
            try { if (Directory.Exists(outputDir.FullName)) Directory.Delete(outputDir.FullName, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task Eval_RunsAboveOne_PrintsItsTablesToStdErr_AndExportsJsonWithTheRuns()
    {
        // The per-test table is a human report and goes to stderr, never to Console.Out: stdout is the export's
        // channel, so `eval --runs 5 | jq .` must read only JSON.
        var client = new RecordingChatClient();
        var dataset = CreateTempDataset();
        var output = TempPath(".json");
        try
        {
            var notQuiet = new EvalOptions
            {
                Dataset = dataset,
                Endpoint = "http://localhost:11434/v1",
                Model = "fake-model",
                Format = "json",
                Output = output,
                Runs = 3,
            };

            var originalOut = Console.Out;
            using var stdout = new StringWriter();
            Console.SetOut(stdout);
            int exit;
            string stderr;
            try
            {
                (exit, stderr) = await CaptureStdErrAsync(
                    () => EvalCommand.ExecuteAsync(notQuiet, default, agentClientOverride: client));
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains("📊", stderr);   // the table title TableFormatter writes
            Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()),
                $"stochastic mode wrote to Console.Out:{Environment.NewLine}{stdout}");

            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(output.FullName));
            var result = Assert.Single(json.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal(3, result.GetProperty("metricScores").GetProperty(StochasticReport.RunsMetric).GetDouble());
            Assert.Equal("stochastic", json.RootElement.GetProperty("metadata").GetProperty("Mode").GetString());
        }
        finally
        {
            TryDelete(dataset);
            TryDelete(output);
        }
    }

    // ── helpers ──

    private static async Task<(int Exit, RecordingChatClient Client, bool OutputExisted)> RunClassicAsync(float? temperature)
    {
        var client = new RecordingChatClient();
        var dataset = CreateTempDataset();
        var output = TempPath(".json");
        try
        {
            var opts = new EvalOptions
            {
                Dataset = dataset,
                Endpoint = "http://localhost:11434/v1",
                Model = "fake-model",
                Format = "json",
                Output = output,
                Quiet = true,
                Temperature = temperature,
            };

            var exit = await EvalCommand.ExecuteAsync(opts, default, agentClientOverride: client);
            output.Refresh();
            return (exit, client, output.Exists);
        }
        finally { TryDelete(dataset); TryDelete(output); }
    }

    private static EvalOptions ClassicOptions(FileInfo dataset, FileInfo? output) => new()
    {
        Dataset = dataset,
        Endpoint = "http://localhost:11434/v1",
        Model = "fake-model",
        Format = "json",
        Output = output,
        Quiet = true,
    };

    private static EvalOptions Minimal() => new() { Dataset = new FileInfo("test.yaml"), Format = "json" };

    // EvalOptions is a class with init-only properties (no `with`), so tests copy the fields they use.
    private static EvalOptions Copy(EvalOptions o, int runs) => new()
    {
        Dataset = o.Dataset,
        Endpoint = o.Endpoint,
        Model = o.Model,
        Format = o.Format,
        Output = o.Output,
        OutputDir = o.OutputDir,
        Quiet = o.Quiet,
        Temperature = o.Temperature,
        SuccessThreshold = o.SuccessThreshold,
        Runs = runs,
    };

    private static EvalOptions SutOptions(FileInfo dataset, FileInfo config, FileInfo output) => new()
    {
        Dataset = dataset,
        Sut = "copilot-studio",
        TargetOptions = new Dictionary<string, ISutTargetOptions?>
        {
            ["copilot-studio"] = new CopilotStudioSutOptions { ConfigFile = config, AckLiveSideEffects = true, MaxCredits = 0 },
        },
        Format = "json",
        Output = output,
    };

    private static IEvaluableAgent BenignSut()
    {
        AIAgent inner = new ChatClientAgent(new RecordingChatClient(), new ChatClientAgentOptions { Name = "cs-fake" });
        return CopilotStudioAgentFactory.FromAgent(inner);
    }

    private static async Task<(int Exit, string StdErr)> CaptureStdErrAsync(Func<Task<int>> run)
    {
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            var exit = await run();
            return (exit, sw.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    private static FileInfo CreateTempDataset()
    {
        var path = TempPath(".yaml");
        File.WriteAllText(path.FullName, """
            - id: test1
              input: "Hello"
              expectedOutput: "Hi"
            """);
        return new FileInfo(path.FullName);
    }

    private static FileInfo WriteValidCopilotStudioConfig()
    {
        var path = TempPath(".json");
        File.WriteAllText(path.FullName,
            "{ \"environmentId\": \"env-1\", \"schemaName\": \"cr1a2_agent\", \"tenantId\": \"tenant-1\", \"appClientId\": \"app-1\" }");
        return new FileInfo(path.FullName);
    }

    private static FileInfo TempPath(string extension) =>
        new(Path.Combine(Path.GetTempPath(), "agenteval-eval-temp-runs-" + Guid.NewGuid().ToString("N") + extension));

    private static void TryDelete(FileInfo f)
    {
        try { if (File.Exists(f.FullName)) { File.Delete(f.FullName); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    /// <summary>Answers "Hi" to every call and records the <see cref="ChatOptions"/> each call carried.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly List<ChatOptions?> _optionsSeen = [];

        public IReadOnlyList<ChatOptions?> OptionsSeen { get { lock (_optionsSeen) { return [.. _optionsSeen]; } } }

        public int Calls { get { lock (_optionsSeen) { return _optionsSeen.Count; } } }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            lock (_optionsSeen) { _optionsSeen.Add(options); }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "fake-model",
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents)
                {
                    FinishReason = response.FinishReason,
                    ModelId = response.ModelId,
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
