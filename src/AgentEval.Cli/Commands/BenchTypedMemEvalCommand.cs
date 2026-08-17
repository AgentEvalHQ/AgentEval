// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench typedmemeval</c> subcommand.
/// Closes the CLI ↔ <see cref="BenchmarkFamilyRegistry"/> gap where <c>bench --list</c>
/// advertised <c>typedmemeval</c> but no command existed to invoke it.
/// </summary>
/// <remarks>
/// <para>
/// Shape B / runner-style (ADR-017 Convention 3), like <see cref="BenchLongMemEvalCommand"/>:
/// the native <see cref="ExternalBenchmarkResult"/> does not fit the
/// <c>EvaluateAsync(EvalInput) → EvalResult</c> shape, so the native result is written to
/// <c>report-native.json</c> beside the canonical run manifest.
/// </para>
/// <para>
/// REQUIRES Azure OpenAI. One answer call plus one judge call per question, with bounded judge
/// retries increasing the total. All three <c>AZURE_OPENAI_*</c> env vars must be set; there is no
/// stub fallback because the judge round-trip IS the correctness signal.
/// </para>
/// <para>
/// The corpora are embedded in the package, so unlike LongMemEval there is no dataset path, no
/// download step, and deliberately no path knob — the corpus identifier and hash in the result
/// answer "which corpus produced this number".
/// </para>
/// <para>
/// <b>Citation rule.</b> Results are cited as "TypedMemEval-&lt;Vertical&gt; &lt;revision&gt; (AgentEval)", the revision being <see cref="TypedMemEvalVerticalDescriptor.CorpusRevision"/>, and are
/// never summed or averaged with LongMemEval numbers. The console prints the typed outcome vector
/// with its denominators; <see cref="ExternalBenchmarkResult.OverallAccuracy"/> appears only under
/// an explicit compatibility label, because a single percentage is the thing this family exists to
/// replace.
/// </para>
/// </remarks>
public static class BenchTypedMemEvalCommand
{
    /// <summary>Runs the bench typedmemeval command using auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(string vertical, string? subject, string? root, CancellationToken ct = default)
        => RunAsync(vertical, subject, root, chatClientOverride: null, ct);

    /// <summary>Internal overload exposed for tests; allows chat-client injection.</summary>
    internal static async Task<int> RunAsync(
        string vertical,
        string? subject,
        string? root,
        Microsoft.Extensions.AI.IChatClient? chatClientOverride,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            Console.Error.WriteLine("Error: --subject is required.");
            return 1;
        }

        // ── Resolve the vertical ─────────────────────────────────────────────
        // Checked before the workspace so a mistyped vertical answers with the list of verticals
        // rather than with a complaint about the current directory.
        var descriptor = TypedMemEvalVerticals.All.FirstOrDefault(
            d => string.Equals(d.Slug, vertical?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            Console.Error.WriteLine($"Unknown TypedMemEval vertical '{vertical}'. Known: " +
                string.Join(", ", TypedMemEvalVerticals.All.Select(d => d.Slug)));
            return 1;
        }

        // ── Workspace setup ──────────────────────────────────────────────────
        if (root is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(root);
            if (canonical is null) return 1;
            root = canonical;
        }
        var workspaceRoot = root ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
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
            Console.Error.WriteLine(
                $".agenteval/ not found at {agentEvalDir}. Run `agenteval init-workspace` first.");
            return 1;
        }

        // ── Anchor module init so the family is discoverable. ────────────────
        _ = typeof(TypedMemEvalRunner).Assembly;

        if (BenchmarkFamilyRegistry.TryGet("typedmemeval") is null)
        {
            Console.Error.WriteLine("typedmemeval family is not registered. Ensure AgentEval.Memory is referenced.");
            return 1;
        }

        // ── Resolve chat client ──────────────────────────────────────────────
        Microsoft.Extensions.AI.IChatClient chatClient;
        string deployment;
        if (chatClientOverride is not null)
        {
            chatClient = chatClientOverride;
            deployment = "override";
        }
        else
        {
            var (resolved, resolvedDeployment, exitCode) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
            if (resolved is null) return exitCode;
            chatClient = resolved;
            deployment = resolvedDeployment!;
        }

