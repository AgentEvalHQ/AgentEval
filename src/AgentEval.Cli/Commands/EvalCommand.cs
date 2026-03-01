// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Cli.Infrastructure;
using AgentEval.Cli.Output;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.Exporters;
using AgentEval.MAF;
using AgentEval.Models;
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
        var azureFlag = new Option<bool>("--azure") { Description = "Use Azure OpenAI (reads AZURE_OPENAI_* env vars)" };

        // Model
        var modelOpt = new Option<string>("--model")
            { Required = true, Description = "Model or deployment name" };

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

        // Judge (LLM-as-judge for scoring)
        var judgeEndpointOpt = new Option<string?>("--judge")
            { Description = "Separate endpoint for LLM-as-judge evaluation" };
        var judgeModelOpt = new Option<string?>("--judge-model")
            { Description = "Model for judge (default: same as --model)" };

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
        command.Options.Add(apiKeyOpt);
        command.Options.Add(systemPromptOpt);
        command.Options.Add(systemPromptFileOpt);
        command.Options.Add(temperatureOpt);
        command.Options.Add(maxTokensOpt);
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
                Model = parseResult.GetValue(modelOpt)!,
                ApiKey = parseResult.GetValue(apiKeyOpt),
                SystemPrompt = parseResult.GetValue(systemPromptOpt),
                SystemPromptFile = parseResult.GetValue(systemPromptFileOpt),
                Temperature = parseResult.GetValue(temperatureOpt),
                MaxTokens = parseResult.GetValue(maxTokensOpt),
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
                Console.Error.WriteLine($"  ❌ Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        return command;
    }

    /// <summary>
    /// Core execution logic — separated from command wiring for testability.
    /// Returns exit code: 0 = all passed, 1 = test failure, 3 = runtime error.
    /// </summary>
    internal static async Task<int> ExecuteAsync(EvalOptions opts, CancellationToken ct)
    {
        // 1. Validate
        if (opts.Endpoint is null && !opts.Azure)
            throw new InvalidOperationException("Specify --endpoint <url> or --azure.");
        if (!opts.Dataset.Exists)
            throw new FileNotFoundException($"Dataset not found: {opts.Dataset.FullName}");

        // 2. Resolve system prompt
        var systemPrompt = opts.SystemPrompt;
        if (opts.SystemPromptFile is { Exists: true })
            systemPrompt = await File.ReadAllTextAsync(opts.SystemPromptFile.FullName, ct);

        // 3. Create IChatClient → IStreamableAgent
        IChatClient chatClient = opts.Azure
            ? EndpointFactory.CreateAzure(opts.Endpoint, opts.Model, opts.ApiKey)
            : EndpointFactory.CreateOpenAICompatible(opts.Endpoint!, opts.Model, opts.ApiKey);

        var chatOptions = new ChatOptions();
        if (opts.Temperature != 0f) chatOptions.Temperature = opts.Temperature;
        if (opts.MaxTokens.HasValue) chatOptions.MaxOutputTokens = opts.MaxTokens.Value;

        var agent = chatClient.AsEvaluableAgent(
            name: opts.Model,
            systemPrompt: systemPrompt,
            chatOptions: chatOptions);

        // 4. Load dataset
        var testCases = await DatasetLoaderFactory.LoadAsync(opts.Dataset.FullName, ct);
        if (testCases.Count == 0)
            throw new InvalidOperationException($"Dataset is empty: {opts.Dataset.FullName}");

        // 5. Create harness (optionally with LLM judge)
        IChatClient? judgeClient = opts.JudgeEndpoint is not null
            ? EndpointFactory.CreateOpenAICompatible(
                opts.JudgeEndpoint, opts.JudgeModel ?? opts.Model, opts.ApiKey)
            : null;
        var harness = judgeClient is not null
            ? new MAFEvaluationHarness(judgeClient, verbose: opts.Verbose && !opts.Quiet)
            : new MAFEvaluationHarness(verbose: opts.Verbose && !opts.Quiet);

        // 6. Run evaluation
        if (!opts.Quiet)
            ConsoleReporter.WriteHeader(opts.Model, opts.Dataset.Name, testCases.Count);

        var summary = await harness.RunBatchAsync(agent, testCases, new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            ModelName = opts.Model,
            Verbose = opts.Verbose && !opts.Quiet,
        }, ct);

        // 7. Export
        var report = summary.ToEvaluationReport(
            agentName: opts.Model,
            modelName: opts.Model,
            endpoint: opts.Endpoint ?? "azure");

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
                Console.Error.WriteLine($"  \U0001f4c1 Results written to: {dirPath.FullName}");
        }

        // 8. Summary (unless --quiet)
        if (!opts.Quiet)
            ConsoleReporter.WriteSummary(summary);

        // 9. Exit code: 0 = all passed, 1 = any failure
        return summary.AllPassed ? ExitCodes.Success : ExitCodes.TestFailure;
    }
}

/// <summary>Parsed options for the eval command.</summary>
internal sealed class EvalOptions
{
    public required FileInfo Dataset { get; init; }
    public string? Endpoint { get; init; }
    public bool Azure { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
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
