// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha during the v1.1 CLI consolidation.
// The `agenteval redteam` command exposes the same scanner surface that the
// `agenteval bench owasp` and `agenteval bench mitre` commands wrap with curated
// presets — `redteam` stays as the low-level, fully-parameterised entry point.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'agenteval redteam' command — run security scans against an AI agent.
/// </summary>
internal static class RedTeamCommand
{
    public static Command Create()
    {
        var command = new Command("redteam", "Run red team security scans against an AI agent");

        // Endpoint (mutually exclusive group)
        var endpointOpt = new Option<string?>("--endpoint") { Description = "OpenAI-compatible API endpoint URL" };
        var azureFlag = new Option<bool>("--azure") { Description = "Use Azure OpenAI (requires --endpoint and --deployment-name)" };

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

        // Attack selection
        var attacksOpt = new Option<string?>("--attacks")
            { Description = "Comma-separated attack types (e.g., PromptInjection,Jailbreak). Default: all" };

        // Intensity
        var intensityOpt = new Option<string>("--intensity")
            { DefaultValueFactory = _ => "moderate", Description = "Scan intensity: quick | moderate | comprehensive" };

        // Options
        var failFastFlag = new Option<bool>("--fail-fast")
            { Description = "Stop scanning on first successful attack" };
        var maxProbesOpt = new Option<int>("--max-probes")
            { DefaultValueFactory = _ => 0, Description = "Maximum probes per attack (0 = unlimited)" };

        // Judge (LLM-as-judge for evaluation)
        var judgeEndpointOpt = new Option<string?>("--judge")
            { Description = "Separate endpoint for LLM judge (evaluates attack success)" };
        var judgeModelOpt = new Option<string?>("--judge-model")
            { Description = "Model for judge (default: same as --model)" };

        // Output
        var formatOpt = new Option<string>("--format")
            { DefaultValueFactory = _ => "markdown", Description = "Export format: json | sarif | markdown | md | junit" };
        var outputOpt = new Option<FileInfo?>("-o", "--output") { Description = "Output file (default: stdout)" };

        // Verbosity
        var verboseFlag = new Option<bool>("--verbose") { Description = "Show detailed progress" };
        var quietFlag = new Option<bool>("--quiet") { Description = "Suppress all output except the export" };

        command.Options.Add(endpointOpt);
        command.Options.Add(azureFlag);
        command.Options.Add(modelOpt);
        command.Options.Add(deploymentNameOpt);
        command.Options.Add(apiKeyOpt);
        command.Options.Add(systemPromptOpt);
        command.Options.Add(attacksOpt);
        command.Options.Add(intensityOpt);
        command.Options.Add(failFastFlag);
        command.Options.Add(maxProbesOpt);
        command.Options.Add(judgeEndpointOpt);
        command.Options.Add(judgeModelOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(verboseFlag);
        command.Options.Add(quietFlag);

        command.SetAction(async (parseResult, ct) =>
        {
            var opts = new RedTeamOptions
            {
                Endpoint = parseResult.GetValue(endpointOpt),
                Azure = parseResult.GetValue(azureFlag),
                Model = parseResult.GetValue(modelOpt),
                DeploymentName = parseResult.GetValue(deploymentNameOpt),
                ApiKey = parseResult.GetValue(apiKeyOpt),
                SystemPrompt = parseResult.GetValue(systemPromptOpt),
                Attacks = parseResult.GetValue(attacksOpt),
                Intensity = parseResult.GetValue(intensityOpt)!,
                FailFast = parseResult.GetValue(failFastFlag),
                MaxProbes = parseResult.GetValue(maxProbesOpt),
                JudgeEndpoint = parseResult.GetValue(judgeEndpointOpt),
                JudgeModel = parseResult.GetValue(judgeModelOpt),
                Format = parseResult.GetValue(formatOpt)!,
                Output = parseResult.GetValue(outputOpt),
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
    /// Core execution logic — separated from command wiring for testability.
    /// </summary>
    internal static async Task<int> ExecuteAsync(RedTeamOptions opts, CancellationToken ct)
    {
        // 1. Validate
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

        // Resolved identifier: deployment name for Azure, model name for OpenAI-compatible
        var resolvedName = opts.Azure ? opts.DeploymentName! : opts.Model!;

        // 2. Create IChatClient → IEvaluableAgent
        IChatClient chatClient = opts.Azure
            ? EndpointFactory.CreateAzure(opts.Endpoint, opts.DeploymentName!, opts.ApiKey)
            : EndpointFactory.CreateOpenAICompatible(opts.Endpoint!, opts.Model!, opts.ApiKey);

        var agent = chatClient.AsEvaluableAgent(
            name: resolvedName,
            systemPrompt: opts.SystemPrompt);

        // 3. Resolve attacks
        IReadOnlyList<IAttackType>? attacks = null;
        if (opts.Attacks is not null)
        {
            var attackNames = opts.Attacks
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var resolvedAttacks = new List<IAttackType>();
            foreach (var name in attackNames)
            {
                var attack = Attack.ByName(name);
                if (attack is null)
                    throw new ArgumentException(
                        $"Unknown attack type: '{name}'. Available: {string.Join(", ", Attack.AvailableNames)}");
                resolvedAttacks.Add(attack);
            }
            attacks = resolvedAttacks;
        }

        // 4. Resolve intensity
        var intensity = opts.Intensity.ToLowerInvariant() switch
        {
            "quick" => Intensity.Quick,
            "moderate" => Intensity.Moderate,
            "comprehensive" => Intensity.Comprehensive,
            _ => throw new ArgumentException(
                $"Unknown intensity: '{opts.Intensity}'. Valid: quick, moderate, comprehensive"),
        };

        // 5. Create ScanOptions
        IChatClient? judgeClient = opts.JudgeEndpoint is not null
            ? EndpointFactory.CreateOpenAICompatible(
                opts.JudgeEndpoint, opts.JudgeModel ?? resolvedName, opts.ApiKey)
            : null;

        var scanOptions = new ScanOptions
        {
            AttackTypes = attacks,
            Intensity = intensity,
            FailFast = opts.FailFast,
            MaxProbesPerAttack = opts.MaxProbes,
            JudgeClient = (object?)judgeClient,
            IncludeEvidence = true,
        };

        // 6. Run scan
        if (!opts.Quiet)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  AgentEval Red Team Scanner");
            Console.Error.WriteLine($"  Model: {opts.Model}");
            Console.Error.WriteLine($"  Attacks: {(attacks is null ? "all (9)" : string.Join(", ", attacks.Select(a => a.Name)))}");
            Console.Error.WriteLine($"  Intensity: {intensity}");
            Console.Error.WriteLine();
        }

        var runner = new RedTeamRunner();

        // Progress callback for verbose mode
        if (opts.Verbose && !opts.Quiet)
        {
            scanOptions = new ScanOptions
            {
                AttackTypes = scanOptions.AttackTypes,
                Intensity = scanOptions.Intensity,
                FailFast = scanOptions.FailFast,
                MaxProbesPerAttack = scanOptions.MaxProbesPerAttack,
                JudgeClient = scanOptions.JudgeClient,
                IncludeEvidence = scanOptions.IncludeEvidence,
                OnProgress = progress =>
                    Console.Error.WriteLine(
                        $"  [{progress.CompletedProbes}/{progress.TotalProbes}] " +
                        $"{progress.CurrentAttack} — {progress.LastOutcome}"),
            };
        }

        var result = await runner.ScanAsync(agent, scanOptions, ct);

        // 7. Export
        var exporter = ResolveExporter(opts.Format);

        if (opts.Output is not null)
        {
            await exporter.ExportToFileAsync(result, opts.Output.FullName, ct);
            if (!opts.Quiet)
                Console.Error.WriteLine($"  Report written to: {opts.Output.FullName}");
        }
        else
        {
            var report = exporter.Export(result);
            Console.Write(report);
        }

        // 8. Summary (unless --quiet)
        if (!opts.Quiet)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  === Red Team Summary ===");
            Console.Error.WriteLine($"  {result.Summary}");
            Console.Error.WriteLine($"  Score: {result.OverallScore:F1}/100");
            Console.Error.WriteLine($"  Verdict: {result.Verdict}");
        }

        // 9. Exit code: 0 = passed, 1 = vulnerabilities found
        return result.Passed ? ExitCodes.Success : ExitCodes.TestFailure;
    }

    /// <summary>
    /// Resolves a report exporter from the format string.
    /// </summary>
    internal static IReportExporter ResolveExporter(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "json" => new JsonReportExporter(),
            "sarif" => new SarifReportExporter(),
            "markdown" or "md" => new MarkdownReportExporter(),
            "junit" or "xml" => new JUnitReportExporter(),
            _ => throw new ArgumentException(
                $"Unknown red team report format: '{format}'. Valid: json, sarif, markdown, md, junit, xml"),
        };
    }
}

/// <summary>Parsed options for the redteam command.</summary>
internal sealed class RedTeamOptions
{
    public string? Endpoint { get; init; }
    public bool Azure { get; init; }
    public string? Model { get; init; }
    public string? DeploymentName { get; init; }
    public string? ApiKey { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Attacks { get; init; }
    public required string Intensity { get; init; }
    public bool FailFast { get; init; }
    public int MaxProbes { get; init; }
    public string? JudgeEndpoint { get; init; }
    public string? JudgeModel { get; init; }
    public required string Format { get; init; }
    public FileInfo? Output { get; init; }
    public bool Verbose { get; init; }
    public bool Quiet { get; init; }
}
