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
using AgentEval.RedTeam.Baseline;
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

        // Attacker (LLM that drives attacker-LLM multi-turn attacks: Crescendo / PAIR / TAP)
        var attackerEndpointOpt = new Option<string?>("--attacker")
            { Description = "Endpoint for the attacker LLM (drives Crescendo/PAIR/TAP; non-deterministic). Required by PAIR/TAP." };
        var attackerModelOpt = new Option<string?>("--attacker-model")
            { Description = "Model for the attacker LLM (default: same as --model)" };

        // Output
        var formatOpt = new Option<string>("--format")
            { DefaultValueFactory = _ => "markdown", Description = "Export format: json | sarif | markdown | md | junit" };
        var outputOpt = new Option<FileInfo?>("-o", "--output") { Description = "Output file (default: stdout)" };

        // Baseline / CI regression gating (Wave E)
        var saveBaselineOpt = new Option<FileInfo?>("--save-baseline")
            { Description = "After the scan, write a regression baseline (JSON) to this path." };
        var baselineOpt = new Option<FileInfo?>("--baseline")
            { Description = "Compare the scan to this baseline (JSON), print the diff, and gate the exit code on regression." };
        var failOnOpt = new Option<string>("--fail-on")
            { DefaultValueFactory = _ => "vuln", Description = "Exit-code gate: vuln (any vulnerability) | regression (new vs --baseline) | never." };

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
        command.Options.Add(attackerEndpointOpt);
        command.Options.Add(attackerModelOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(saveBaselineOpt);
        command.Options.Add(baselineOpt);
        command.Options.Add(failOnOpt);
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
                AttackerEndpoint = parseResult.GetValue(attackerEndpointOpt),
                AttackerModel = parseResult.GetValue(attackerModelOpt),
                Format = parseResult.GetValue(formatOpt)!,
                Output = parseResult.GetValue(outputOpt),
                SaveBaseline = parseResult.GetValue(saveBaselineOpt),
                Baseline = parseResult.GetValue(baselineOpt),
                FailOn = parseResult.GetValue(failOnOpt)!,
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

        // Validate --fail-on up front so a typo fails fast, before an expensive scan runs.
        var failOn = opts.FailOn.ToLowerInvariant();
        if (failOn is not ("vuln" or "regression" or "never"))
            throw new InvalidOperationException(
                $"Unknown --fail-on value: '{opts.FailOn}'. Valid: vuln, regression, never.");
        if (failOn == "regression" && opts.Baseline is null && !opts.Quiet)
            Console.Error.WriteLine(
                "  Warning: --fail-on regression has no effect without --baseline <file>; the run will always exit 0.");

        // Fail fast on a missing baseline BEFORE running an expensive scan, rather than scanning then erroring.
        if (opts.Baseline is not null && !opts.Baseline.Exists)
            throw new InvalidOperationException($"Baseline file not found: {opts.Baseline.FullName}");

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

        // Wave C′: the attacker LLM drives Crescendo/PAIR/TAP. Distinct from the judge (it generates, the judge scores).
        IChatClient? attackerClient = opts.AttackerEndpoint is not null
            ? EndpointFactory.CreateOpenAICompatible(
                opts.AttackerEndpoint, opts.AttackerModel ?? resolvedName, opts.ApiKey)
            : null;

        // Fail fast: PAIR/TAP are fundamentally attacker-driven and would error mid-scan without an attacker.
        if (attackerClient is null && attacks is not null &&
            attacks.FirstOrDefault(a => a.Name is "PAIR" or "TAP") is { } needsAttacker)
            throw new InvalidOperationException(
                $"Attack '{needsAttacker.Name}' requires an attacker LLM — pass --attacker <url> (and optionally --attacker-model).");

        // Build the verbose progress callback up front so it can go into the single ScanOptions
        // initializer below. (Previously a second construction site rebuilt ScanOptions and silently
        // dropped any property not copied — e.g. AttackerClient — breaking PAIR/TAP under --verbose.)
        Action<ScanProgress>? onProgress = opts.Verbose && !opts.Quiet
            ? progress => Console.Error.WriteLine(
                $"  [{progress.CompletedProbes}/{progress.TotalProbes}] " +
                $"{progress.CurrentAttack} — {progress.LastOutcome}")
            : null;

        var scanOptions = new ScanOptions
        {
            AttackTypes = attacks,
            Intensity = intensity,
            FailFast = opts.FailFast,
            MaxProbesPerAttack = opts.MaxProbes,
            JudgeClient = judgeClient, // GAP-19: the runner re-evaluates Inconclusive probes with this judge (capped at IntentToAct)
            AttackerClient = attackerClient, // Wave C′: drives attacker-LLM multi-turn attacks (Crescendo/PAIR/TAP)
            IncludeEvidence = true,
            OnProgress = onProgress,
        };

        // 6. Run scan
        if (!opts.Quiet)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  AgentEval Red Team Scanner");
            Console.Error.WriteLine($"  Model: {resolvedName}");
            Console.Error.WriteLine($"  Attacks: {(attacks is null ? $"all ({Attack.All.Count})" : string.Join(", ", attacks.Select(a => a.Name)))}");
            Console.Error.WriteLine($"  Intensity: {intensity}");
            // GAP-19: the judge IS consumed — it re-evaluates probes the deterministic evaluators left Inconclusive.
            if (opts.JudgeEndpoint is not null)
                Console.Error.WriteLine($"  Judge: {opts.JudgeModel ?? resolvedName} (LLM-judge fallback on inconclusive probes, capped at IntentToAct fidelity)");
            // Wave C′: an attacker LLM makes Crescendo/PAIR/TAP LLM-driven — and therefore non-deterministic.
            if (opts.AttackerEndpoint is not null)
                Console.Error.WriteLine($"  Attacker: {opts.AttackerModel ?? resolvedName} (LLM-driven attacks — NON-DETERMINISTIC; not a stable baseline)");
            Console.Error.WriteLine();
        }

        var runner = new RedTeamRunner();

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
            // RC-6 honesty: lead with the conclusive-only score + coverage, not the inconclusive-diluted OverallScore.
            Console.Error.WriteLine($"  Verdict: {result.Verdict}  (score {result.ConclusiveScore:F1}/100 over {result.Coverage:F0}% conclusive coverage)");
            if (result.InconclusiveProbes > 0)
                Console.Error.WriteLine($"  Note: {result.InconclusiveProbes}/{result.TotalProbes} probes were inconclusive — that lowers coverage, not the pass rate.");
        }

        // 9. Baseline capture (W-E1) — write a snapshot the next run can diff against.
        if (opts.SaveBaseline is not null)
        {
            await RedTeamBaseline.FromResult(result, version: "1.0").SaveAsync(opts.SaveBaseline.FullName, ct);
            if (!opts.Quiet)
            {
                Console.Error.WriteLine($"  Baseline written to: {opts.SaveBaseline.FullName}");
                // Honesty: a baseline captured from a low-coverage run carries that coverage forward;
                // future comparisons measure drops relative to it, not relative to a perfect 100%.
                if (result.ConclusiveRate < 1.0 - ComparisonThresholds.Default.RegressionCoverageDrop)
                    Console.Error.WriteLine(
                        $"  Note: this baseline's coverage is only {result.ConclusiveRate * 100:F0}% conclusive — " +
                        "future runs are compared against that level, not 100%.");
            }
        }

        // 10. Regression gate (W-E2) — diff against a baseline. A non-comparable scan (intensity mismatch / FailFast
        // truncation) throws from Compare and is surfaced honestly as a runtime error by the caller's try/catch.
        RegressionStatus? regression = null;
        if (opts.Baseline is not null)
        {
            var baseline = await RedTeamBaseline.LoadAsync(opts.Baseline.FullName, ct);
            var comparison = new RedTeamBaselineComparer().Compare(result, baseline);
            regression = comparison.Status;
            if (!opts.Quiet)
                PrintComparison(comparison);
        }

        // 11. Honest exit-code gate (W-E3).
        return ComputeExitCode(failOn, regression, result.Passed);
    }

    /// <summary>
    /// Pure exit-code gate (W-E3), separated from the scan for testability. A regression versus the baseline is the
    /// strongest signal (<see cref="ExitCodes.RegressionFailure"/> = 4) and takes precedence over the absolute
    /// vulnerability gate (<see cref="ExitCodes.TestFailure"/> = 1); <paramref name="failOn"/> selects which gate
    /// applies. <c>never</c> always passes; <c>regression</c> ignores absolute vulnerabilities.
    /// </summary>
    internal static int ComputeExitCode(string failOn, RegressionStatus? regression, bool passed)
    {
        var isRegression = regression == RegressionStatus.Regression;
        return failOn.ToLowerInvariant() switch
        {
            "never" => ExitCodes.Success,
            "regression" => isRegression ? ExitCodes.RegressionFailure : ExitCodes.Success,
            _ /* vuln */ => isRegression
                ? ExitCodes.RegressionFailure
                : (passed ? ExitCodes.Success : ExitCodes.TestFailure),
        };
    }

    /// <summary>Renders a baseline comparison (new / fixed / persistent findings + deltas) to stderr.</summary>
    private static void PrintComparison(RedTeamComparison c)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  === Baseline Comparison ===");
        Console.Error.WriteLine($"  Status: {c.StatusEmoji} {c.Status}  (score {c.ScoreDelta:+0.0;-0.0;0.0} vs baseline {c.Baseline.OverallScore:F1})");
        Console.Error.WriteLine($"  New: {c.NewVulnerabilities.Count}   Fixed: {c.ResolvedVulnerabilities.Count}   Persistent: {c.PersistentVulnerabilities.Count}");
        if (c.NotReTested.Count > 0)
            Console.Error.WriteLine($"  Not re-tested: {c.NotReTested.Count} baseline attack(s) with {c.NotReTested.Sum(n => n.KnownVulnerabilities)} known vuln(s) — excluded from Fixed.");
        if (c.CoverageDrop > 0)
            Console.Error.WriteLine($"  Coverage drop: {c.CoverageDrop * 100:F0}% (current conclusive {c.Current.ConclusiveRate * 100:F0}% vs baseline {c.BaselineCoverage * 100:F0}%).");
        foreach (var v in c.NewVulnerabilities)
            Console.Error.WriteLine($"    + NEW [{v.Severity}] {v.AttackName} / {v.ProbeId}: {v.Reason}");
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
    public string? AttackerEndpoint { get; init; }
    public string? AttackerModel { get; init; }
    public required string Format { get; init; }
    public FileInfo? Output { get; init; }
    public FileInfo? SaveBaseline { get; init; }
    public FileInfo? Baseline { get; init; }
    public string FailOn { get; init; } = "vuln";
    public bool Verbose { get; init; }
    public bool Quiet { get; init; }
}
