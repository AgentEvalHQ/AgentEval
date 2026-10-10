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
using System.Globalization;
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
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;
using AgentEval.Snapshots;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'agenteval eval' command — evaluate an AI agent against a dataset.
/// </summary>
internal static class EvalCommand
{
    /// <summary>The <c>--format</c> value used when the option is not given.</summary>
    internal const string DefaultFormat = "json";

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
        var temperatureOpt = new Option<float?>("--temperature")
        {
            Description = "Sampling temperature sent with every agent call. Omit it to use the provider's default; " +
                          "a value given, including 0, is sent as given (0 narrows sampling but does not guarantee " +
                          "identical outputs). Not applied with --sut, which configures its own model.",
        };
        var maxTokensOpt = new Option<int?>("--max-tokens") { Description = "Maximum output tokens" };

        // Metric selection
        var metricsOpt = new Option<string?>("--metrics")
            { Description = "Comma-separated named metrics to score in addition to the pass/fail check (e.g., llm_relevance,code_tool_success). Omitted: none are scored. 'agenteval list --type metrics' shows which names are accepted." };

        // Judge (LLM-as-judge for scoring)
        var judgeEndpointOpt = new Option<string?>("--judge")
            { Description = "Separate endpoint for LLM-as-judge evaluation" };
        var judgeModelOpt = new Option<string?>("--judge-model")
            { Description = "Model for judge (default: same as --model)" };

        // Stochastic evaluation
        var runsOpt = new Option<int>("--runs")
        {
            DefaultValueFactory = _ => 1,
            Description = "Runs per test case (default: 1; must be at least 1). Above 1 is stochastic analysis, which " +
                          "the stochastic runner accepts from 3 runs: a test case passes when its pass rate reaches " +
                          "--success-threshold. The export has one entry per test case, scored as the mean over its " +
                          "runs, with the run count, runs passed, pass rate and score SD as metric columns.",
        };
        var thresholdOpt = new Option<double>("--success-threshold")
            { DefaultValueFactory = _ => 0.8, Description = "Success rate threshold for stochastic evaluation (default: 0.8)" };

        // Output
        var formatOpt = new Option<string>("--format")
            { DefaultValueFactory = _ => DefaultFormat, Description = "Export format: json | junit (alias xml) | markdown (alias md) | trx | csv. For the structured directory, use --output-dir." };
        var outputOpt = new Option<FileInfo?>("-o", "--output") { Description = "Output file (default: stdout)" };
        var outputDirOpt = new Option<DirectoryInfo?>("--output-dir")
            { Description = "Write structured results to a directory (ADR-002 format: results.jsonl + summary.json + run.json)" };

        // Golden trace (regression against a saved run)
        var saveGoldenOpt = new Option<FileInfo?>("--save-golden")
        {
            Description = "Save this run as a golden trace: each test case's verdict, output and tool calls with their " +
                          "arguments, as JSON to commit beside the dataset. With --golden, the comparison is made first.",
        };
        var goldenOpt = new Option<FileInfo?>("--golden")
        {
            Description = "Compare this run with a golden trace saved by --save-golden. Each test case is reported as " +
                          "regressed, improved, tools changed, output changed, unchanged, added or removed (stderr). " +
                          "The exit code then follows the comparison: 1 when a test case that passed in the golden trace " +
                          "fails now, else 0, so a test that was already failing does not fail the build.",
        };
        var failOnToolChangeFlag = new Option<bool>("--fail-on-tool-change")
        {
            Description = "With --golden, also exit 1 when any test case called different tools, or the same tools with " +
                          "different arguments, than in the golden trace.",
        };

        // Verbosity
        var verboseFlag = new Option<bool>("--verbose") { Description = "Show detailed progress" };
        var quietFlag = new Option<bool>("--quiet") { Description = "Suppress all output except the export" };
        var aefOpt = new Option<DirectoryInfo?>("--aef")
            { Description = "Write the run as sealed AEF evidence under this folder (default: .agenteval/aef in a workspace)" };
        var noAefFlag = new Option<bool>("--no-aef") { Description = "Write no AEF run" };
        var subjectOpt = new Option<string?>("--subject")
            { Description = "The subject's AEF ref (kind:name, e.g. agent:support-bot; default model:<model>)" };
        var subjectVersionOpt = new Option<string?>("--subject-version")
            { Description = "The subject's exact version, recorded in the AEF run (a checkpoint lane needs it)" };

