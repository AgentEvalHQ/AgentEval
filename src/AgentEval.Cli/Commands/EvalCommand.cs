// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha during the v1.1 CLI consolidation.
// The public command surface (option names + behaviour + exit codes) is preserved verbatim
// because the agenteval CLI documentation references these flag names. The class lives in
// the same namespace as the new bench/doctor/mc commands so all "agenteval *" subcommands
// share one assembly with InternalsVisibleTo to AgentEval.Tests.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Infrastructure;
using AgentEval.Cli.Output;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.Exporters;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Output;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'agenteval eval' command — evaluate an AI agent against a dataset.
/// </summary>
internal static class EvalCommand
{
    public static Command Create()
    {
        var command = new Command("eval", "Evaluate an AI agent against a dataset");

        // Required
        var datasetOpt = new Option<FileInfo>("--dataset")
            { Required = true, Description = "Dataset file (YAML, JSON, JSONL, CSV)" };

        // Endpoint (mutually exclusive group)
        var endpointOpt = new Option<string?>("--endpoint") { Description = "OpenAI-compatible API endpoint URL" };
        var azureFlag = new Option<bool>("--azure") { Description = "Use Azure OpenAI (requires --endpoint and --deployment-name)" };

        // Built-in SUT targets (Track 2 PR2): --sut copilot-studio evaluates a live Copilot Studio agent
        // with your --dataset's prompts + judge criteria, via the same shared seam `redteam` already uses.
        // Each target owns its own options/validation/construction (ISutTarget).
        var (sutOpt, sutTargets) = SutTargetResolver.AddOptionsTo(command, "eval");

        // Model / Deployment
        var modelOpt = new Option<string?>("--model")
            { Description = "Model name (required for OpenAI-compatible endpoints)" };
        var deploymentNameOpt = new Option<string?>("--deployment-name")
            { Description = "Azure OpenAI deployment name (required when using --azure)" };

        // Authentication
        var apiKeyOpt = new Option<string?>("--api-key")
            { Description = "API key (or set OPENAI_API_KEY / AZURE_OPENAI_API_KEY env var)" };

        // Agent configuration
        var systemPromptOpt = new Option<string?>("--system-prompt") { Description = "System prompt text" };
        var systemPromptFileOpt = new Option<FileInfo?>("--system-prompt-file")
            { Description = "Read system prompt from file" };
        var temperatureOpt = new Option<float>("--temperature")
            { DefaultValueFactory = _ => 0f, Description = "Sampling temperature (0 = deterministic)" };
        var maxTokensOpt = new Option<int?>("--max-tokens") { Description = "Maximum output tokens" };

        // Metric selection
        var metricsOpt = new Option<string?>("--metrics")
            { Description = "Comma-separated metric names to run (e.g., llm_relevance,code_tool_success). Default: all" };

        // Judge (LLM-as-judge for scoring)
        var judgeEndpointOpt = new Option<string?>("--judge")
            { Description = "Separate endpoint for LLM-as-judge evaluation" };
        var judgeModelOpt = new Option<string?>("--judge-model")
            { Description = "Model for judge (default: same as --model)" };

        // Stochastic evaluation
        var runsOpt = new Option<int>("--runs")
            { DefaultValueFactory = _ => 1, Description = "Number of evaluation runs for stochastic analysis (default: 1)" };
        var thresholdOpt = new Option<double>("--success-threshold")
            { DefaultValueFactory = _ => 0.8, Description = "Success rate threshold for stochastic evaluation (default: 0.8)" };

        // Output
        var formatOpt = new Option<string>("--format")
            { DefaultValueFactory = _ => "json", Description = "Export format: json | junit | xml | markdown | md | trx | csv | directory | dir" };
        var outputOpt = new Option<FileInfo?>("-o", "--output") { Description = "Output file (default: stdout)" };
        var outputDirOpt = new Option<DirectoryInfo?>("--output-dir")
            { Description = "Write structured results to a directory (ADR-002 format: results.jsonl + summary.json + run.json)" };

        // Verbosity
        var verboseFlag = new Option<bool>("--verbose") { Description = "Show detailed progress" };
        var quietFlag = new Option<bool>("--quiet") { Description = "Suppress all output except the export" };

        command.Options.Add(datasetOpt);
        command.Options.Add(endpointOpt);
        command.Options.Add(azureFlag);
        command.Options.Add(modelOpt);
        command.Options.Add(deploymentNameOpt);
        command.Options.Add(apiKeyOpt);
        command.Options.Add(systemPromptOpt);
        command.Options.Add(systemPromptFileOpt);
        command.Options.Add(temperatureOpt);
        command.Options.Add(maxTokensOpt);
        command.Options.Add(metricsOpt);
        command.Options.Add(runsOpt);
        command.Options.Add(thresholdOpt);
        command.Options.Add(judgeEndpointOpt);
        command.Options.Add(judgeModelOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(outputDirOpt);
        command.Options.Add(verboseFlag);
        command.Options.Add(quietFlag);

        command.SetAction(async (parseResult, ct) =>
        {
            var opts = new EvalOptions
            {
                Dataset = parseResult.GetValue(datasetOpt)!,
                Endpoint = parseResult.GetValue(endpointOpt),
                Azure = parseResult.GetValue(azureFlag),
                Model = parseResult.GetValue(modelOpt),
                DeploymentName = parseResult.GetValue(deploymentNameOpt),
                ApiKey = parseResult.GetValue(apiKeyOpt),
                Sut = parseResult.GetValue(sutOpt),
                // Bind every built-in target's own flags polymorphically (keyed by --sut value) — mirrors
                // RedTeamCommand's convention so a target's flags never need a per-verb special case.
                TargetOptions = sutTargets.ToDictionary(
                    t => t.Sut, t => t.BindOptions(parseResult), StringComparer.OrdinalIgnoreCase),
                SystemPrompt = parseResult.GetValue(systemPromptOpt),
                SystemPromptFile = parseResult.GetValue(systemPromptFileOpt),
                Temperature = parseResult.GetValue(temperatureOpt),
                MaxTokens = parseResult.GetValue(maxTokensOpt),
                Metrics = parseResult.GetValue(metricsOpt),
                Runs = parseResult.GetValue(runsOpt),
                SuccessThreshold = parseResult.GetValue(thresholdOpt),
                JudgeEndpoint = parseResult.GetValue(judgeEndpointOpt),
                JudgeModel = parseResult.GetValue(judgeModelOpt),
                Format = parseResult.GetValue(formatOpt)!,
                Output = parseResult.GetValue(outputOpt),
                OutputDir = parseResult.GetValue(outputDirOpt),
                Verbose = parseResult.GetValue(verboseFlag),
                Quiet = parseResult.GetValue(quietFlag),
            };

            try
            {
                return await ExecuteAsync(opts, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        return command;
    }

    /// <summary>
    /// Core execution logic — separated from command wiring for testability. <paramref name="sutOverride"/>,
    /// when non-null, is forwarded into a built-in <c>--sut</c> target's construction (the credential-free
    /// test seam — mirrors <see cref="RedTeamCommand.ExecuteAsync"/>); it has no effect when <c>--sut</c>
    /// is not set.
    /// Returns exit code: 0 = all passed, 1 = test failure, 3 = runtime error.
    /// </summary>
    internal static async Task<int> ExecuteAsync(EvalOptions opts, CancellationToken ct, IEvaluableAgent? sutOverride = null)
    {
        // --sut path ONLY: dataset-existence must be checked before ISutTarget.Validate (called inside
        // TryResolve below, step 1) — a bad --sut config (e.g. missing consent) shouldn't mask a typo'd
        // --dataset path, and vice versa (see EvalCommandCopilotStudioSutTests.Eval_MissingDataset_
        // ThrowsBeforeSutValidation). The classic (--endpoint/--azure) path restores its ORIGINAL
        // precedence instead — connection-config validation before dataset-existence — via the second
        // dataset check further down, inside the `else` branch (review: this review-flagged regression
        // came from a top-of-method dataset check that applied to BOTH paths; only the --sut path's own
        // test actually needed dataset-first).
        if (opts.Sut is not null && !opts.Dataset.Exists)
            throw new FileNotFoundException($"Dataset not found: {opts.Dataset.FullName}");

        // 0. Parse + validate --metrics NAMES (Item 4, D1 bridge) as early as possible, before any network
        // call — an unknown metric name fails fast here, the same way a bad --dataset path already does.
        // Resolving to actual IMetric INSTANCES happens later (needs an evaluator client, built below).
        IReadOnlyList<string>? selectedMetrics = null;
        if (opts.Metrics is not null)
        {
            selectedMetrics = opts.Metrics
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (selectedMetrics.Count == 0)
                throw new ArgumentException("--metrics was specified but no metric names were provided.");

            var unknown = selectedMetrics.Where(m => !MetricCatalog.IsKnown(m)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException(
                    $"Unknown metric name(s): {string.Join(", ", unknown)}. Available: {string.Join(", ", MetricCatalog.AvailableNames)}.");
        }

        // 1. Resolve --sut (Track 2 PR2), if set — the same shared seam `redteam`/`bench` use. Built-in
        // targets own their own validation (consent gates, required config, etc.) via ISutTarget.Validate,
        // invoked inside TryResolve.
        var sutTargets = SutTargetResolver.BuiltInTargets().Where(t => t.SupportedVerbs.Contains("eval")).ToList();
        var commonTargetOptions = new CommonTargetOptions
        {
            Endpoint = opts.Endpoint,
            Azure = opts.Azure,
            Model = opts.Model,
            DeploymentName = opts.DeploymentName,
            ApiKey = opts.ApiKey,
            Sut = opts.Sut,
            TargetOptions = opts.TargetOptions,
        };
        var (sutAgent, sutResolvedName) = SutTargetResolver.TryResolve(commonTargetOptions, sutTargets, sutOverride);

        string resolvedName;
        IEvaluableAgent agent;
        IChatClient? chatClient = null;   // hoisted: --metrics' LLM-based fallback evaluator needs this outside the else block

        if (sutAgent is not null)
        {
            // A built-in target (e.g. copilot-studio) constructed itself; the --endpoint/--azure/--model
            // path below is entirely skipped, matching RedTeamCommand's existing --sut branch. No raw
            // IChatClient is exposed for a --sut target, so chatClient stays null here — an LLM-based
            // --metrics selection then needs an explicit --judge (see MetricCatalog.Resolve's own error).
            agent = sutAgent;
            resolvedName = sutResolvedName!;
        }
        else
        {
            // 1b. Validate the --endpoint/--azure path — unchanged from before --sut existed.
            if (opts.Endpoint is null && !opts.Azure)
                throw new InvalidOperationException("Specify --endpoint <url> or --azure.");
            if (opts.Azure && opts.Endpoint is null)
                throw new InvalidOperationException(
                    "--azure requires --endpoint <url> (your Azure OpenAI resource endpoint, e.g. https://myresource.openai.azure.com/).");
            if (opts.Azure && string.IsNullOrWhiteSpace(opts.DeploymentName))
                throw new InvalidOperationException(
                    "--azure requires --deployment-name <name> (your Azure OpenAI deployment name).");
            if (!opts.Azure && string.IsNullOrWhiteSpace(opts.Model))
                throw new InvalidOperationException(
                    "--model is required when using --endpoint.");

            // Classic path's dataset-existence check — AFTER connection-config validation, restoring the
            // original precedence (a typo'd --dataset path must not mask a missing --endpoint/--azure/
            // --model, which was this path's behavior before the --sut dataset-first requirement above was
            // added and accidentally moved to the top of the whole method instead of staying --sut-only).
            if (!opts.Dataset.Exists)
                throw new FileNotFoundException($"Dataset not found: {opts.Dataset.FullName}");

            // Resolved identifier: deployment name for Azure, model name for OpenAI-compatible
            resolvedName = opts.Azure ? opts.DeploymentName! : opts.Model!;

            // 2. Resolve system prompt
            var systemPrompt = opts.SystemPrompt;
            if (opts.SystemPromptFile is { Exists: true })
                systemPrompt = await File.ReadAllTextAsync(opts.SystemPromptFile.FullName, ct);

            // 3. Create IChatClient → IStreamableAgent
            chatClient = VerboseLog.Wrap(opts.Azure
                ? EndpointFactory.CreateAzure(opts.Endpoint, opts.DeploymentName!, opts.ApiKey)
                : EndpointFactory.CreateOpenAICompatible(opts.Endpoint!, opts.Model!, opts.ApiKey), "agent");

            var chatOptions = new ChatOptions();
            if (opts.Temperature != 0f) chatOptions.Temperature = opts.Temperature;
            if (opts.MaxTokens.HasValue) chatOptions.MaxOutputTokens = opts.MaxTokens.Value;

            agent = chatClient.AsEvaluableAgent(
                name: resolvedName,
                systemPrompt: systemPrompt,
                chatOptions: chatOptions);
        }

        // 4. Load dataset
        var testCases = await DatasetLoaderFactory.LoadAsync(opts.Dataset.FullName, ct);
        if (testCases.Count == 0)
            throw new InvalidOperationException($"Dataset is empty: {opts.Dataset.FullName}");

        // 5. Create harness (optionally with LLM judge)
        IChatClient? judgeClient = opts.JudgeEndpoint is not null
            ? VerboseLog.Wrap(EndpointFactory.CreateOpenAICompatible(
                opts.JudgeEndpoint, opts.JudgeModel ?? resolvedName, opts.ApiKey), "judge")
            : null;
        var harness = judgeClient is not null
            ? new MAFEvaluationHarness(judgeClient, verbose: opts.Verbose && !opts.Quiet)
            : new MAFEvaluationHarness(verbose: opts.Verbose && !opts.Quiet);

        // 5b. --metrics (Item 4, D1 bridge): resolve every selected name to a real IMetric instance NOW,
        // before the (possibly expensive) scan runs — a metric that needs an LLM judge but has none
        // available fails here, not after wastefully running the whole batch. --judge wins as the
        // evaluator when set; otherwise falls back to the SUT's own chatClient (null for a --sut target).
        // Skipped entirely for the stochastic path (opts.Runs > 1, step 6b below): that path never
        // consumes selectedMetricInstances at all (--metrics scoring isn't wired to it yet), so resolving
        // here was both wasted work AND could THROW for a reason (no evaluator client available) that
        // --runs > 1 is supposed to just warn-and-ignore, not fail the whole run on (review).
        IReadOnlyList<IMetric>? selectedMetricInstances = null;
        IChatClient? metricsEvaluatorClient = judgeClient ?? chatClient;
        if (selectedMetrics is not null && opts.Runs <= 1)
        {
            selectedMetricInstances = selectedMetrics
                .Select(name => MetricCatalog.Resolve(name, metricsEvaluatorClient))
                .ToList();
        }

        // 6. Run evaluation
        if (!opts.Quiet)
            ConsoleReporter.WriteHeader(resolvedName, opts.Dataset.Name, testCases.Count);

        var evalOptions = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            ModelName = resolvedName,
            Verbose = opts.Verbose && !opts.Quiet,
            SelectedMetrics = selectedMetrics,
        };

        // 6b. Stochastic evaluation path (--runs > 1) — --metrics scoring is not wired for this path yet;
        // say so honestly rather than silently dropping the flag.
        if (opts.Runs > 1)
        {
            if (selectedMetrics is not null && !opts.Quiet)
                Console.Error.WriteLine(
                    "  Warning: --metrics has no effect combined with --runs > 1 in this release " +
                    "(stochastic scoring is not wired to the named-metric pipeline yet).");
            return await ExecuteStochasticAsync(opts, harness, agent, testCases, evalOptions, ct);
        }

        // 6c. Standard single-run evaluation path
        var summary = await harness.RunBatchAsync(agent, testCases, evalOptions, ct);

        // 6d. --metrics (Item 4, D1 bridge, continued): score every selected metric against each test
        // case's REAL captured response (never re-invokes the agent) and attach the results to
        // TestResult.MetricResults — an existing, already-wired field (TestSummaryExtensions.MapTestResult
        // already projects it into the exported report's MetricScores) that nothing previously populated.
        // Purely additive: the harness's own pass/fail gate above is completely unchanged.
        if (selectedMetricInstances is not null)
        {
            var metricsBuilder = AgentEvalBuilder.Create();
            if (metricsEvaluatorClient is not null)
                metricsBuilder.WithEvaluatorClient(metricsEvaluatorClient);
            foreach (var metric in selectedMetricInstances)
                metricsBuilder.AddMetric(metric);

            await using var metricRunner = await metricsBuilder.BuildAsync(ct);
            for (var i = 0; i < testCases.Count && i < summary.Results.Count; i++)
            {
                var metricsContext = ToMetricsContext(testCases[i], summary.Results[i]);
                summary.Results[i].MetricResults = await metricRunner.EvaluateAsync(selectedMetrics!, metricsContext, ct);
            }
        }

        // 7. Export
        var report = summary.ToEvaluationReport(
            agentName: resolvedName,
            modelName: resolvedName,
            endpoint: opts.Sut is not null ? $"sut:{opts.Sut}" : (opts.Endpoint ?? "azure"));

        // Directory format is handled exclusively via --output-dir, not the stream-based export path
        var isDirectoryFormat = opts.Format.Equals("directory", StringComparison.OrdinalIgnoreCase)
            || opts.Format.Equals("dir", StringComparison.OrdinalIgnoreCase);

        if (isDirectoryFormat && opts.OutputDir is null)
            throw new ArgumentException(
                "The 'directory' format produces a structured directory (results.jsonl, summary.json, run.json). " +
                "Specify --output-dir <path> to write the directory output.",
                nameof(opts.Format));

        if (!isDirectoryFormat)
            await ExportHandler.ExportAsync(report, opts.Format, opts.Output, ct);

        // 7b. Directory export (ADR-002) — can coexist with single-file export
        if (opts.OutputDir is not null)
        {
            var dirName = DirectoryExporter.GenerateDirectoryName(report);
            var dirPath = new DirectoryInfo(Path.Combine(opts.OutputDir.FullName, dirName));
            await ExportHandler.ExportToDirectoryAsync(report, dirPath, opts.Dataset.FullName, ct);
            if (!opts.Quiet)
                Console.Error.WriteLine($"  Results written to: {dirPath.FullName}");
        }

        // 8. Summary (unless --quiet)
        if (!opts.Quiet)
            ConsoleReporter.WriteSummary(summary);

        // 9. Exit code: 0 = all passed, 1 = any failure
        return summary.AllPassed ? ExitCodes.Success : ExitCodes.TestFailure;
    }

    /// <summary>
    /// Builds the <see cref="EvaluationContext"/> a named <c>--metrics</c> selection is scored against —
    /// the SAME captured response the harness already produced (never re-invokes the agent). Mirrors
    /// <c>DatasetTestCaseExtensions.ToEvaluationContext</c> exactly, plus <see cref="TestResult.ToolUsage"/>
    /// (that extension omits it; agentic metrics like <c>code_tool_success</c> need it and would otherwise
    /// always see "no tools called").
    /// </summary>
    internal static EvaluationContext ToMetricsContext(DatasetTestCase testCase, TestResult testResult) => new()
    {
        Input = testCase.Input,
        Output = testResult.ActualOutput ?? "",
        Context = testCase.Context is null ? null : string.Join("\n", testCase.Context),
        GroundTruth = testCase.ExpectedOutput,
        ToolUsage = testResult.ToolUsage,
        Performance = testResult.Performance,
    };

    /// <summary>
    /// Stochastic evaluation path — runs each test case N times and reports statistics.
    /// </summary>
    private static async Task<int> ExecuteStochasticAsync(
        EvalOptions opts,
        MAFEvaluationHarness harness,
        IEvaluableAgent agent,
        IReadOnlyList<DatasetTestCase> datasetTestCases,
        EvaluationOptions evalOptions,
        CancellationToken ct)
    {
        var runner = new StochasticRunner(harness, statisticsCalculator: null, evalOptions);
        var stochasticOptions = new StochasticOptions(
            Runs: opts.Runs,
            SuccessRateThreshold: opts.SuccessThreshold);

        if (!opts.Quiet)
            Console.Error.WriteLine($"  Stochastic mode: {opts.Runs} runs per test case, threshold={opts.SuccessThreshold:P0}");

        var allPassed = true;
        var results = new List<StochasticResult>();

        foreach (var datasetTestCase in datasetTestCases)
        {
            var testCase = datasetTestCase.ToTestCase();

            if (!opts.Quiet)
                Console.Error.WriteLine($"\n  Test: {testCase.Name ?? "unnamed"}");

            var result = await runner.RunStochasticTestAsync(agent, testCase, stochasticOptions, ct);
            results.Add(result);

            if (!opts.Quiet)
            {
                result.PrintTable(testCase.Name ?? "Test");
                Console.Error.WriteLine($"    {result.Summary}");
            }

            if (!result.Passed)
                allPassed = false;
        }

        // Summary
        if (!opts.Quiet)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  === Stochastic Summary ===");
            Console.Error.WriteLine($"  Test cases: {datasetTestCases.Count}");
            Console.Error.WriteLine($"  Passed: {results.Count(r => r.Passed)}/{results.Count}");
            Console.Error.WriteLine($"  Threshold: {opts.SuccessThreshold:P0}");
        }

        return allPassed ? ExitCodes.Success : ExitCodes.TestFailure;
    }
}

/// <summary>Parsed options for the eval command.</summary>
internal sealed class EvalOptions
{
    public required FileInfo Dataset { get; init; }
    public string? Endpoint { get; init; }
    public bool Azure { get; init; }
    public string? Model { get; init; }
    public string? DeploymentName { get; init; }
    public string? ApiKey { get; init; }

    /// <summary>Built-in SUT selector (<c>--sut</c>, Track 2 PR2); <c>copilot-studio</c> is the only built-in target today.</summary>
    public string? Sut { get; init; }

    /// <summary>
    /// Per-built-in-target bound options, keyed by <see cref="ISutTarget.Sut"/> — mirrors
    /// <see cref="RedTeamOptions.TargetOptions"/>'s exact shape/purpose for the `eval` verb.
    /// </summary>
    public IReadOnlyDictionary<string, ISutTargetOptions?> TargetOptions { get; init; }
        = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase);

    public string? Metrics { get; init; }
    public int Runs { get; init; } = 1;
    public double SuccessThreshold { get; init; } = 0.8;
    public string? SystemPrompt { get; init; }
    public FileInfo? SystemPromptFile { get; init; }
    public float Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? JudgeEndpoint { get; init; }
    public string? JudgeModel { get; init; }
    public required string Format { get; init; }
    public FileInfo? Output { get; init; }
    public DirectoryInfo? OutputDir { get; init; }
    public bool Verbose { get; init; }
    public bool Quiet { get; init; }
}