        // ── Build the agent-under-test ───────────────────────────────────────
        var agent = chatClient.AsEvaluableAgent(
            name: subject,
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        // ── Persist setup ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running {descriptor.DisplayName} {descriptor.Revision} for subject '{subject}' against {deployment}...");
        Console.WriteLine($"   Corpus:              {descriptor.CorpusId} ({descriptor.QuestionCount} embedded questions, no download)");
        Console.WriteLine($"   Grounding:           {descriptor.RequiredGrounding}");
        Console.WriteLine("   Scoring:             typed outcome vector — no single percentage");
        Console.WriteLine();

        // ── Run ──────────────────────────────────────────────────────────────
        ExternalBenchmarkResult result;
        try
        {
            result = await new TypedMemEvalRunner(chatClient).RunAsync(agent, descriptor.Vertical, options: null, ct);
        }
        catch (ArgumentException ex) when (ex.ParamName == "agent")
        {
            // The runner refuses this before its first provider call, and a stack trace would bury
            // the one thing the operator has to change about the agent under test.
            Console.Error.WriteLine($"✖ {descriptor.DisplayName} cannot run against agent '{subject}'.");
            Console.Error.WriteLine($"  This vertical runs under {descriptor.RequiredGrounding} grounding: session dates are");
            Console.Error.WriteLine("  delivered as real timestamps and never printed in the conversation, so the agent under");
            Console.Error.WriteLine("  test must implement AgentEval.Core.ITimestampedHistoryInjectableAgent.");
            Console.Error.WriteLine("  There is no text fallback: the dates a fallback would use are the ones this vertical");
            Console.Error.WriteLine("  exists to take away. AgentEval.Core.ChatClientAgentAdapter implements it, if a");
            Console.Error.WriteLine("  reference implementation helps.");
            return ExitCodes.RuntimeError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TypedMemEval run failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var typed = result.TypedOutcomes;
        if (typed is null)
        {
            Console.Error.WriteLine(
                "TypedMemEval run produced no typed outcome vector. Refusing to report the compatibility " +
                "accuracy field in its place — it is not a TypedMemEval score.");
            return ExitCodes.RuntimeError;
        }

        // Inconclusive and Unrun are not measurements, so they are excluded here: a run whose judge
        // never returned a verdict has measured nothing, however many questions it walked through.
        var measured = typed.Outcomes.Correct + typed.Outcomes.Wrong + typed.Outcomes.Abstained +
                       typed.Outcomes.Missed + typed.Outcomes.Premature;

        // ── Persist via output store ─────────────────────────────────────────
        string runId;
        string runDir;
        try
        {
            var manifest = await store.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.Memory.TypedMemEval",
                    EvalProjectPath: "src/AgentEval.Memory/External/TypedMemEval/",
                    Harness: "BenchTypedMemEvalCommand",
                    Seed: result.Options.RandomSeed,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            runId = manifest.Run.RunId;

            // Shape B / Convention 3 (ADR-017): the native result is "N questions → a typed outcome
            // vector", which doesn't fit the per-scenario EvalResult shape. Skip per-scenario writes
            // and persist the native result as report-native.json beside the manifest.

            // The schema's verdict vocabulary is PASS/FAIL/WARN/PENDING and this family publishes no
            // pass threshold — a cut-off would rebuild the single percentage it exists to replace.
            // WARN is the schema's indeterminate value and the only one of the four that does not
            // assert a gate outcome the run never measured; the typed vector below is the result.
            const string summaryVerdict = "WARN";
            var metrics = new Dictionary<string, double>
            {
                // The typed counts are the result. OverallAccuracy is deliberately absent: a
                // percentage key here is exactly what a dashboard would chart as the headline.
                ["typed_n"] = typed.Outcomes.N,
                ["typed_correct"] = typed.Outcomes.Correct,
                ["typed_wrong"] = typed.Outcomes.Wrong,
                ["typed_abstained"] = typed.Outcomes.Abstained,
                ["typed_missed"] = typed.Outcomes.Missed,
                ["typed_premature"] = typed.Outcomes.Premature,
                ["typed_inconclusive"] = typed.Outcomes.Inconclusive,
                ["typed_unrun"] = typed.Outcomes.Unrun,
                ["coverage_observed"] = typed.Coverage.Observed,
                ["attribution_evidence_present"] = typed.Attribution.EvidencePresent,
                ["attribution_evidence_absent"] = typed.Attribution.EvidenceAbsent,
                ["attribution_unobserved"] = typed.Attribution.Unobserved,
                ["attribution_observed_share"] = typed.Attribution.ObservedShare,
                ["judge_failure_policy"] = (int)result.Options.JudgeFailurePolicy,
                ["max_judge_retries"] = result.Options.MaxJudgeRetries,
                ["judge_max_output_tokens"] = result.Options.JudgeMaxOutputTokens,
                ["evidence_capture_mode"] = (int)result.Options.EvidenceCaptureMode,
                ["evidence_top_k"] = result.Options.EvidenceTopK
            };
            if (typed.Coverage.Mean is { } coverageMean) metrics["coverage_mean"] = coverageMean;
            if (typed.Coverage.CalibratedFloorMean is { } floorMean) metrics["coverage_calibrated_floor_mean"] = floorMean;
            if (typed.StaleRecall is { } stale) metrics["forgetting_stale_recall"] = stale;
            if (typed.OverForgetting is { } over) metrics["forgetting_over_forgetting"] = over;
            if (typed.MisattributedForgetting is { } misattributed) metrics["forgetting_misattributed"] = misattributed;
            if (typed.PairConsistency is { } pairs)
            {
                metrics["pairs"] = pairs.Pairs;
                metrics["pairs_both_arms_correct"] = pairs.BothArmsCorrect;
                metrics["pairs_premature_before"] = pairs.PrematureBefore;
                metrics["pairs_missed_after"] = pairs.MissedAfter;
                metrics["pairs_both_arms_same_outcome"] = pairs.TimeBlindPattern;
            }

            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: summaryVerdict,
                Stats: new RunStats(
                    Total: typed.Outcomes.N,
                    Passed: typed.Outcomes.Correct,
                    // Abstention is not a wrong answer, so it never lands in Failed; the schema's
                    // three live buckets keep it beside the judge failures instead.
                    Failed: typed.Outcomes.Wrong + typed.Outcomes.Missed + typed.Outcomes.Premature,
                    Warnings: typed.Outcomes.Abstained + typed.Outcomes.Inconclusive,
                    Skipped: typed.Outcomes.Unrun),
                Metrics: metrics);
            await store.CompleteRunAsync(manifest, summary, ct);

            // Resolve via the store so the report path matches the manifest exactly; hand-building
            // from the RAW subject diverges when FileSystemLayout.Sanitize rewrites the name (BUG-17).
            runDir = store.ResolveRunDirectory(subjectIdentity, runId);
            await File.WriteAllTextAsync(
                Path.Combine(runDir, "report-native.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist TypedMemEval run: {ex.Message}");
            return 1;
        }

        PrintTypedReport(descriptor, result, typed, runId, runDir);

        // GateIndeterminate (11) is the documented bench code for "no verdict"; 6 belongs to
        // `gatekeeper inspect`, and reusing it would make a script keyed on exit codes read a
        // TypedMemEval run that measured nothing as a gatekeeper result.
        return measured > 0 ? ExitCodes.Success : ExitCodes.GateIndeterminate;
    }

    private static void PrintTypedReport(
        TypedMemEvalVerticalDescriptor descriptor,
        ExternalBenchmarkResult result,
        TypedMemEvalReport typed,
        string runId,
        string runDir)
    {
        Console.WriteLine($"   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)");
        Console.WriteLine();
        Console.WriteLine($"   Outcomes (n={typed.Outcomes.N})");
        Console.WriteLine($"     correct        {typed.Outcomes.Correct}");
        Console.WriteLine($"     wrong          {typed.Outcomes.Wrong}");
        Console.WriteLine($"     abstained      {typed.Outcomes.Abstained}");
        Console.WriteLine($"     missed         {typed.Outcomes.Missed}");
        Console.WriteLine($"     premature      {typed.Outcomes.Premature}");
        Console.WriteLine($"     inconclusive   {typed.Outcomes.Inconclusive}   (not a measurement)");
        Console.WriteLine($"     unrun          {typed.Outcomes.Unrun}   (not a measurement)");

        Console.WriteLine();
        Console.WriteLine("   By shape (diagnostic strata — each n is printed because most hold 5–20 questions)");
        var shapeWidth = typed.ByShape.Count > 0 ? typed.ByShape.Keys.Max(k => k.Length) : 0;
        foreach (var (shape, counts) in typed.ByShape)
            Console.WriteLine($"     {shape.PadRight(shapeWidth)}  {FormatCounts(counts)}");

        if (typed.ByDistance is { Count: > 0 } byDistance)
        {
            Console.WriteLine();
            Console.WriteLine("   Outcome × distance (averaging this curve would destroy what the vertical measures)");
            foreach (var (distance, counts) in byDistance)
                Console.WriteLine($"     {distance,3} sessions  {FormatCounts(counts)}");
        }

        if (typed.PairConsistency is { } pairs)
        {
            Console.WriteLine();
            Console.WriteLine($"   Pair consistency (pairs={pairs.Pairs})");
            Console.WriteLine($"     both arms correct        {pairs.BothArmsCorrect}");
            Console.WriteLine($"     premature before-arm     {pairs.PrematureBefore}");
            Console.WriteLine($"     missed after-arm         {pairs.MissedAfter}");
            Console.WriteLine($"     time-blind pattern   {pairs.TimeBlindPattern}");
            Console.WriteLine("       gold flips between the arms by construction, so the excess over both-arms-correct");
            Console.WriteLine("       is the signature of a system that never received the query time.");
        }

        if (typed.StaleRecall is { } stale)
        {
            Console.WriteLine();
            Console.WriteLine("   Forgetting sub-counts");
            Console.WriteLine($"     stale recall               {stale}   (asserted the superseded value as current)");
            Console.WriteLine($"     over-forgetting            {typed.OverForgetting}   (claimed a still-valid fact is no longer known)");
            Console.WriteLine($"     mis-attributed forgetting  {typed.MisattributedForgetting}   (claimed to have forgotten something never stated)");
        }

        Console.WriteLine();
        Console.WriteLine($"   Coverage (K_ref={typed.ReferenceBudgetSessions})");
        Console.WriteLine($"     observed                 {typed.Coverage.Observed} of {typed.Outcomes.N} questions");
        Console.WriteLine($"     mean realised            {FormatCoverage(typed.Coverage.Mean)}");
        Console.WriteLine($"     calibrated floor (BM25)  {FormatCoverage(typed.Coverage.CalibratedFloorMean)}   (below the floor is retrieving worse than word matching)");

        Console.WriteLine();
        Console.WriteLine("   Evidence attribution");
        Console.WriteLine($"     evidence present   {typed.Attribution.EvidencePresent}");
        Console.WriteLine($"     evidence absent    {typed.Attribution.EvidenceAbsent}");
        Console.WriteLine($"     unobserved         {typed.Attribution.Unobserved}");
        Console.WriteLine($"     observed share     {typed.Attribution.ObservedShare:P1}   (level {typed.Attribution.Level}; 0% is no telemetry, not nothing retrieved)");

        if (typed.UnrunReasons.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("   Unrun questions");
            foreach (var (questionId, reason) in typed.UnrunReasons)
                Console.WriteLine($"     {questionId}: {reason}");
        }

        Console.WriteLine();
        Console.WriteLine($"   Compatibility field, NOT a TypedMemEval score: OverallAccuracy = {FormatPercent(result.OverallAccuracy)}");
        Console.WriteLine();
        Console.WriteLine("   Gate:      none — the family publishes no pass threshold, so the run is recorded as");
        Console.WriteLine("              indeterminate (WARN) rather than PASS or FAIL.");
        Console.WriteLine($"   Corpus:    {typed.CorpusId} (sha256 {typed.CorpusSha256[..12]}…)");
        Console.WriteLine($"   Run ID:    {runId}");
        Console.WriteLine($"   Canonical: {runDir}");
        Console.WriteLine($"   Native:    {Path.Combine(runDir, "report-native.json")}");
        Console.WriteLine();
        Console.WriteLine($"Cite as \"{descriptor.DisplayName} {descriptor.Revision} (AgentEval)\" — not comparable with LongMemEval.");
    }

    private static string FormatCounts(TypedMemEvalOutcomeCounts counts)
        => $"n={counts.N,-3} correct {counts.Correct,-3} wrong {counts.Wrong,-3} abstained {counts.Abstained,-3} " +
           $"missed {counts.Missed,-3} premature {counts.Premature,-3} inconclusive {counts.Inconclusive,-3} unrun {counts.Unrun}";

    // Null coverage means "not observed" and must never render as zero.
    private static string FormatCoverage(double? value) => value is { } coverage ? $"{coverage:F3}" : "not observed";

    private static string FormatPercent(double? value) => value is { } score ? $"{score:F1}%" : "n/a";
}
