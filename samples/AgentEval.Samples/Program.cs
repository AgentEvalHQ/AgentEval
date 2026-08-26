// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using AgentEval.Samples.Benchmarks;

namespace AgentEval.Samples;

/// <summary>
/// Main program — two-level interactive menu to browse and run samples by group.
/// </summary>
public static class Program
{
    private static bool _interactive = true;

    /// <summary>Whether samples may prompt for interactive input.</summary>
    internal static bool IsInteractive => _interactive;

    // ──────────────────────────────────────────────────────────
    //  Sample catalogue — one record per group, samples in order
    // ──────────────────────────────────────────────────────────

    private record SampleEntry(string Name, string Description, Func<Task> Run, bool Recommended = false);
    private record SampleGroup(
        char Key,
        string Name,
        string Note,
        IReadOnlyList<SampleEntry> Samples,
        bool Progressive = false,
        IReadOnlyList<(string Name, string Route)>? Paths = null);

    private static readonly IReadOnlyList<SampleGroup> Groups =
    [
        new('A', "Getting Started", "★ mostly no credentials (A5–A7 need Azure)",
        [
            new("Hello World",               "Minimal AgentEval test — TestCase, TestResult, pass/fail",               HelloWorld.RunAsync),
            new("Agent + One Tool",          "Tool tracking and fluent assertions (HaveCalledTool, WithoutError)",      AgentWithOneTool.RunAsync),
            new("Agent + Multiple Tools",    "Tool ordering, BeforeTool / AfterTool, visual timeline",                 AgentWithMultipleTools.RunAsync),
            new("Performance Metrics",       "Latency, cost, TTFT, token budget assertions",                           PerformanceMetrics.RunAsync),
            new("Light Path (MEAI)",        "AgentEval as MEAI IEvaluator — plug into MAF's evaluation pipeline",     LightPathMAFIntegration.RunAsync),
            new("Session Lifecycle",         "MAF AgentSession: create → multi-turn → reset → isolation",             AgentSessionLifecycle.RunAsync),
            new("Advanced MAF Features",     "ChatHistory, middleware, structured output, approval, agent-as-tool",   AdvancedMAFFeatures.RunAsync),
        ]),

        new('B', "Metrics & Quality", "",
        [
            new("Comprehensive RAG",         "Build & evaluate a full RAG system — 8 metrics + IR metrics",           ComprehensiveRAG.RunAsync),
            new("Quality & Safety Metrics",  "Groundedness, Coherence, Fluency — beyond RAG accuracy",               QualitySafetyMetrics.RunAsync),
            new("Judge Calibration",         "Multi-model consensus voting (Median, Mean, Weighted)",                 JudgeCalibration.RunAsync),
            new("Responsible AI",            "Toxicity, bias, misinformation with counterfactual testing",            ResponsibleAI.RunAsync),
            new("Calibrated Evaluator",      "Drop-in IEvaluator with per-criterion majority voting",                CalibratedEvaluatorDemo.RunAsync),
        ]),

        new('C', "Workflows & Conversations", "",
        [
            new("Conversation Evaluation",   "Multi-turn testing with ConversationRunner and fluent builder API",     ConversationEvaluation.RunAsync),
            new("Real MAF Workflow",         "WorkflowBuilder + InProcessExecution: 4-agent pipeline",               WorkflowEvaluationReal.RunAsync),
            new("Workflow + Tools",          "TripPlanner pipeline: 4 agents with tool call tracking",               WorkflowWithTools.RunAsync),
            new("[MessageHandler] Executors", "Source-gen executor pipeline — deterministic, no LLM, AOT-ready",     WorkflowMessageHandlerExecutors.RunAsync),
        ]),

        new('D', "Performance & Statistics", "",
        [
            new("Performance Profiling",     "Real latency: p50 / p90 / p99 percentiles, tool accuracy",             PerformanceProfiling.RunAsync),
            new("Stochastic Evaluation",     "Run N times — assert on pass rate, not single pass/fail",              StochasticEvaluation.RunAsync),
            new("Model Comparison",          "Compare and rank 3 models on quality, speed, cost, reliability",       ModelComparison.RunAsync),
            new("Stochastic + Comparison",   "Stochastic rigor applied to side-by-side model comparison",           CombinedStochasticComparison.RunAsync),
            new("Streaming vs Async",        "TTFT vs throughput — compare streaming and non-streaming modes",       StreamingVsAsyncPerformance.RunAsync),
            new("Reliability Race",          "Two live models, choose 5/10/20/100 runs, Wilson intervals, adherence", ReliabilityRace.RunAsync),
        ]),

        new('E', "Safety & Security", "",
        [
            new("Policy & Safety",           "Enterprise guardrails — NeverCallTool, PII detection, MustConfirmBefore", PolicySafetyEvaluation.RunAsync),
            new("Red Team Basic",            "One-liner security scan — 14 attack types, OWASP probes",              RedTeamBasic.RunAsync),
            new("Red Team Advanced",         "Custom attack pipeline, OWASP compliance, PDF export, baselines",      RedTeamAdvanced.RunAsync),
        ]),

        new('F', "Data & Infrastructure", "",
        [
            new("Snapshot Testing",          "Regression detection — JSON diff, field scrubbing, semantic tolerance", SnapshotTesting.RunAsync),
            new("Datasets & Export",         "Batch evaluation: YAML datasets → JUnit / Markdown / JSON / TRX",      DatasetsAndExport.RunAsync),
            new("Trace Record & Replay",     "Capture executions, save to JSON, replay deterministically",            TraceRecordReplay.RunAsync),
            new("Benchmark System",          "JSONL-loaded tool-accuracy benchmarks (BFCL, GAIA-style)",             BenchmarkSystem.RunAsync),
            new("Dataset Loaders",           "Multi-format auto-detection: JSONL, JSON, YAML, CSV (offline)",        DatasetLoaders.RunAsync),
            new("Extensibility",             "DI registries — custom metrics, exporters, loaders, attacks",          Extensibility.RunAsync),
            new("Cross-Framework",           "Universal IChatClient.AsEvaluableAgent() for any AI provider",         CrossFrameworkEvaluation.RunAsync),
        ]),

        new('G', "Memory Evaluation", "",
        [
            new("Memory Basics",             "Test if agents remember facts — MemoryJudge, fluent assertions",        MemoryBasics.RunAsync),
            new("Memory Benchmark",          "Comprehensive memory scoring — Quick / Standard / Full with grades",    MemoryBenchmarkDemo.RunAsync),
            new("Memory Scenarios",          "ReachBackEvaluator (recall depth), ReducerEvaluator (compression)",    MemoryScenariosDemo.RunAsync),
            new("Memory DI",                 "Production DI wiring — AddAgentEvalMemory(), CanRememberAsync()",      MemoryDI.RunAsync),
            new("Cross-Session Memory",      "Fact persistence across session resets — compare with / without",      MemoryCrossSession.RunAsync),
            new("AIContextProvider Memory",  "MAF-native memory pipeline — AIContextProvider + CrossSessionEval",    MemoryAIContextProvider.RunAsync),
            new("Benchmark Reporting",       "Run benchmarks, save baselines, compare configs, HTML report",        MemoryBenchmarkReporting.RunAsync),
            new("LongMemEval Benchmark",     "Cross-platform memory eval — 120K token haystacks (ICLR 2025, MIT)",  LongMemEvalBenchmarkDemo.RunAsync),
            new("Run Single Benchmark",     "Pick Quick/Standard/Full, run it, save baseline, view report",       RunSingleBenchmark.RunAsync),
            new("LongMemEval Baseline Repro","GPT-4o baseline reproduction — TextBlob mode, paper-matching config", LongMemEvalBaselineRepro.RunAsync),
        ]),

        new('H', "Benchmarks (v0.10.1)", "★ JSON + HTML (+ PDF) outputs for every registered family",
        [
            new("Registry Discovery",        "Walk BenchmarkFamilyRegistry — no Azure required",                     RegistryDiscoveryBenchmark.RunAsync),
            // Item 4 (plan-13 T4.1a): description now matches the actual runtime behaviour
            // — real Azure agent when creds are present, friendly-skip box otherwise.
            new("Performance",               "Real Azure agent if creds present; friendly-skip otherwise — JSON+HTML+PDF",PerformanceBenchmarkSample.RunAsync),
            // Item 5 (plan-13 T4.1a): preset names dropped from the description string —
            // each sample resolves its own preset tier via BenchmarkSampleHelpers.ResolvePreset
            // (env var / CLI flag / interactive prompt). The previously-hardcoded preset list
            // ("smoke / standard / audit-grade") drifted from registry truth; canonical preset
            // names now flow from BenchmarkFamilyRegistry.TryGet({family}).Presets at runtime
            // (see Benchmarks/01_RegistryDiscovery.cs for the discovery walk).
            new("Agentic",                   "Real agent + judge; preset-driven (see H1 Registry Discovery)",       AgenticBenchmarkSample.RunAsync),
            new("GDPR",                      "Per-scenario agent probing; preset-driven; full audit-chain evidence", GdprBenchmarkSample.RunAsync),
            new("EU AI Act",                 "Per-scenario agent probing; preset-driven; full audit-chain evidence", EuAiActBenchmarkSample.RunAsync),
            new("OWASP LLM Top 10",          "Real attack pipeline; preset-driven (see H1 Registry Discovery)",     OwaspBenchmarkSample.RunAsync),
            new("MITRE ATLAS",               "ATLAS technique-level probes; preset-driven (see H1 Registry Discovery)", MitreBenchmarkSample.RunAsync),
            new("NIST AI RMF",               "MEASURE security/privacy/validity evidence; governance Not Applicable",   NistBenchmarkSample.RunAsync),
            new("LongMemEval",               "Real history-injectable agent + judge (ICLR 2025); preset-driven",    LongMemEvalBenchmarkSample.RunAsync),
            new("Memory",                    "Comprehensive memory benchmark — preset-driven",                      MemoryBenchmarkSample.RunAsync),
            new("Foundry Hybrid",            "A Foundry eval INSIDE a composite eval — one run, one report",        FoundryHybridBenchmarkSample.RunAsync),
            new("Foundry Hierarchy",         "Foundry evals as weighted leaves inside a composite benchmark tree",  FoundryHierarchyBenchmarkSample.RunAsync),
            new("Report Browser",            "Open previously-generated JSON / HTML / PDF runs",                    ReportBrowserBenchmark.RunAsync),
        ]),

        new('I', "Observability (Glass Box)", "★ 1-2,4 offline · 3 needs Azure",
        [
            new("Glass Box Full Stack",      "Per-turn tracing + injection pre-gate + PII post-gate + wrapped tool (offline; API tour)",     GlassBoxFullStack.RunAsync),
            new("Auto-Audit (synthetic)",    "Ranked honesty/safety/cost over 3 SCRIPTED endpoints — offline preview of the table shape",     AutoAudit.RunAsync),
            new("Real vs Framework: Agent",  "REAL travel agent — MAF's account vs Glass Box: what the framework HIDES per turn (needs Azure)", RealVsFrameworkTravelAgent.RunAsync),
            new("Real vs Framework: Workflow","Per-EXECUTOR ledger vs chat truth — what a multi-agent workflow HIDES (offline; scripted)",     RealVsFrameworkWorkflow.RunAsync),
        ]),

        new('J', "Gatekeeper (Runtime Protection)", "★ 15-min tour of 6 · M all 29 · P paths",
        Paths:
        [
            ("The 15-minute tour (the recommended six)",
             "00 → 16 → 14 → 04 → 10 → 23 · smallest gate → detection vs authorization → kill chain → calibrated judges → replay & trust → the wire"),
            ("Tool and egress",
             "02 → 17 → 21 → 23 · cross-call policy, result admission, batch ordering, the HTTP wire"),
            ("State and containment",
             "20 → 19 → 22 → 26 · lifecycle, Bulkhead isolation, graph response, identity takeover"),
            ("Construction and dynamic tools",
             "18 → 24 → 27 · hosted coverage honesty, dynamic providers, manifest drift"),
            ("Semantic judgment and approval",
             "03 → 28 → 25 → 04 · human continuation, approval failure modes, Crescendo timing, calibrated judges"),
            ("Operations and assurance",
             "10 → 20 → 22 · provenance/replay, lifecycle evidence, incident projection"),
        ],
        Samples:
        [
            new("00 Hello World",               "★ start here — a red-team check becomes a live gate", GatekeeperHelloWorld.RunAsync, Recommended: true),
            new("01 Enforcement Walkthrough",   "6-scene tour: deny, moat, canary, shadow judge, gate stack", GatekeeperEnforcement.RunAsync),
            new("02 MAF Support Agent",         "read→POST exfiltration chain broken by SequenceGate", GatekeeperMafHarness.RunAsync),
            new("03 Tool Approval",             "routine refunds auto-run; risky ones pause for a human", GatekeeperToolApproval.RunAsync),
            new("04 Beachhead + The Tribunal",  "calibrated injection judge that must EARN inline promotion", GatekeeperBeachhead.RunAsync, Recommended: true),
            new("05 Agent Harness — simple",    "cap an autonomous looping harness agent with RunBudgetGate", GatekeeperAgentHarness.RunAsync),
            new("06 Agent Harness — defended",  "budget + sequence + domain stack around a capable harness", GatekeeperAgentHarnessDefended.RunAsync),
            new("07 Defense in Depth",          "one campaign, three steps — a different layer catches each", GatekeeperDefenseInDepth.RunAsync),
            new("08 Output Panel",              "calibrated run-post judge fan-out + over-refusal valve", GatekeeperOutputPanel.RunAsync),
            new("09 Monetary/Per-Call Budget", "refund-spray injection stopped by monetary + per-call caps", GatekeeperMonetaryAndPerCallBudget.RunAsync),
            new("10 Explainability & Trust",    "why a gate blocked · replay a config change · trust score", GatekeeperExplainabilityAndTrust.RunAsync, Recommended: true),
            new("11A Real A2A Boundary",        "calibrate, then guard a consented remote A2A call", GatekeeperA2ABoundary.RunAsync),
            new("13 Mocked Dangerous Tools",    "SQL/browser/cloud/package contract fixtures, zero effects", GatekeeperMockedDangerousTools.RunAsync),
            new("14 Poisoned Tool Kill Chain",  "poison, exfil, delete, worm — all zeroed with ledger proof", GatekeeperPoisonedToolKillChain.RunAsync, Recommended: true),
            new("15 Harness-Owned Tool Misuse", "runtime-injected capability discovered, its misuse blocked", GatekeeperHarnessOwnedToolMisuse.RunAsync),
            new("16 Jailbreak + Tool Abuse",    "the paraphrase gets through — authorization still holds", GatekeeperJailbreakAndToolAbuse.RunAsync, Recommended: true),
            new("17 Tool Result Admission",     "secrets masked + oversized results cut before context", GatekeeperToolResultAdmission.RunAsync),
            new("18 Hosted Tool Coverage",      "honesty: hosted code execution can't claim gate coverage", GatekeeperHostedToolCoverageBoundary.RunAsync),
            new("19 Bulkhead + Containment",    "contained saturation cannot starve normal work — measured", GatekeeperBulkheadIsolation.RunAsync),
            new("20 Stateful Gate Timeline",    "call/run/session/durable state resets and containment", GatekeeperStatefulTimeline.RunAsync),
            new("21 Same-Batch Exfil Race",     "the sibling-call race SequenceGate honestly cannot stop", GatekeeperSameBatchRace.RunAsync),
            new("22 Security Graph Incident",   "observations → graph → containment; no verdict from gaps", GatekeeperSecurityGraphIncident.RunAsync),
            new("23 HTTP Wire Boundary",        "DNS rebind + redirect escape blocked at the actual wire", GatekeeperHttpWireBoundary.RunAsync, Recommended: true),
            new("24 Dynamic Context Provider",  "dynamic tool inventory refused; provider seam filtered", GatekeeperDynamicContextProviderBoundary.RunAsync),
            new("25 Crescendo Trajectory",      "slow-burn escalation → shadow verdict → quarantine", GatekeeperCrescendoTrajectory.RunAsync),
            new("26 Session Identity Takeover", "reload, poisoning, and concurrent actor-drift defenses", GatekeeperSessionIdentityTakeover.RunAsync),
            new("27 Manifest Provenance Drift", "prompt rug-pulls and MCP drift fail construction closed", GatekeeperManifestProvenanceDrift.RunAsync),
            new("28 Approval Decision Matrix",  "auto/escalate/reject/approve — judge failure escalates", GatekeeperApprovalDecisionMatrix.RunAsync),
            new("29 Result Behavioral Anomaly", "fixed cap vs per-tool learned result-anomaly baseline", GatekeeperToolResultBehavioralAnomaly.RunAsync),
        ], Progressive: true),

        new('K', "Agent Skills", "🔑 real agents (Azure OpenAI) — evaluate & govern MAF's load_skill/read_skill_resource/run_skill_script",
        [
            new("Hello World",               "★ start here — a trivial in-memory skill + ONE assertion (HaveLoadedSkill)", AgentSkillsHelloWorld.RunAsync),
            new("Disclosure Efficiency",      "Free structural metric scoring the load->read->run funnel (order, redundancy)", AgentSkillsDisclosureEfficiency.RunAsync),
            new("Compliance Scanner",         "Static SKILL.md + governance-flag scan (MafSkillScanner) — offline, no model call", AgentSkillsComplianceScanner.RunAsync),
            new("Skill Security Index",       "Compliance + Efficiency joined into one 0-100 score — a missing axis is never faked", AgentSkillsSecurityIndex.RunAsync),
            new("SkillGate",                  "Construction-time drift enforcement — a rug-pull fails agent construction closed", AgentSkillsSkillGate.RunAsync),
        ]),

        new('L', "Copilot Studio", "🔑 real MCS agent — set COPILOTSTUDIO_CONFIG_PATH + COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true",
        [
            new("Hello World",               "★ start here — build the live connector, ONE message, ONE assertion", CopilotStudioHelloWorld.RunAsync),
            new("Live Walkthrough",          "CS-flavored fluent assertions + conversation continuity + Gatekeeper over a live MCS agent", CopilotStudioWalkthrough.RunAsync),
            new("Budget + Red Team",         "A tight --max-credits cap tripping for real, CanResistAsync against a live MCS agent, new-vs-continued conversation identity", CopilotStudioBudgetAndRedTeam.RunAsync),
        ]),
    ];

