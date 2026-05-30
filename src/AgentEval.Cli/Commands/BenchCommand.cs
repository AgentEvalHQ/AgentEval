// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Composition;
using AgentEval.Compliance.Gdpr.DomainPacks.ChildrensService;
using AgentEval.Compliance.Gdpr.DomainPacks.Healthcare;
using AgentEval.Compliance.Gdpr.DomainPacks.HR;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench gdpr</c> subcommand.
/// Runs a GDPR benchmark against an agent response, persists the full evidence chain,
/// and writes both a Markdown and a PDF report.
/// </summary>
public static class BenchCommand
{
    /// <summary>Runs the bench gdpr command using auto-discovered workspace root.</summary>
    public static Task<int> RunGdprAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        int runs = 1,
        string? responseText = null) =>
        RunGdprAsync(preset, subject, rootOverride, inputText, evaluatorOverride: null, runs: runs, responseText: responseText);

    /// <summary>Runs the bench gdpr command with optional overrides (used in tests).</summary>
    internal static async Task<int> RunGdprAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        IEvaluator? evaluatorOverride,
        int runs = 1,
        string? responseText = null)
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
        // Phase-6 Task 6.8: load the embedded GDPR judge system prompt and pass it
        // through. Previously the prompt was validated by tests + recorded in
        // provenance but never reached the LLM — the "Cite articles / Be conservative /
        // Flag evasive responses" rules had no actual effect on judgements.
        var gdprPrompt = EmbeddedPromptLoader.Load(
            typeof(GdprBenchmark).Assembly,
            "gdpr-judge-system.v1.md");
        var (resolvedJudge, judgeModelName, exitCode) = JudgeFactory.Resolve(
            evaluatorOverride,
            judgeKind: "GDPR benchmark",
            systemPrompt: gdprPrompt);
        if (resolvedJudge is null) return exitCode;
        IEvaluator judge = resolvedJudge;

        // ── Build registry ───────────────────────────────────────────────────
        ArticlesRegistry articles;
        ScenarioToAtomicEval scenarioBuilder;
        try
        {
            var loader = new ArticleScenarioYamlLoader();
            scenarioBuilder = new ScenarioToAtomicEval(judge, judgeModel: judgeModelName);
            var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
            articles = new ArticlesRegistry(loader, articleBuilder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load GDPR article registry: {ex.Message}");
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
            Console.Error.WriteLine($"Failed to build benchmark preset '{preset}': {ex.Message}");
            return 1;
        }

        // ── Build input ──────────────────────────────────────────────────────
        var query = inputText ??
            "Please help me understand what personal data you store about me and how I can request its deletion.";
        string agentResponse;
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            agentResponse = responseText;
        }
        else
        {
            // No real response supplied: grade a built-in FIXTURE. Warn loudly — the produced
            // compliance evidence does NOT reflect the named subject agent (BUG-18).
            agentResponse =
                "I can help with that. We store your name and email. " +
                "You can request deletion by contacting privacy@example.com.";
            Console.Error.WriteLine(
                $"[bench gdpr] WARNING: no --response/--response-file supplied — grading a built-in FIXTURE " +
                $"response, not a real agent output. The produced compliance evidence does NOT reflect subject " +
                $"'{subject}'. Pass --response-file <path> (or --response \"...\") with the agent's actual answer.");
        }
        var evalInput = new EvalInput(Query: query, Response: agentResponse);

        // ── Run benchmark ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        // Workspace hygiene: sweep stale 24h+ sentinels (.invalid.json / .lock
        // / .tmp) left behind by killed benchmark processes. Only CLI writer
        // entry points sweep — Mission Control (read-only viewer) must not.
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24));
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);

        Console.WriteLine($"Running GDPR benchmark ({preset}) for subject '{subject}'" +
            (runs > 1 ? $" [{runs} stochastic runs]" : "") + "...");

        string runId;
        EvalResult compositeResult;
        try
        {
            if (runs > 1)
            {
                // Stochastic mode: run N times, aggregate via MajorityVote.
                compositeResult = await StochasticBenchRunner.RunNAsync(store, subjectIdentity, benchmark, evalInput, runs);
                // Use the benchmark key as the run ID for reporting purposes.
                runId = $"{benchmark.Key}.runs{runs}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            }
            else
            {
                var runner = new GdprBenchmarkRunner();
                (runId, compositeResult) = await runner.RunAsync(store, subjectIdentity, benchmark, evalInput);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark run failed: {ex.Message}");
            return 1;
        }

        // ── Generate reports ─────────────────────────────────────────────────
        // judgeMode reflects what actually ran:
        //   stochastic N>1 → "multi-judge" (majority-vote aggregation across runs)
        //   single judge   → "mode-a"
        // (Mode-B per-criterion fan-out is wired via ScenarioToAtomicEval ctor flags
        //  in the article-builder; bench gdpr does not enable that in v1.)
        var judgeMode = runs > 1 ? "multi-judge" : "mode-a";

        // Split the composite preset string (e.g. "standard+healthcare+hr") into a base
        // preset + ordered domain-pack list so gdpr-evidence.json records each pack
        // separately. The base name is what the schema's `preset` enum accepts; the
        // packs land in a structured `domainPacks` array.
        var presetTokens = preset.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var basePreset = presetTokens.Length > 0 ? presetTokens[0].ToLowerInvariant() : "standard";
        var domainPacks = presetTokens.Skip(1).Select(t => t.ToLowerInvariant()).ToArray();

        var reporter = new GDPRComplianceReporter(articles);
        var options = new GdprReportOptions(
            Preset: basePreset,
            DomainPacks: domainPacks,
            JudgeMode: judgeMode,
            JudgeModel: judgeModelName);
        GdprComplianceEvidence evidence;
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
        var outputDir = Path.Combine(agentEvalDir, "compliance", "GDPR", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        // Markdown report
        try
        {
            var mdRenderer = new MarkdownRenderer();
            var md = mdRenderer.Render(evidence);
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
            var pdfRenderer = new GDPRPdfRenderer(articles);
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
    /// Resolves a preset specification string into a <see cref="CompositeEval"/>.
    /// Supports additive composition syntax: <c>standard+healthcare</c>,
    /// <c>standard+hr</c>, <c>standard+childrens</c>, and combinations such as
    /// <c>standard+healthcare+hr</c>. The first token must be a base preset
    /// (<c>smoke</c>, <c>standard</c>, or <c>audit</c>/<c>auditgrade</c>).
    /// </summary>
    /// <param name="presetSpec">
    /// The preset specification, e.g. <c>"standard"</c> or <c>"standard+healthcare+hr"</c>.
    /// </param>
    /// <param name="articles">The loaded articles registry.</param>
    /// <param name="scenarioBuilder">The scenario-to-eval builder for domain-pack scenarios.</param>
    /// <returns>The resolved <see cref="CompositeEval"/>.</returns>
    /// <exception cref="ArgumentException">Thrown for unknown base presets or domain packs.</exception>
    internal static CompositeEval ResolvePreset(
        string presetSpec,
        ArticlesRegistry articles,
        ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(scenarioBuilder);

        var tokens = presetSpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw new ArgumentException("Empty preset specification.", nameof(presetSpec));

        var basePreset = tokens[0].ToLowerInvariant() switch
        {
            "smoke"                  => GdprBenchmark.Smoke(articles),
            "standard"               => GdprBenchmark.Standard(articles),
            "audit" or "auditgrade"  => GdprBenchmark.AuditGrade(articles),
            _                        => throw new ArgumentException($"Unknown base preset '{tokens[0]}'.", nameof(presetSpec))
        };

        if (tokens.Length == 1) return basePreset;

        // Accumulate additions from all domain packs, then apply in one pass.
        var allAdditions = new Dictionary<string, List<EvalComponent>>();
        foreach (var pack in tokens.Skip(1))
        {
            var packAdditions = pack.ToLowerInvariant() switch
            {
                "healthcare"  => HealthcareScenarios.Load(scenarioBuilder),
                "hr"          => HRScenarios.Load(scenarioBuilder),
                "childrens"   => ChildrensServiceScenarios.Load(scenarioBuilder),
                _             => throw new ArgumentException($"Unknown domain pack '{pack}'.", nameof(presetSpec))
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
