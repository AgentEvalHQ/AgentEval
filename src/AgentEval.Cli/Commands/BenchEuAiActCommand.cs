// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Articles.Building;
using AgentEval.Compliance.EuAiAct.Articles.Loading;
using AgentEval.Compliance.Core;
using AgentEval.Compliance.EuAiAct.DomainPacks.HighRiskCredit;
using AgentEval.Compliance.EuAiAct.DomainPacks.HighRiskEducation;
using AgentEval.Compliance.EuAiAct.DomainPacks.HighRiskEmployment;
using AgentEval.Compliance.EuAiAct.Reporting;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench eu-ai-act</c> subcommand.
/// Runs an EU AI Act benchmark against an agent response, persists the full evidence chain,
/// and writes both a Markdown and a PDF report.
/// </summary>
public static class BenchEuAiActCommand
{
    /// <summary>Runs the bench eu-ai-act command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        string? responseText = null,
        bool azureFromEnv = false,
        CancellationToken ct = default) =>
        RunAsync(preset, subject, rootOverride, inputText, evaluatorOverride: null, agentOverride: null, responseText: responseText, azureFromEnv: azureFromEnv, ct: ct);

    /// <summary>Runs the bench eu-ai-act command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        IEvaluator? evaluatorOverride,
        IEvaluableAgent? agentOverride = null,
        string? responseText = null,
        bool azureFromEnv = false,
        CancellationToken ct = default)
    {
        // ── Workspace setup ──────────────────────────────────────────────────
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null) return 1;
            rootOverride = canonical;
        }
        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("Could not find a solution root (.sln, .slnx, or .git). " +
                "Provide --root or run from within a solution directory.");
            return 1;
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        if (!Directory.Exists(agentEvalDir))
        {
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init` first.");
            return 1;
        }

        // ── Judge / evaluator ────────────────────────────────────────────────
        // Phase-6 Task 6.8: load the embedded EU AI Act judge system prompt and pass
        // it through. See BenchCommand for rationale.
        var euAiActPrompt = EmbeddedPromptLoader.Load(
            typeof(EuAiActBenchmark).Assembly,
            "eu-ai-act-judge-system.v1.md");
        var (resolvedJudge, judgeModelName, exitCode) = JudgeFactory.Resolve(
            evaluatorOverride,
            judgeKind: "EU AI Act benchmark",
            systemPrompt: euAiActPrompt);
        if (resolvedJudge is null) return exitCode;
        IEvaluator judge = resolvedJudge;

        // ── Agent under test ─────────────────────────────────────────────────
        // Priority: --sut (agentOverride, e.g. copilot-studio) > --azure-from-env > grade the supplied
        // --response as before. The judge resolves AZURE_OPENAI_JUDGE_* first (see JudgeFactory) so
        // agent and judge can point at different endpoints.
        AgentEval.Core.IEvaluableAgent? agent = agentOverride;
        if (agent is null && azureFromEnv)
        {
            var (builtAgent, agentExit) = AzureChatAgentFactory.TryBuildFromEnv(subject);
            if (builtAgent is null) return agentExit;
            agent = builtAgent;
        }

        // ── Build registry ───────────────────────────────────────────────────
        EuAiActArticlesRegistry articles;
        ScenarioToAtomicEval scenarioBuilder;
        try
        {
            var loader = new ArticleScenarioYamlLoader();
            scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: judgeModelName, agent: agent);
            var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
            articles = new EuAiActArticlesRegistry(loader, articleBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load EU AI Act article registry: {ex.Message}");
            return 1;
        }

        // ── Select preset ────────────────────────────────────────────────────
        CompositeEval benchmark;
        try
        {
            benchmark = ResolvePreset(preset, articles, scenarioBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build EU AI Act preset '{preset}': {ex.Message}");
            return 1;
        }

        // ── Build input ──────────────────────────────────────────────────────
        // Phase-7 Task 7.22: --input is now required at the CLI level (see Program.cs).
        // The prior hard-coded fixture was removed because it produced
        // identical evidence across every run, defeating the point of a per-
        // subject benchmark. The internal RunAsync still accepts null inputText
        // for in-process callers; in that case we hold the line by failing fast
        // with a clear message rather than silently producing fixture-graded
        // evidence.
        if (string.IsNullOrWhiteSpace(inputText))
        {
            Console.Error.WriteLine(
                "Error: input text is required for bench eu-ai-act. " +
                "Pass --input '<your prompt>' at the CLI, or supply inputText programmatically.");
            return 1;
        }
        string agentResponse;
        if (agent is not null)
        {
            // Response is produced per scenario by the live agent (see AgentScenarioEval);
            // this placeholder is never graded.
            agentResponse = "(driven per-scenario by the live agent under test)";
            Console.Error.WriteLine(
                $"[bench eu-ai-act] Driving live agent '{subject}' per scenario via --azure-from-env; " +
                "each scenario's own prompt is sent to the agent and its real answer is graded.");
        }
        else if (!string.IsNullOrWhiteSpace(responseText))
        {
            agentResponse = responseText;
        }
        else
        {
            // No real response supplied: grade a built-in FIXTURE. Warn loudly — the produced
            // compliance evidence does NOT reflect the named subject agent (BUG-18).
            agentResponse =
                "I should clearly identify myself as an AI assistant when interacting with users. " +
                "For high-risk decisions, I'll defer to human review.";
            Console.Error.WriteLine(
                $"[bench eu-ai-act] WARNING: no --response/--response-file supplied — grading a built-in " +
                $"FIXTURE response, not a real agent output. The produced compliance evidence does NOT reflect " +
                $"subject '{subject}'. Pass --response-file <path> (or --response \"...\") with the agent's actual answer.");
        }
        var evalInput = new EvalInput(Query: inputText, Response: agentResponse);

        // ── Run benchmark ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        // Workspace hygiene: sweep stale 24h+ sentinels (.invalid.json / .lock
        // / .tmp). Phase-0 0.9: only CLI writer paths sweep; MC does not.
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);

        Console.WriteLine($"Running EU AI Act benchmark ({preset}) for subject '{subject}'...");

        string runId;
        EvalResult compositeResult;
        try
        {
            var runner = new EuAiActBenchmarkRunner();
            (runId, compositeResult) = await runner.RunAsync(store, subjectIdentity, benchmark, evalInput, ct: ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark run failed: {ex.Message}");
            return 1;
        }

        // ── Generate reports ─────────────────────────────────────────────────
        // Split the composite preset string (e.g. "standard+high-risk-employment") into a
        // base preset + ordered domain-pack list so eu-ai-act-evidence.json records each
        // pack separately. The base name is what the schema's `preset` enum accepts; the
        // packs land in a structured `domainPacks` array.
        var presetTokens = preset.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var basePreset = presetTokens.Length > 0 ? presetTokens[0].ToLowerInvariant() : "standard";
        var domainPacks = presetTokens.Skip(1).Select(t => t.ToLowerInvariant()).ToArray();

        var reporter = new EuAiActComplianceReporter(articles);
        var options = new EuAiActReportOptions(
            Preset: basePreset,
            DomainPacks: domainPacks,
            JudgeMode: "mode-a",                // EU AI Act has no stochastic --runs path in v1; symmetric with GDPR call site
            JudgeModel: judgeModelName);
        EuAiActComplianceEvidence evidence;
        try
        {
            evidence = await reporter.SaveReportAsync(store, subjectIdentity, runId, compositeResult, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to generate compliance report: {ex.Message}");
            return 1;
        }

        // Derive output directory (mirrors FileSystemOutputStore path convention)
        var sanitizedSubject = FileSystemLayout.Sanitize(subject);
        var ts = evidence.Base.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var outputDir = Path.Combine(agentEvalDir, "compliance", "EU-AI-Act", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        // Markdown report
        try
        {
            var md = new MarkdownRenderer().Render(evidence);
            var mdPath = Path.Combine(outputDir, "report.md");
            await File.WriteAllTextAsync(mdPath, md);
            Console.WriteLine($"Markdown report: {mdPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write Markdown report: {ex.Message}");
        }

        // PDF report
        try
        {
            var pdfRenderer = new EuAiActPdfRenderer(articles);
            var pdfPath = Path.Combine(outputDir, "report.pdf");
            await pdfRenderer.RenderAsync(evidence, pdfPath);
            Console.WriteLine($"PDF report: {pdfPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write PDF report: {ex.Message}");
        }

        // ── Exit code ─────────────────────────────────────────────────────────
        var overall = evidence.Summary.OverallStatus;
        Console.WriteLine($"Overall result: {overall} (score {evidence.Summary.OverallScore:P0})");

        return overall switch
        {
            "PASS" => 0,
            "FAIL" => 2,
            _ => 2  // WARN also returns non-zero for CI strictness
        };
    }

    /// <summary>
    /// Resolves a preset specification into a <see cref="CompositeEval"/>.
    /// Base presets: <c>smoke</c>, <c>standard</c>, <c>audit</c>/<c>auditgrade</c>.
    /// Supports additive domain-pack composition with <c>+</c>:
    /// <list type="bullet">
    ///   <item><c>standard+high-risk-employment</c> — Standard with employment domain-pack additions.</item>
    ///   <item><c>standard+high-risk-credit</c> — Standard with credit-scoring domain-pack additions.</item>
    ///   <item><c>standard+high-risk-education</c> — Standard with education domain-pack additions.</item>
    ///   <item><c>standard+high-risk-employment+high-risk-credit</c> — multiple packs accumulated.</item>
    /// </list>
    /// </summary>
    /// <param name="presetSpec">The preset specification, e.g. <c>"standard"</c> or <c>"standard+high-risk-employment+high-risk-credit"</c>.</param>
    /// <param name="articles">The loaded articles registry.</param>
    /// <param name="scenarioBuilder">The scenario-to-eval builder for domain-pack scenarios.</param>
    /// <returns>The resolved <see cref="CompositeEval"/>.</returns>
    /// <exception cref="ArgumentException">Thrown for unknown base presets or domain packs.</exception>
    internal static CompositeEval ResolvePreset(
        string presetSpec,
        EuAiActArticlesRegistry articles,
        ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(scenarioBuilder);

        var tokens = presetSpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw new ArgumentException("Empty preset specification.", nameof(presetSpec));

        var basePreset = tokens[0].ToLowerInvariant() switch
        {
            "smoke"                  => EuAiActBenchmark.Smoke(articles),
            "standard"               => EuAiActBenchmark.Standard(articles),
            "audit" or "auditgrade"  => EuAiActBenchmark.AuditGrade(articles),
            _                        => throw new ArgumentException($"Unknown EU AI Act preset '{tokens[0]}'.", nameof(presetSpec))
        };

        if (tokens.Length == 1) return basePreset;

        // Accumulate additions from all domain packs, then apply in one pass.
        var allAdditions = new Dictionary<string, List<EvalComponent>>();
        foreach (var pack in tokens.Skip(1))
        {
            var packAdditions = pack.ToLowerInvariant() switch
            {
                "high-risk-employment" => HighRiskEmploymentScenarios.Load(scenarioBuilder),
                "high-risk-credit"     => HighRiskCreditScenarios.Load(scenarioBuilder),
                "high-risk-education"  => HighRiskEducationScenarios.Load(scenarioBuilder),
                _                      => throw new ArgumentException($"Unknown EU AI Act domain pack '{pack}'.", nameof(presetSpec))
            };

            foreach (var (key, components) in packAdditions)
            {
                if (!allAdditions.TryGetValue(key, out var list))
                {
                    list = new List<EvalComponent>();
                    allAdditions[key] = list;
                }
                list.AddRange(components);
            }
        }

        var merged = allAdditions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<EvalComponent>)kv.Value);

        return basePreset.WithExtraScenarios(merged);
    }

}