        command.Options.Add(datasetOpt);
        command.Options.Add(aefOpt);
        command.Options.Add(noAefFlag);
        command.Options.Add(subjectOpt);
        command.Options.Add(subjectVersionOpt);
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
        command.Options.Add(saveGoldenOpt);
        command.Options.Add(goldenOpt);
        command.Options.Add(failOnToolChangeFlag);
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
                SaveGolden = parseResult.GetValue(saveGoldenOpt),
                Golden = parseResult.GetValue(goldenOpt),
                FailOnToolChange = parseResult.GetValue(failOnToolChangeFlag),
                Verbose = parseResult.GetValue(verboseFlag),
                Quiet = parseResult.GetValue(quietFlag),
                Aef = parseResult.GetValue(aefOpt),
                NoAef = parseResult.GetValue(noAefFlag),
                Subject = parseResult.GetValue(subjectOpt),
                SubjectVersion = parseResult.GetValue(subjectVersionOpt),
            };

            // No target at all is a usage error (exit 2), as for every command that evaluates an agent: there is
            // nothing to evaluate. ExecuteAsync still throws for it, for its direct callers; through 0.42 the
            // command line reported that throw as a runtime error (exit 3).
            if (opts.Sut is null && opts.Endpoint is null && !opts.Azure)
            {
                Console.Error.WriteLine("  Error: Specify --endpoint <url> or --azure, or --sut <target>.");
                return ExitCodes.UsageError;
            }

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
    /// is not set. <paramref name="agentClientOverride"/>, when non-null, replaces the chat client the
    /// <c>--endpoint</c>/<c>--azure</c> path would build — the same kind of seam for that path. Every validation
    /// still runs and the agent is still built from <paramref name="opts"/>; only the client construction is
    /// replaced. It has no effect when <c>--sut</c> is set.
    /// Returns exit code: 0 = all passed, 1 = test failure, 2 = usage error (<c>--runs</c>, the golden-trace options),
    /// 3 = runtime error. With <c>--golden</c>, 1 means a regression against the golden trace (or, with
    /// <c>--fail-on-tool-change</c>, a tool change) rather than any failing test.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        EvalOptions opts, CancellationToken ct, IEvaluableAgent? sutOverride = null, IChatClient? agentClientOverride = null)
    {
        // --runs (and, in stochastic mode, --success-threshold) are checked before anything else: a value the
        // command cannot honour is a usage error, exit 2. Before this check, 0 and negative values silently ran
        // once, and 2 got past this method only to fail inside StochasticOptions.Validate() as a runtime error.
        if (ValidateRuns(opts) is { } runsError)
        {
            Console.Error.WriteLine($"  Error: {runsError}");
            return ExitCodes.UsageError;
        }

        // An AEF run of --runs above 1 needs trial lines and their rollup, which are not written yet: refuse an explicit
        // --aef rather than write nothing, and say so for the workspace default.
        if (opts.Runs > 1 && opts.Aef is not null && !opts.NoAef)
        {
            Console.Error.WriteLine("  Error: --aef cannot be combined with --runs above 1 yet: AEF trial runs are not written in this release.");
            return ExitCodes.UsageError;
        }

        // The golden trace is read before any agent call, so a missing or unreadable file costs nothing.
        var (golden, goldenError) = await LoadGoldenAsync(opts, ct);
        if (goldenError is not null)
        {
            Console.Error.WriteLine($"  Error: {goldenError}");
            return ExitCodes.UsageError;
        }

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
        bool standIn;   // a caller-supplied stand-in answered for the subject: the run is never written as live
        IChatClient? chatClient = null;   // hoisted: --metrics' LLM-based fallback evaluator needs this outside the else block

        if (sutAgent is not null)
        {
            standIn = sutOverride is not null;
            // A built-in target (e.g. copilot-studio) constructed itself; the --endpoint/--azure/--model
            // path below is entirely skipped, matching RedTeamCommand's existing --sut branch. No raw
            // IChatClient is exposed for a --sut target, so chatClient stays null here — an LLM-based
            // --metrics selection then needs an explicit --judge (see MetricCatalog.Resolve's own error).
            agent = sutAgent;
            resolvedName = sutResolvedName!;

            // The target builds and configures its own model, so these agent options never reach it. Say so
            // rather than accept them silently (respects --quiet, like the --metrics warning further down).
            var notApplied = AgentOptionsNotAppliedBySut(opts);
            if (notApplied.Count > 0 && !opts.Quiet)
                Console.Error.WriteLine(
                    $"  Warning: --sut {opts.Sut} configures its own model; {string.Join(", ", notApplied)} " +
                    $"{(notApplied.Count == 1 ? "is" : "are")} not applied.");
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
            standIn = agentClientOverride is not null;
            chatClient = CliChatClientDiagnostics.Wrap(agentClientOverride ?? (opts.Azure
                ? EndpointFactory.CreateAzure(opts.Endpoint, opts.DeploymentName!, opts.ApiKey)
                : EndpointFactory.CreateOpenAICompatible(opts.Endpoint!, opts.Model!, opts.ApiKey)), "agent");

            agent = chatClient.AsEvaluableAgent(
                name: resolvedName,
                systemPrompt: systemPrompt,
                chatOptions: BuildAgentChatOptions(opts));
        }

        // 4. Load dataset
        var testCases = await DatasetLoaderFactory.LoadAsync(opts.Dataset.FullName, ct);
        if (testCases.Count == 0)
            throw new InvalidOperationException($"Dataset is empty: {opts.Dataset.FullName}");

        // 5. Create harness (optionally with LLM judge)
        IChatClient? judgeClient = opts.JudgeEndpoint is not null
            ? CliChatClientDiagnostics.Wrap(EndpointFactory.CreateOpenAICompatible(
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

            if (!opts.NoAef && !opts.Quiet && EvalAef.Root(opts) is not null)
                Console.Error.WriteLine("  AEF: --runs above 1 is not written as AEF yet, so this evaluation wrote no AEF run.");
            return await ExecuteStochasticAsync(opts, harness, agent, testCases, evalOptions, resolvedName, ct);
        }

        // 6c. Standard single-run evaluation path
        var startedAt = DateTimeOffset.UtcNow;
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

        // 6e. The run as sealed AEF evidence (S1 #5a): into the workspace's .agenteval/aef, or --aef. A write failure
        // fails the command only when --aef asked for it; otherwise it is a warning and the evaluation stands.
        // A real target (--endpoint, --azure, --sut) is live; a stand-in a caller passes in is never written as live.
        var targetMode = standIn ? AefTargetMode.Mocked : AefTargetMode.Live;
        var aefFailed = WriteAef(opts, summary, testCases, resolvedName, targetMode, startedAt, DateTimeOffset.UtcNow);

        // 7. Export
        var report = summary.ToEvaluationReport(
            agentName: resolvedName,
            modelName: resolvedName,
            endpoint: EndpointLabel(opts));
        await ExportReportAsync(opts, report, ct);

        // 8. Summary (unless --quiet)
        if (!opts.Quiet)
            ConsoleReporter.WriteSummary(summary);

        // 9. Golden trace: compare with the saved run first, then save this one (both may be given, to update it).
        var thisRun = GoldenTrace.FromResults(summary.Results, resolvedName);
        GoldenTraceComparison? comparison = null;
        if (golden is not null)
        {
            comparison = GoldenTraceComparer.Compare(golden, thisRun);
            WriteGoldenComparison(comparison, opts);
        }

        if (opts.SaveGolden is not null)
        {
            await thisRun.SaveAsync(opts.SaveGolden.FullName, ct);
            if (!opts.Quiet)
                Console.Error.WriteLine($"  Golden trace saved: {opts.SaveGolden.FullName}");
        }

        // 10. Exit code: 3 when an explicit --aef could not be written, whatever the results; else 0 = all passed,
        // 1 = any failure. Against a golden trace, 1 = a regression (or a tool change with --fail-on-tool-change); a test
        // that was already failing in the golden trace does not fail the run.
        if (aefFailed)
            return ExitCodes.RuntimeError;
        if (comparison is not null)
            return comparison.HasRegression || (opts.FailOnToolChange && comparison.HasToolChange)
                ? ExitCodes.TestFailure
                : ExitCodes.Success;
        return summary.AllPassed ? ExitCodes.Success : ExitCodes.TestFailure;
    }

    /// <summary>
    /// Checks the golden-trace options and loads <c>--golden</c>. Returns the trace (or null when not given) and the
    /// usage error, if any.
    /// </summary>
    internal static async Task<(GoldenTrace? Golden, string? Error)> LoadGoldenAsync(EvalOptions opts, CancellationToken ct)
    {
        if (opts.FailOnToolChange && opts.Golden is null)
            return (null, "--fail-on-tool-change needs --golden <file> to compare with.");
        if (opts.Runs > 1 && (opts.Golden is not null || opts.SaveGolden is not null))
            return (null, "a golden trace records one run per test case; --golden and --save-golden cannot be combined with --runs above 1.");
        if (opts.Golden is null)
            return (null, null);
        if (!opts.Golden.Exists)
            return (null, $"golden trace not found: {opts.Golden.FullName}");
        try
        {
            return (await GoldenTrace.LoadAsync(opts.Golden.FullName, ct), null);
        }
        catch (InvalidDataException ex)
        {
            return (null, ex.Message);
        }
    }

    private static void WriteGoldenComparison(GoldenTraceComparison comparison, EvalOptions opts)
    {
        // Regressions are printed even with --quiet: they decide the exit code.
        var w = Console.Error;
        if (!opts.Quiet)
        {
            w.WriteLine();
            w.WriteLine($"  === Compared with the golden trace {opts.Golden!.Name} ===");
            w.WriteLine(
                $"  Regressed {comparison.Count(TraceChange.Regressed)}, improved {comparison.Count(TraceChange.Improved)}, " +
                $"tools changed {comparison.Count(TraceChange.ToolsChanged)}, output changed {comparison.Count(TraceChange.OutputChanged)}, " +
                $"unchanged {comparison.Count(TraceChange.Unchanged)}, added {comparison.Count(TraceChange.Added)}, " +
                $"removed {comparison.Count(TraceChange.Removed)}.");
        }

        foreach (var c in comparison.Cases.OrderBy(c => c.Change))
        {
            if (c.Change == TraceChange.Unchanged || (opts.Quiet && c.Change != TraceChange.Regressed))
                continue;
            w.WriteLine($"  {Label(c.Change),-15} {c.Name}{(c.Detail is null ? "" : $": {c.Detail}")}");
        }

        static string Label(TraceChange change) => change switch
        {
            TraceChange.Regressed => "REGRESSED",
            TraceChange.Improved => "improved",
            TraceChange.ToolsChanged => "tools changed",
            TraceChange.OutputChanged => "output changed",
            TraceChange.Added => "added",
            TraceChange.Removed => "removed",
            _ => "unchanged",
        };
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
    /// Writes the run as sealed AEF evidence (S1 #5a); returns true only when an explicit <c>--aef</c> could not be
    /// written. A write the user did not ask for fails with a warning: the evaluation's own result stands.
    /// </summary>
    private static bool WriteAef(
        EvalOptions opts, TestSummary summary, IReadOnlyList<DatasetTestCase> cases, string resolvedName,
        AefTargetMode targetMode, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var root = EvalAef.Root(opts);
        if (root is null)
        {
            if (!opts.NoAef && !opts.Quiet)
                Console.Error.WriteLine("  AEF: no .agenteval workspace here, so no run was written (--aef <dir> writes one anywhere).");
            return false;
        }

        try
        {
            var subjectRef = opts.Subject ?? AefEvalWriter.Ref("model", resolvedName);   // resolvedName answered the cases
            var (directory, runHash) = AefEvalWriter.Write(Path.Combine(root, "runs"), summary, EvalAef.CaseIds(cases), new AefEvalRunOptions
            {
                SubjectRef = subjectRef,
                SubjectKind = AefEvalWriter.KindOf(subjectRef),
                SubjectVersion = opts.SubjectVersion,
                Endpoint = opts.Endpoint,
                // The judge the harness was given: only with --judge, on --judge-model or the subject's model.
                JudgeModel = opts.JudgeEndpoint is null ? null : opts.JudgeModel ?? resolvedName,
                TargetMode = targetMode,
                DatasetName = Path.GetRelativePath(Directory.GetCurrentDirectory(), opts.Dataset.FullName).Replace('\\', '/'),
                DatasetBytes = File.ReadAllBytes(opts.Dataset.FullName),
            }, startedAt, endedAt);
            if (!opts.Quiet)
                Console.Error.WriteLine($"  AEF run: {directory} (run hash {runHash[..12]}…)");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (opts.Aef is not null)
            {
                Console.Error.WriteLine($"  Error: the AEF run was not written: {ex.Message}");
                return true;
            }

            if (!opts.Quiet)
                Console.Error.WriteLine($"  Warning: the AEF run was not written: {ex.Message}");
            return false;
        }
    }

    private static string EndpointLabel(EvalOptions opts) =>
        opts.Sut is not null ? $"sut:{opts.Sut}" : (opts.Endpoint ?? "azure");

    /// <summary>
    /// Writes <paramref name="report"/> in the requested <c>--format</c> (to <c>-o</c>, or stdout) and, with
    /// <c>--output-dir</c>, as the ADR-002 directory too. The single-run and the stochastic path both export here.
    /// </summary>
    private static async Task ExportReportAsync(EvalOptions opts, EvaluationReport report, CancellationToken ct)
    {
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

        // Directory export (ADR-002) — can coexist with single-file export
        if (opts.OutputDir is not null)
        {
            var dirName = DirectoryExporter.GenerateDirectoryName(report);
            var dirPath = new DirectoryInfo(Path.Combine(opts.OutputDir.FullName, dirName));
            await ExportHandler.ExportToDirectoryAsync(report, dirPath, opts.Dataset.FullName, ct);
            if (!opts.Quiet)
                Console.Error.WriteLine($"  Results written to: {dirPath.FullName}");
        }
    }

    /// <summary>
    /// Why <c>--runs</c> cannot be honoured, or <see langword="null"/> when it can. Below 1 is refused here. Above 1
    /// (stochastic mode) is checked by <see cref="StochasticOptions.Validate"/> itself — the same check the run
    /// would hit — so this method never restates that runner's limits (its minimum run count and the
    /// <c>--success-threshold</c> range). At exactly 1, <c>--success-threshold</c> is not used and not checked.
    /// </summary>
    internal static string? ValidateRuns(EvalOptions opts)
    {
        if (opts.Runs < 1)
            return $"--runs must be at least 1 (got {opts.Runs}).";

        if (opts.Runs == 1)
            return null;

        try
        {
            ToStochasticOptions(opts).Validate();
            return null;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            var given = ex.ParamName switch
            {
                nameof(StochasticOptions.Runs) => $"--runs {opts.Runs}",
                nameof(StochasticOptions.SuccessRateThreshold) =>
                    $"--success-threshold {opts.SuccessThreshold.ToString(CultureInfo.InvariantCulture)}",
                _ => $"stochastic option '{ex.ParamName}'",
            };
            return $"{given} is not accepted in stochastic mode (--runs greater than 1): {ThrownText(ex)}";
        }
    }

    private static StochasticOptions ToStochasticOptions(EvalOptions opts) =>
        new(Runs: opts.Runs, SuccessRateThreshold: opts.SuccessThreshold);

    // ArgumentOutOfRangeException.Message appends " (Parameter '…')" and an "Actual value was …" line to the text
    // it was thrown with. The caller already names the flag and its value, so keep only the thrown text.
    private static string ThrownText(ArgumentException ex)
    {
        var text = ex.Message.Split('\n')[0].TrimEnd('\r');
        var suffix = ex.ParamName is null ? null : $" (Parameter '{ex.ParamName}')";
        return suffix is not null && text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;
    }

    /// <summary>
    /// The <see cref="ChatOptions"/> the <c>--endpoint</c>/<c>--azure</c> path sends with every agent call. An option
    /// that was not given is left unset, so the provider's own default applies; one that was given is sent as given —
    /// including a temperature of 0, which used to be dropped because 0 was also the "not given" value.
    /// </summary>
    internal static ChatOptions BuildAgentChatOptions(EvalOptions opts)
    {
        var chatOptions = new ChatOptions();
        if (opts.Temperature is { } temperature) chatOptions.Temperature = temperature;
        if (opts.MaxTokens is { } maxTokens) chatOptions.MaxOutputTokens = maxTokens;
        return chatOptions;
    }

    /// <summary>
    /// The agent options a built-in <c>--sut</c> target does not use: the target builds and configures its own
    /// model, and <see cref="CommonTargetOptions"/> carries none of these to it.
    /// </summary>
    internal static IReadOnlyList<string> AgentOptionsNotAppliedBySut(EvalOptions opts)
    {
        var notApplied = new List<string>();
        if (opts.Temperature is not null) notApplied.Add("--temperature");
        if (opts.MaxTokens is not null) notApplied.Add("--max-tokens");
        if (opts.SystemPrompt is not null) notApplied.Add("--system-prompt");
        if (opts.SystemPromptFile is not null) notApplied.Add("--system-prompt-file");
        return notApplied;
    }

    /// <summary>
    /// Stochastic evaluation path — runs each test case N times, exports one entry per test case
    /// (<see cref="StochasticReport"/>) and prints the statistics to stderr.
    /// </summary>
    private static async Task<int> ExecuteStochasticAsync(
        EvalOptions opts,
        MAFEvaluationHarness harness,
        IEvaluableAgent agent,
        IReadOnlyList<DatasetTestCase> datasetTestCases,
        EvaluationOptions evalOptions,
        string resolvedName,
        CancellationToken ct)
    {
        var runner = new StochasticRunner(harness, statisticsCalculator: null, evalOptions);
        var stochasticOptions = ToStochasticOptions(opts);

        if (!opts.Quiet)
            Console.Error.WriteLine($"  Stochastic mode: {opts.Runs} runs per test case, threshold={opts.SuccessThreshold:P0}");

        var allPassed = true;
        var results = new List<StochasticResult>();
        var startTime = DateTimeOffset.UtcNow;

        foreach (var datasetTestCase in datasetTestCases)
        {
            var testCase = datasetTestCase.ToTestCase();

            if (!opts.Quiet)
                Console.Error.WriteLine($"\n  Test: {testCase.Name ?? "unnamed"}");

            var result = await runner.RunStochasticTestAsync(agent, testCase, stochasticOptions, ct);
            results.Add(result);

            if (!opts.Quiet)
            {
                // stdout is the export's channel, and this mode has no export; the table is a human report, so it
                // goes to stderr with every other line here (OutputOptions writes to Console.Out by default).
                result.PrintTable(testCase.Name ?? "Test", new OutputOptions { Writer = Console.Error });
                Console.Error.WriteLine($"    {result.Summary}");
            }

            if (!result.Passed)
                allPassed = false;
        }

        // Export: one entry per test case, its verdict the stochastic one. Same suite name as the single-run batch.
        var report = StochasticReport.Build(
            results, "BatchEvaluation", opts.Runs, opts.SuccessThreshold, startTime, DateTimeOffset.UtcNow,
            agentName: resolvedName, modelName: resolvedName, endpoint: EndpointLabel(opts));
        await ExportReportAsync(opts, report, ct);

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
internal static class EvalAef
{
    /// <summary>The AEF root a run goes to: <c>--aef</c>, else a workspace's <c>.agenteval/aef</c>, else none.</summary>
    public static string? Root(EvalOptions opts)
    {
        if (opts.NoAef)
            return null;
        if (opts.Aef is not null)
            return opts.Aef.FullName;
        var workspace = WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        return workspace is not null && Directory.Exists(Path.Combine(workspace, ".agenteval"))
            ? Path.Combine(workspace, ".agenteval", "aef")
            : null;
    }

    /// <summary>
    /// A case's id: its dataset id when every case has a distinct one, else case-n. Never the test name: a case without an
    /// id is named after the start of its input, and content stays out of a run captured with content off ([RUN-11]).
    /// </summary>
    public static IReadOnlyList<string> CaseIds(IReadOnlyList<DatasetTestCase> cases)
    {
        var ids = cases.Select(c => c.Id).ToList();
        return ids.All(i => !string.IsNullOrWhiteSpace(i)) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count
            ? ids
            : cases.Select((_, i) => $"case-{i + 1}").ToList();
    }
}

internal sealed class EvalOptions
{
    /// <summary>Where to write the AEF run (<c>--aef</c>); null writes into a workspace's <c>.agenteval/aef</c> when there is one.</summary>
    public DirectoryInfo? Aef { get; init; }

    /// <summary><c>--no-aef</c>: write no AEF run.</summary>
    public bool NoAef { get; init; }

    /// <summary><c>--subject</c>: the subject's AEF ref, <c>kind:name</c>.</summary>
    public string? Subject { get; init; }

    /// <summary><c>--subject-version</c>: the subject's exact version.</summary>
    public string? SubjectVersion { get; init; }

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

    /// <summary>
    /// Sampling temperature for the agent's model, or <see langword="null"/> when <c>--temperature</c> was not given
    /// (the provider's default then applies). Not applied with <see cref="Sut"/>.
    /// </summary>
    public float? Temperature { get; init; }

    public int? MaxTokens { get; init; }
    public string? JudgeEndpoint { get; init; }
    public string? JudgeModel { get; init; }
    public required string Format { get; init; }

    public FileInfo? Output { get; init; }
    public DirectoryInfo? OutputDir { get; init; }

    /// <summary>Where to save this run as a golden trace (<c>--save-golden</c>).</summary>
    public FileInfo? SaveGolden { get; init; }

    /// <summary>The golden trace to compare this run with (<c>--golden</c>).</summary>
    public FileInfo? Golden { get; init; }

    /// <summary>With <see cref="Golden"/>, also fail when any test case's tool calls changed.</summary>
    public bool FailOnToolChange { get; init; }
    public bool Verbose { get; init; }
    public bool Quiet { get; init; }
}
