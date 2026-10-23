// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench longmemeval</c> subcommand (plan-13 T0.6).
/// Closes the CLI ↔ <see cref="BenchmarkFamilyRegistry"/> gap where <c>bench --list</c>
/// advertised <c>longmemeval</c> but no command existed to invoke it.
/// </summary>
/// <remarks>
/// LongMemEval is a Shape B / runner-style benchmark (ADR-017 Convention 3) — its native
/// result (<see cref="ExternalBenchmarkResult"/>) does not fit the
/// <c>EvaluateAsync(EvalInput) → EvalResult</c> shape that <c>bench perf</c> uses, so this
/// command writes the native shape directly to <c>report-native.json</c> alongside the
/// canonical run manifest. No <c>report.md</c> is written; the human-readable summary is
/// the console output.
///
/// <para>
/// Needs a real model, from whichever provider <c>AI_INFERENCE_PROVIDER</c> selects (or
/// auto-detects) via <see cref="AzureChatAgentFactory.TryBuildChatClientFromEnv"/>; the one
/// client both answers and judges. The runner normally makes one answer call plus one judge
/// call per question, with bounded judge retries increasing the total. There is no stub
/// fallback because LongMemEval's correctness signal IS the LLM grader.
/// </para>
///
/// <para>
/// The AuditGrade / Full preset additionally requires <c>LONGMEMEVAL_DATASET_PATH</c>
/// pointing at the full ~500-question dataset. No dataset is bundled; the subset preset
/// can also resolve the canonical local path. When the full-path variable is unset, the
/// command exits cleanly with a clear download-instructions message.
/// </para>
/// </remarks>
public static class BenchLongMemEvalCommand
{
    /// <summary>Runs the bench longmemeval command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(string preset, string subject, string? rootOverride, CancellationToken ct = default,
        DirectoryInfo? aef = null, bool noAef = false, string? subjectVersion = null)
        => RunAsync(preset, subject, rootOverride, chatClientOverride: null, ct, aef, noAef, subjectVersion);

    /// <summary>Runs normal evaluation followed by the diagnostic oracle over the same selected questions.</summary>
    public static Task<int> RunPairedAsync(string preset, string subject, string? rootOverride, CancellationToken ct = default,
        DirectoryInfo? aef = null, bool noAef = false, string? subjectVersion = null) =>
        RunAsync(preset, subject, rootOverride, chatClientOverride: null, ct, aef, noAef, subjectVersion, pairedOracle: true);

    /// <summary>Internal overload exposed for tests; allows chat-client injection.</summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        Microsoft.Extensions.AI.IChatClient? chatClientOverride,
        CancellationToken ct = default,
        DirectoryInfo? aef = null,
        bool noAef = false,
        string? subjectVersion = null,
        Action<QuestionResult>? onQuestionCompleted = null, bool pairedOracle = false,
        Action<LongMemEvalArm, QuestionResult>? onPairedQuestionCompleted = null)
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
            // `init` is the dataset scaffolder; `init-workspace` is what creates .agenteval/.
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init-workspace` first.");
            return 1;
        }

        // ── Anchor module init so the family is discoverable. ────────────────
        _ = typeof(LongMemEvalBenchmark).Assembly;

        var family = BenchmarkFamilyRegistry.TryGet("longmemeval");
        if (family is null)
        {
            Console.Error.WriteLine("longmemeval family is not registered. Ensure AgentEval.Memory is referenced.");
            return 1;
        }
        if (family.Presets.All(p => !string.Equals(p.Name, preset, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Unknown longmemeval preset '{preset}'. Known: " +
                string.Join(", ", family.Presets.Select(p => p.Name)));
            return ExitCodes.UsageError;
        }

        // ── Resolve chat client ──────────────────────────────────────────────
        string? agentEndpoint = null;
        Microsoft.Extensions.AI.IChatClient chatClient;
        string deployment;
        if (chatClientOverride is not null)
        {
            chatClient = chatClientOverride;
            deployment = "override";
        }
        else
        {
            var (resolved, resolvedDeployment, exitCode, envEndpoint) = AzureChatAgentFactory.TryBuildChatClientFromEnvWithEndpoint();
            if (resolved is null) return exitCode;
            chatClient = resolved;
            agentEndpoint = envEndpoint;
            deployment = resolvedDeployment!;
        }

        // ── Resolve runner + options for preset ──────────────────────────────
        LongMemEvalBenchmarkRunner runner;
        ExternalBenchmarkOptions options;
        try
        {
            if (string.Equals(preset, "full", StringComparison.OrdinalIgnoreCase))
            {
                runner = LongMemEvalBenchmark.Full(chatClient);
                options = LongMemEvalBenchmark.FullOptions;
            }
            else
            {
                // Default to "subset"
                runner = LongMemEvalBenchmark.Subset(chatClient);
                options = LongMemEvalBenchmark.SubsetOptions;
            }
        }
        catch (InvalidOperationException ex)
        {
            // Full() throws when LONGMEMEVAL_DATASET_PATH is unset.
            Console.Error.WriteLine($"LongMemEval preset selection failed: {ex.Message}");
            Console.Error.WriteLine($"Set {LongMemEvalDataLoader.DatasetPathEnvVar} or use --preset subset.");
            return 1;
        }

        // ── Build the agent-under-test ───────────────────────────────────────
        var agent = chatClient.AsEvaluableAgent(
            name: subject,
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var benchmarkConfig = new AgentBenchmarkConfig
        {
            AgentName = subject,
            ModelId = deployment,
            ReducerStrategy = "None",
            MemoryProvider = "InMemoryChatHistory (history injection)"
        };

        if (pairedOracle)
            return await RunPairedCoreAsync(runner, agent, benchmarkConfig, chatClient, options, preset, subject,
                subjectVersion, agentEvalDir, aef, noAef, chatClientOverride is null, agentEndpoint, ct, onPairedQuestionCompleted);

        // ── Persist setup ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running LongMemEval ({preset}) for subject '{subject}' against {deployment}...");
        Console.WriteLine($"   Max questions:       {(options.MaxQuestions?.ToString() ?? "all in dataset")}");
        Console.WriteLine($"   Scoring:             binary (0/1) — official LongMemEval methodology");
        Console.WriteLine();

        // ── Run ──────────────────────────────────────────────────────────────
        ExternalBenchmarkResult? result = null;
        byte[]? nativeReportJson = null;
        AgentEval.Results.Suites.SuiteDefinitionManifest? suiteDefinition = null;
        var completed = new List<QuestionResult>();
        var startedAt = DateTimeOffset.UtcNow;
        string? storeRunId = null;
        AgentEval.Results.Adapters.Native.AefStoreV1Link? StoreLink() => storeRunId is null ? null : new() { RunId = storeRunId };
        bool WriteEvidence(string? reason = null, int? exitCode = null)
        {
            if (noAef) return false;
            var observed = result ?? new ExternalBenchmarkResult
            {
                BenchmarkId = "longmemeval", BenchmarkName = "Interrupted LongMemEval", Options = options,
                OverallAccuracy = null, TaskAveragedAccuracy = null, PerTypeResults = [],
                QuestionResults = completed.ToArray(), Duration = DateTimeOffset.UtcNow - startedAt,
            };
            return BenchAef.WriteResult(aef, agentEvalDir, "longmemeval", preset, subject, subjectVersion,
                chatClientOverride is null ? AgentEval.Results.Writing.AefTargetMode.Live : AgentEval.Results.Writing.AefTargetMode.Mocked,
                BenchAef.QuestionTree(observed, "longmemeval", preset,
                    result is null ? "INCONCLUSIVE" : LongMemEvalVerdict(result.OverallAccuracy, result.QuestionResults.Count - result.ScoredQuestions),
                    chatClientOverride is null ? deployment : null), startedAt, DateTimeOffset.UtcNow,
                exitCode: exitCode, abortReason: reason, emptyResults: result is null && completed.Count == 0, storeV1: StoreLink(), endpoint: agentEndpoint,
                definition: suiteDefinition, nativeReportJson: nativeReportJson);
        }
        try
        {
            result = await runner.RunWithProgressAsync(agent, benchmarkConfig, options, question =>
            {
                completed.Add(question);
                onQuestionCompleted?.Invoke(question);
            }, onPrepared: noAef ? null : plan => suiteDefinition = AgentEval.Cli.Infrastructure.ExternalMemorySuiteDefinition.Create(
                "longmemeval", preset, plan, chatClientOverride is null ? deployment : null,
                chatClientOverride is null ? AgentEval.Cli.Infrastructure.ProviderChatClientFactory.Settings.ProviderTag : "override"), ct: ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (LongMemEvalDatasetNotFoundException ex)
        {
            Console.Error.WriteLine($"✖ LongMemEval dataset not found: {ex.Message}");
            Console.Error.WriteLine($"  Download longmemeval_s_cleaned.json and place at the canonical location or set {LongMemEvalDataLoader.DatasetPathEnvVar}.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LongMemEval run failed: {ex.GetType().Name}: {ex.Message}");
            return WriteEvidence(ct.IsCancellationRequested ? "cancelled" : "execution-error") ? ExitCodes.RuntimeError : 1;
        }

        // ── Persist via output store ─────────────────────────────────────────
        string runId;
        try
        {
            nativeReportJson = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
            var manifest = await store.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.Memory.LongMemEval",
                    EvalProjectPath: "src/AgentEval.Memory/External/LongMemEval/",
                    Harness: "BenchLongMemEvalCommand",
                    Seed: options.RandomSeed,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            runId = manifest.Run.RunId;
            storeRunId = runId;

            // Shape B / Convention 3 (ADR-017): LongMemEval's native ExternalBenchmarkResult
            // is "N questions → accuracy", which doesn't fit the per-scenario EvalResult shape.
            // Skip per-scenario writes; persist the native result as report-native.json beside
            // the manifest below. The summary still captures pass/fail totals.

            // A pass on part of the questions is a warn (#203 review, B6c-9): accuracy is over the SCORED questions only,
            // and the default RetryThenInconclusive policy leaves judge failures and agent errors unscored, so 1 scored
            // question and 499 inconclusive passed. A fail stays a fail.
            var verdict = LongMemEvalVerdict(result.OverallAccuracy, unscored: result.QuestionResults.Count - result.ScoredQuestions);
            var metrics = new Dictionary<string, double>();
            // The canonical RunSummary schema uses WARN for indeterminate runs.
            // Keep the more precise INCONCLUSIVE label in LongMemEval's console/native surfaces.
            var summaryVerdict = result.OverallAccuracy.HasValue ? verdict : "WARN";
            metrics["judge_failure_policy"] = (int)options.JudgeFailurePolicy;
            metrics["max_judge_retries"] = options.MaxJudgeRetries;
            metrics["judge_temperature_configured"] = options.JudgeTemperature.HasValue ? 1 : 0;
            metrics["judge_max_output_tokens"] = options.JudgeMaxOutputTokens;
            metrics["judge_evidence_mode"] = (int)options.JudgeEvidenceMode;
            metrics["evidence_capture_mode"] = (int)options.EvidenceCaptureMode;
            metrics["evidence_top_k"] = options.EvidenceTopK;
            if (options.JudgeTemperature is { } temperature) metrics["judge_temperature"] = temperature;
            if (result.OverallAccuracy is { } overall) metrics["overall_accuracy"] = overall;
            if (result.TaskAveragedAccuracy is { } taskAverage) metrics["task_averaged_accuracy"] = taskAverage;
            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: summaryVerdict,
                Stats: new RunStats(
                    Total: result.QuestionResults.Count,
                    Passed: result.QuestionResults.Count(q => q.Correct is true),
                    Failed: result.QuestionResults.Count(q => q.Correct is false),
                    Warnings: result.QuestionResults.Count(q => !q.Correct.HasValue)),
                Metrics: metrics);
            await store.CompleteRunAsync(manifest, summary, ct);

            // Write the native ExternalBenchmarkResult JSON alongside the manifest.
            // Resolve via the store so the report path matches the manifest exactly; hand-building
            // from the RAW subject diverges when FileSystemLayout.Sanitize rewrites the name (BUG-17).
            var runDir = store.ResolveRunDirectory(subjectIdentity, runId);
            await File.WriteAllBytesAsync(
                Path.Combine(runDir, "report-native.json"),
                nativeReportJson);

            Console.WriteLine();
            Console.WriteLine($"   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)");
            Console.WriteLine($"   Overall accuracy:        {FormatPercent(result.OverallAccuracy)}  ({result.CorrectQuestions}/{result.ScoredQuestions} scored, {result.SelectedQuestions} selected)");
            Console.WriteLine($"   Task-averaged accuracy:  {FormatPercent(result.TaskAveragedAccuracy)}  ({result.ScoredTypeCount} scored types)");
            Console.WriteLine($"   Inconclusive judgments:  {result.InconclusiveQuestions}");
            Console.WriteLine($"   Agent failures:          {result.AgentFailureQuestions}");
            Console.WriteLine($"   Verdict:                 {verdict}" +
                (verdict == "WARN" ? $" — {result.QuestionResults.Count - result.ScoredQuestions} of {result.QuestionResults.Count} questions were not scored, so the pass is a warn" : ""));
            Console.WriteLine();
            Console.WriteLine($"   Run ID: {runId}");
            Console.WriteLine($"   Canonical: {runDir}");
            Console.WriteLine($"   Native:    {Path.Combine(runDir, "report-native.json")}");
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist LongMemEval run: {ex.Message}");
            return WriteEvidence(ct.IsCancellationRequested ? "cancelled" : "execution-error") ? ExitCodes.RuntimeError : 1;
        }

        var finalExit = VerdictExit(LongMemEvalVerdict(result.OverallAccuracy, result.QuestionResults.Count - result.ScoredQuestions));
        // AEF: a case per question. The environment's provider is live; a client passed in (tests) is a stand-in.
        if (WriteEvidence(exitCode: finalExit))
            return ExitCodes.RuntimeError;   // an explicit --aef could not be written

        return finalExit;
    }

    /// <summary>
    /// The run's verdict: INCONCLUSIVE when nothing was scored; PASS only when accuracy over the scored questions meets 50%
    /// AND every question was scored — otherwise that pass is a WARN (B6c-9); FAIL below 50%.
    /// </summary>
    internal static string LongMemEvalVerdict(double? overallAccuracy, int unscored) => overallAccuracy switch
    {
        null => "INCONCLUSIVE",
        >= 50.0 when unscored > 0 => "WARN",
        >= 50.0 => "PASS",
        _ => "FAIL",
    };

    internal static int VerdictExit(string verdict) => verdict switch
    {
        "INCONCLUSIVE" => ExitCodes.GateInconclusive,
        "WARN" => ExitCodes.GateWarning,
        "PASS" => ExitCodes.Success,
        _ => ExitCodes.GateFailed,
    };

    private static async Task<int> RunPairedCoreAsync(LongMemEvalBenchmarkRunner runner, IEvaluableAgent agent,
        AgentBenchmarkConfig config, Microsoft.Extensions.AI.IChatClient client, ExternalBenchmarkOptions options,
        string preset, string subject, string? subjectVersion, string agentEvalDir, DirectoryInfo? aef, bool noAef,
        bool live, string? endpoint, CancellationToken ct, Action<LongMemEvalArm, QuestionResult>? onQuestion)
    {
        Console.WriteLine($"Running paired LongMemEval ({preset}) for subject '{subject}': normal, then gold-only oracle.");
        var oracleConfig = new AgentBenchmarkConfig
        {
            AgentName = "LongMemEval oracle reader", ModelId = config.ModelId, ModelVersion = config.ModelVersion,
            Temperature = config.Temperature, MaxTokens = config.MaxTokens,
        };
        try
        {
            LongMemEvalPairedResult paired;
            if (noAef)
                paired = await runner.RunPairedWithProgressAsync(agent, config, client, oracleConfig, options,
                    new() { OnQuestionCompleted = onQuestion }, ct);
            else
            {
                var session = new AgentEval.Cli.Infrastructure.PairedMemoryAefSession(
                    Path.Combine(aef?.FullName ?? Path.Combine(agentEvalDir, "aef"), "runs"), preset,
                    new AgentEval.Results.Adapters.Native.AefCompositeRunOptions
                    {
                        SubjectRef = AgentEval.Results.Adapters.Native.AefEvalWriter.Ref("agent", subject),
                        SubjectVersion = subjectVersion, Endpoint = endpoint,
                        TargetMode = live ? AgentEval.Results.Writing.AefTargetMode.Live : AgentEval.Results.Writing.AefTargetMode.Mocked,
                        SuiteRef = "bench:longmemeval/" + preset, Lane = "longmemeval",
                        Seal = AefCliOptions.Seal, Signer = AefCliOptions.Signer, ContentCapture = AefCliOptions.ContentCapture,
                        Cli = AefCliOptions.Provenance ?? new() { Verb = "bench longmemeval" },
                        Config = new AgentEval.Results.Writing.AefRunConfig
                        {
                            Settings = new System.Text.Json.Nodes.JsonObject { ["preset"] = preset, ["pairedOracle"] = true, ["runs"] = 1 },
                        },
                    }, live ? AgentEval.Cli.Infrastructure.ProviderChatClientFactory.Settings.ProviderTag : "override");
                paired = await session.RunAsync(runner, agent, config, client, oracleConfig, options, ct, onQuestion,
                    artifact => AefCliOptions.ReportSaved(artifact.Directory, artifact.RunHash, subjectVersion));
            }
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"   Pair:             {paired.Plan!.PairId}");
            Console.WriteLine($"   Normal accuracy:  {FormatPercent(paired.Normal.OverallAccuracy)}");
            Console.WriteLine($"   Oracle accuracy:  {FormatPercent(paired.Oracle.OverallAccuracy)} (diagnostic only)");
            Console.WriteLine($"   Oracle gap:       {(paired.OracleGapPercentagePoints is { } gap ? gap.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " percentage points" : "n/a")}");
            return VerdictExit(LongMemEvalVerdict(paired.Normal.OverallAccuracy,
                paired.Normal.QuestionResults.Count - paired.Normal.ScoredQuestions));
        }
        catch (AgentEval.Cli.Infrastructure.PairedMemoryEvidenceException ex)
        {
            Console.Error.WriteLine($"Paired evidence failed: {ex.Message}");
            return ExitCodes.RuntimeError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Paired LongMemEval failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static string FormatPercent(double? value) => value is { } score ? $"{score:F1}%" : "n/a";
}
