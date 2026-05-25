// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;

namespace AgentEval.Evals.Agentic.Calibration;

/// <summary>A single hand-labeled calibration entry used to evaluate judge accuracy.</summary>
public sealed record CalibrationEntry(
    string ScenarioId,
    string EvaluatorKey,
    string Input,
    string AgentResponse,
    string ExpectedVerdict,
    double ExpectedScoreMin,
    double ExpectedScoreMax,
    string Rationale);

/// <summary>A named collection of calibration entries for a single agentic category.</summary>
public sealed record CalibrationDataset(string CategoryKey, IReadOnlyList<CalibrationEntry> Entries);

/// <summary>
/// Loads <see cref="CalibrationDataset"/> instances from JSONL streams or
/// embedded assembly resources. Groups entries by category derived from the
/// evaluator key: keys starting with <c>task_</c> or <c>intent_</c> map to
/// <c>system</c>; keys starting with <c>tool_</c> map to <c>process</c>.
/// </summary>
public sealed class CalibrationDatasetLoader
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads a <see cref="CalibrationDataset"/> from a JSONL stream.
    /// Each non-blank line must be a JSON object matching <see cref="CalibrationEntry"/>.
    /// </summary>
    /// <param name="categoryKey">Logical key identifying the category (e.g. <c>system</c> or <c>process</c>).</param>
    /// <param name="jsonl">Readable stream containing JSONL content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed <see cref="CalibrationDataset"/>.</returns>
    public async Task<CalibrationDataset> LoadAsync(string categoryKey, Stream jsonl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryKey);
        ArgumentNullException.ThrowIfNull(jsonl);

        var entries = new List<CalibrationEntry>();
        using var reader = new StreamReader(jsonl);
        string? line;
        int lineNumber = 0;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<CalibrationEntry>(line, s_jsonOpts)
                    ?? throw new InvalidOperationException($"line {lineNumber} parsed to null");
                entries.Add(entry);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Calibration {categoryKey} line {lineNumber} malformed: {ex.Message}", ex);
            }
        }
        return new CalibrationDataset(categoryKey, entries);
    }

    /// <summary>
    /// Loads all calibration datasets from JSONL files embedded in <paramref name="assembly"/>,
    /// grouped by agentic category. Only resources whose name contains
    /// <c>.Agentic.Calibration.Golden.</c> and ends with <c>.jsonl</c> are considered.
    /// </summary>
    /// <remarks>
    /// The legacy <c>.AgenticBenchmark.Golden.</c> prefix (pre-v0.9.0 namespace) is no longer
    /// recognised — all goldens migrated to the <c>Agentic.Calibration.Golden</c> resource
    /// path in the v0.9.0 namespace-move (ADR-017, plan-13 T4.1d item 26).
    /// </remarks>
    /// <param name="assembly">The assembly that contains the embedded JSONL resources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All loaded datasets, one per derived category.</returns>
    public async Task<IReadOnlyList<CalibrationDataset>> LoadAllFromAssemblyAsync(
        Assembly assembly, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
                        n.Contains(".Agentic.Calibration.Golden.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Load every file and accumulate all entries, then group by implied category.
        var allEntries = new List<CalibrationEntry>();
        foreach (var resourceName in resourceNames)
        {
            // Extract category key from the filename segment, e.g. "...golden-20-system.jsonl" → "system"
            var parts = resourceName.Split('.');
            var fileName = parts.Length >= 2 ? parts[^2] : resourceName;
            var categoryKey = fileName.StartsWith("golden-", StringComparison.OrdinalIgnoreCase)
                ? DeriveCategory(fileName[("golden-".Length)..])
                : DeriveCategory(fileName);

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            var dataset = await LoadAsync(categoryKey, stream, ct);
            allEntries.AddRange(dataset.Entries);
        }

        // Group by implied category derived from each entry's evaluator key.
        return allEntries
            .GroupBy(e => DeriveCategory(e.EvaluatorKey))
            .Select(g => new CalibrationDataset(g.Key, g.ToList()))
            .ToList();
    }

    /// <summary>
    /// Derives the agentic category from a file name suffix or evaluator key.
    /// <para>
    /// <b>Routing rules</b> (T1.3, plan-13 §[14]):
    /// </para>
    /// <list type="bullet">
    ///   <item><c>task_*</c> / <c>intent_*</c> → <c>system</c></item>
    ///   <item><c>tool_*</c> → <c>process</c></item>
    ///   <item>UX evaluators (verbosity / tone / refusal) → <c>ux</c></item>
    ///   <item>Adversarial-resistance evaluators (direct_injection / persona_attack /
    ///         jailbreak_resistance / prompt_leak / escalation_resistance) → <c>adversarial</c></item>
    ///   <item>Reasoning evaluators (reasoning_correctness / intermediate_step_hallucination /
    ///         plan_formulation_quality / goal_decomposition_quality / self_correction_quality)
    ///         → <c>reasoning</c></item>
    ///   <item>Meta-calibration evaluators (confidence_calibration / uncertainty_acknowledgment)
    ///         → <c>calibration</c></item>
    ///   <item>Memory / multi-turn evaluators (memory_recall_accuracy / turn_coherence /
    ///         goal_tracking / clarification_appropriateness / long_conversation_coherence)
    ///         → <c>memory</c></item>
    ///   <item>RAG-quality evaluators (groundedness / relevance / coherence / fluency /
    ///         similarity / response_completeness / f1_score) → <c>quality</c></item>
    ///   <item>Safety / content-classifier evaluators (hate_unfairness / self_harm /
    ///         sexual / violence / code_vulnerability / ungrounded_attributes /
    ///         sensitive_data_leakage / protected_material / unsafe_tool_use /
    ///         indirect_attack / system_prompt_leakage / prohibited_actions) → <c>safety</c></item>
    ///   <item>Numeric suffixes like <c>20-system</c> → <c>system</c>;
    ///         <c>ux</c> / <c>reasoning</c> / <c>memory-multiturn</c> / <c>quality</c> /
    ///         <c>adversarial-direct</c> / <c>confidence-calibration</c> filename roots map to
    ///         their respective categories by suffix scan.</item>
    ///   <item>Unknown keys fall back to <c>unknown</c> (preserved as a safety net so that
    ///         newly-added evaluators are visible in the SKIP message rather than silently
    ///         miscategorised).</item>
    /// </list>
    /// </summary>
    internal static string DeriveCategory(string keyOrSuffix)
    {
        if (string.IsNullOrWhiteSpace(keyOrSuffix)) return "unknown";

        var lower = keyOrSuffix.ToLowerInvariant();

        // Filename-style suffix: "20-system" / "20-process" / "20-quality" / "ux" / etc.
        // These match the suffix of golden filenames like "golden-20-quality" → "quality"
        // and "golden-memory-multiturn" → "memory" (via the explicit-key path below).
        if (lower.EndsWith("system", StringComparison.Ordinal)) return "system";
        if (lower.EndsWith("process", StringComparison.Ordinal)) return "process";
        if (lower.EndsWith("quality", StringComparison.Ordinal)) return "quality";
        if (lower.EndsWith("ux", StringComparison.Ordinal)) return "ux";
        if (lower.EndsWith("reasoning", StringComparison.Ordinal)) return "reasoning";
        if (lower.EndsWith("calibration", StringComparison.Ordinal)) return "calibration";
        if (lower.EndsWith("multiturn", StringComparison.Ordinal)) return "memory";
        if (lower.EndsWith("memory", StringComparison.Ordinal)) return "memory";
        if (lower.EndsWith("adversarial-direct", StringComparison.Ordinal)) return "adversarial";
        if (lower.EndsWith("safety", StringComparison.Ordinal)) return "safety";

        // ── Evaluator-key prefixes / exact matches ───────────────────────────
        // Original mappings (preserved for backwards compatibility):
        if (lower.StartsWith("task_", StringComparison.Ordinal) ||
            lower.StartsWith("intent_", StringComparison.Ordinal))
            return "system";

        if (lower.StartsWith("tool_", StringComparison.Ordinal))
            return "process";

        // T1.3 extensions: route the 38 previously-"unknown" evaluator keys into
        // their proper buckets. Per the v1.1 audit (plan-13 Part 4), 25 of these
        // already had goldens on disk but were silently dropped by the runner;
        // the other 13 receive newly-authored goldens in this same task.
        switch (lower)
        {
            // ── UX (3) ───────────────────────────────────────────────────────
            case "verbosity_appropriateness":
            case "tone_appropriateness":
            case "refusal_quality":
                return "ux";

            // ── Adversarial-resistance (5) ───────────────────────────────────
            // Note: system_prompt_leakage and indirect_attack are Safety-category
            // evaluators by their `Category` property; here we route them to
            // "safety" since they ship through the IContentSafety / safety pipeline,
            // not the adversarial-prompt-resistance pattern library.
            case "direct_injection":
            case "persona_attack":
            case "jailbreak_resistance":
            case "prompt_leak":
            case "escalation_resistance":
                return "adversarial";

            // ── Reasoning (5) ────────────────────────────────────────────────
            case "reasoning_correctness":
            case "intermediate_step_hallucination":
            case "plan_formulation_quality":
            case "goal_decomposition_quality":
            case "self_correction_quality":
                return "reasoning";

            // ── Calibration meta (2) ─────────────────────────────────────────
            // Plan flags these as "likely noisy" — the override map keeps headroom
            // for relaxed thresholds if real-LLM measurement confirms.
            case "confidence_calibration":
            case "uncertainty_acknowledgment":
                return "calibration";

            // ── Memory / multi-turn (5) ──────────────────────────────────────
            case "memory_recall_accuracy":
            case "turn_coherence":
            case "goal_tracking":
            case "clarification_appropriateness":
            case "long_conversation_coherence":
                return "memory";

            // ── Quality / RAG (7) ────────────────────────────────────────────
            // Note: f1_score is dispatched via F1ScoreEval (pure-code) — its
            // calibration entries verify deterministic output is in the expected
            // band. Per playbook, f1_score is flagged as noisy at small n.
            case "groundedness":
            case "relevance":
            case "coherence":
            case "fluency":
            case "similarity":
            case "response_completeness":
            case "f1_score":
                return "quality";

            // ── Safety / content-classifier (12) ─────────────────────────────
            // All Safety/* evaluators land here. system_prompt_leakage and
            // indirect_attack live in Safety/ (not Adversarial/), so they
            // route through this branch — the adversarial branch above is
            // reserved for prompt-injection-resistance keys.
            case "hate_unfairness":
            case "self_harm":
            case "sexual":
            case "violence":
            case "code_vulnerability":
            case "ungrounded_attributes":
            case "sensitive_data_leakage":
            case "protected_material":
            case "unsafe_tool_use":
            case "indirect_attack":
            case "system_prompt_leakage":
            case "prohibited_actions":
                return "safety";

            default:
                return "unknown";
        }
    }
}