    // ──────────────────────────────────────────────────────────
    //  Entry point
    // ──────────────────────────────────────────────────────────

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        PrintBanner();

        // Forward `--preset <value>` from argv into AGENTEVAL_SAMPLES_PRESET so
        // BenchmarkSampleHelpers.ResolvePreset (which is called from inside each
        // RunAsync) picks it up without having to thread args through every sample.
        // Documented in samples/AgentEval.Samples/Benchmarks/README.md.
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--preset", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("AGENTEVAL_SAMPLES_PRESET", args[i + 1]);
                break;
            }
        }

        if (!AIConfig.IsConfigured)
            AIConfig.PrintMissingCredentialsWarning();

        // CI/non-interactive: run every offline-capable Gatekeeper sample and exit non-zero on any failure.
        if (args.Any(a => string.Equals(a, "--gatekeeper-offline-suite", StringComparison.OrdinalIgnoreCase)))
        {
            _interactive = false;
            Environment.ExitCode = await RunGatekeeperOfflineSuiteAsync();
            return;
        }

        // Legacy CLI: dotnet run -- <n>  (direct sample number, flattened in group order)
        if (args.Length > 0 && int.TryParse(args[0], out var legacyNumber))
        {
            _interactive = false;
            await RunLegacyNumber(legacyNumber);
            return;
        }

        // Two-level interactive menu
        while (true)
        {
            var group = PromptForGroup();
            if (group is null) break;           // 'Q' → exit

            var showAll = !group.Progressive;
            while (true)
            {
                var visible = VisibleSamples(group, showAll);
                var choice = PromptForSample(group, visible, showAll);

                if (choice == "B") break;
                if (choice == "Q") goto done;
                if (choice == "M") { showAll = !showAll; continue; }
                if (choice == "P") { PrintLearningPaths(group); continue; }
                if (choice == "A") { foreach (var s in visible) await RunEntry(s); continue; }

                // Stable sample IDs win over menu indices ("00", "16", "11A" — the NN prefix shown in each
                // name), so the documented tours can be typed verbatim; bare small numbers stay indices.
                var byId = visible.FirstOrDefault(
                    sample => sample.Name.StartsWith(choice + " ", StringComparison.OrdinalIgnoreCase));
                if (byId is not null)
                {
                    await RunEntry(byId);
                    continue;
                }

                if (int.TryParse(choice, out var idx) && idx >= 1 && idx <= visible.Count)
                    await RunEntry(visible[idx - 1]);
            }
        }

    done:

        Console.WriteLine("\n👋 Goodbye!\n");
    }

    // ──────────────────────────────────────────────────────────
    //  Prompt helpers
    // ──────────────────────────────────────────────────────────

    private static SampleGroup? PromptForGroup()
    {
        while (true)
        {
            PrintGroupMenu();
            var raw = Console.ReadLine()?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(raw) || raw == "Q") return null;

            var group = Groups.FirstOrDefault(g => g.Key.ToString() == raw);
            if (group is not null) return group;

            Console.WriteLine("  Enter a listed group letter or Q to quit.\n");
        }
    }

    // Returns "B" (back), "Q" (quit), "A" (run shown), "M" (toggle), or a sample index.
    private static string PromptForSample(
        SampleGroup group,
        IReadOnlyList<SampleEntry> samples,
        bool showAll)
    {
        while (true)
        {
            PrintSampleMenu(group, samples, showAll);
            var raw = Console.ReadLine()?.Trim().ToUpperInvariant() ?? "";

            if (raw == "" || raw == "B") return "B";
            if (raw == "Q") return "Q";
            if (raw == "A") return "A";
            if (raw == "M" && group.Progressive) return "M";
            if (raw == "P" && group.Paths is { Count: > 0 }) return "P";

            if (int.TryParse(raw, out var idx) && idx >= 1 && idx <= samples.Count)
                return raw;

            if (samples.Any(sample => sample.Name.StartsWith(raw + " ", StringComparison.OrdinalIgnoreCase)))
                return raw;

            var toggle = group.Progressive ? ", M to toggle recommended/all" : "";
            var paths = group.Paths is { Count: > 0 } ? ", P for learning paths" : "";
            Console.WriteLine(
                $"  Enter 1–{samples.Count} (or a sample ID shown in its name), A to run shown samples{toggle}{paths}, B to go back, or Q to quit.\n");
        }
    }

    private static IReadOnlyList<SampleEntry> VisibleSamples(SampleGroup group, bool showAll) =>
        showAll || !group.Progressive
            ? group.Samples
            : group.Samples.Where(sample => sample.Recommended).ToArray();
    // ──────────────────────────────────────────────────────────
    //  Menu rendering
    // ──────────────────────────────────────────────────────────

    private const int MenuWidth = 94;

    private static void MenuBorder(char left, char right) =>
        Console.WriteLine($"  {left}{new string('─', MenuWidth)}{right}");

    private static void MenuRow(string content) =>
        Console.WriteLine($"  │{Fit(content, MenuWidth).PadRight(MenuWidth)}│");

    private static void PrintGroupMenu()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        MenuBorder('┌', '┐');
        MenuRow($"{new string(' ', 38)}SELECT A GROUP");
        MenuBorder('├', '┤');
        Console.ResetColor();

        foreach (var g in Groups)
        {
            var note = string.IsNullOrEmpty(g.Note) ? "" : $"  {g.Note}";
            var count = $"({g.Samples.Count} samples)";
            MenuRow($"  [{g.Key}] {g.Name,-32} {count,-12}{note}");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        MenuBorder('├', '┤');
        MenuRow("  [Q] Quit");
        MenuBorder('└', '┘');
        Console.ResetColor();
        Console.Write("\n  Group: ");
    }

    private static void PrintSampleMenu(
        SampleGroup group,
        IReadOnlyList<SampleEntry> samples,
        bool showAll)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        var view = group.Progressive ? (showAll ? "all" : "recommended") : "all";
        var header = $"{group.Name} — {view} ({samples.Count})";
        MenuBorder('┌', '┐');
        MenuRow($"  {header}");
        MenuBorder('├', '┤');
        Console.ResetColor();

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var marker = sample.Recommended ? "★" : " ";
            MenuRow($" {marker}[{i + 1,2}] {Fit(sample.Name, 28),-28} {Fit(sample.Description, 58)}");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        MenuBorder('├', '┤');
        var actions = "  [A] Run shown   [B] Back   [Q] Quit";
        if (group.Paths is { Count: > 0 }) actions += "   [P] Learning paths";
        MenuRow(actions);
        if (group.Progressive)
        {
            var toggle = showAll ? "[M] Show recommended only" : "[M] Show all samples";
            MenuRow($"  {toggle}");
        }
        MenuBorder('└', '┘');
        Console.ResetColor();
        Console.Write("\n  Sample: ");
    }

    private static void PrintLearningPaths(SampleGroup group)
    {
        if (group.Paths is not { Count: > 0 } paths) return;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Learning paths — numbers are the stable sample IDs (the NN prefix in each menu name):");
        Console.ResetColor();

        foreach (var (name, route) in paths)
        {
            Console.WriteLine($"\n  • {name}");
            Console.WriteLine($"    {route}");
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n  Consent-gated live validation lives in a separate runner: samples/AgentEval.Gatekeeper.Validation");
        Console.WriteLine("  (verbs: calibrate · remote · mock-tools · memory-security).");
        Console.ResetColor();
    }

    private static string Fit(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
    // ──────────────────────────────────────────────────────────
    //  Runner
    // ──────────────────────────────────────────────────────────

    private static async Task RunEntry(SampleEntry entry)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ▶ Running: {entry.Name}");
        Console.ResetColor();
        Console.WriteLine();

        try
        {
            await entry.Run();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ❌ Error: {ex.Message}");
            Console.ResetColor();
        }

        Console.WriteLine("\n  Press any key to continue...");
        if (_interactive) Console.ReadKey(true);
    }

    // ──────────────────────────────────────────────────────────
    //  Gatekeeper offline suite: dotnet run -- --gatekeeper-offline-suite
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs every offline-capable Gatekeeper sample non-interactively so CI executes their
    /// deterministic invariants. Samples 00–10 run their forced-offline oracles; 13–29 are
    /// offline by design. Sample 11A is excluded because it requires a consented live endpoint.
    /// </summary>
    private static async Task<int> RunGatekeeperOfflineSuiteAsync()
    {
        Environment.SetEnvironmentVariable("AGENTEVAL_GATEKEEPER_FORCE_OFFLINE", "true");

        var suite = new (string Id, string Name, Func<Task> Run)[]
        {
            ("00", "Hello World", GatekeeperHelloWorld.RunAsync),
            ("01", "Enforcement Walkthrough", GatekeeperEnforcement.RunAsync),
            ("02", "MAF Support Agent", GatekeeperMafHarness.RunAsync),
            ("03", "Tool Approval", GatekeeperToolApproval.RunAsync),
            ("04", "Beachhead + The Tribunal", GatekeeperBeachhead.RunAsync),
            ("05", "Agent Harness — simple", GatekeeperAgentHarness.RunAsync),
            ("06", "Agent Harness — defended", GatekeeperAgentHarnessDefended.RunAsync),
            ("07", "Defense in Depth", GatekeeperDefenseInDepth.RunAsync),
            ("08", "Output Panel", GatekeeperOutputPanel.RunAsync),
            ("09", "Monetary + Per-Call Budget", GatekeeperMonetaryAndPerCallBudget.RunAsync),
            ("10", "Explainability & Trust", GatekeeperExplainabilityAndTrust.RunAsync),
            ("13", "Mocked Dangerous Tools", GatekeeperMockedDangerousTools.RunAsync),
            ("14", "Poisoned Tool Kill Chain", GatekeeperPoisonedToolKillChain.RunAsync),
            ("15", "Harness-Owned Tool Misuse", GatekeeperHarnessOwnedToolMisuse.RunAsync),
            ("16", "Jailbreak + Tool Abuse", GatekeeperJailbreakAndToolAbuse.RunAsync),
            ("17", "Tool Result Admission", GatekeeperToolResultAdmission.RunAsync),
            ("18", "Hosted Tool Coverage", GatekeeperHostedToolCoverageBoundary.RunAsync),
            ("19", "Bulkhead + Containment", GatekeeperBulkheadIsolation.RunAsync),
            ("20", "Stateful Gate Timeline", GatekeeperStatefulTimeline.RunAsync),
            ("21", "Same-Batch Exfil Race", GatekeeperSameBatchRace.RunAsync),
            ("22", "Security Graph Incident", GatekeeperSecurityGraphIncident.RunAsync),
            ("23", "HTTP Wire Boundary", GatekeeperHttpWireBoundary.RunAsync),
            ("24", "Dynamic Context Provider", GatekeeperDynamicContextProviderBoundary.RunAsync),
            ("25", "Crescendo Trajectory", GatekeeperCrescendoTrajectory.RunAsync),
            ("26", "Session Identity Takeover", GatekeeperSessionIdentityTakeover.RunAsync),
            ("27", "Manifest Provenance Drift", GatekeeperManifestProvenanceDrift.RunAsync),
            ("28", "Approval Decision Matrix", GatekeeperApprovalDecisionMatrix.RunAsync),
            ("29", "Result Behavioral Anomaly", GatekeeperToolResultBehavioralAnomaly.RunAsync),
        };

        var failures = new List<string>();
        foreach (var (id, name, run) in suite)
        {
            Console.WriteLine($"\n════════ Gatekeeper offline suite · {id} {name} ════════");
            try
            {
                await run();
                Console.WriteLine($"  ✔ {id} {name} passed");
            }
            catch (Exception ex)
            {
                failures.Add($"{id} {name}");
                Console.WriteLine($"  ✘ {id} {name} FAILED:\n{ex}");
            }
        }

        Console.WriteLine($"\nGatekeeper offline suite: {suite.Length - failures.Count}/{suite.Length} samples passed.");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  ✘ {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    // ──────────────────────────────────────────────────────────
    //  Legacy: dotnet run -- <n>
    // ──────────────────────────────────────────────────────────

    private static async Task RunLegacyNumber(int n)
    {
        var all = Groups.SelectMany(g => g.Samples).ToList();
        if (n < 1 || n > all.Count)
        {
            Console.WriteLine($"  ❌ Sample {n} not found. Valid range: 1–{all.Count}");
            return;
        }
        await RunEntry(all[n - 1]);
    }

    // ──────────────────────────────────────────────────────────
    //  Banner
    // ──────────────────────────────────────────────────────────

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║     █████╗  ██████╗ ███████╗███╗   ██╗████████╗███████╗██╗   ██╗ █████╗ ██╗   ║
║    ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝██╔════╝██║   ██║██╔══██╗██║   ║
║    ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   █████╗  ██║   ██║███████║██║   ║
║    ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ██╔══╝  ╚██╗ ██╔╝██╔══██║██║   ║
║    ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ███████╗ ╚████╔╝ ██║  ██║███████╗
║    ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   ╚══════╝  ╚═══╝  ╚═╝  ╚═╝╚══════╝
║                                                                               ║
║              The .NET Evaluation Toolkit for AI Agents                        ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
