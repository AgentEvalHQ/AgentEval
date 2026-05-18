// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Benchmarks;

/// <summary>
/// Top-level factory for the LongMemEval academic benchmark (ICLR 2025)
/// preset configurations.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LongMemEvalBenchmark"/> is a thin façade over
/// <see cref="AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner"/>.
/// Each preset constructs and pre-configures a runner instance — it does not
/// execute the benchmark. Call <c>runner.RunAsync(agent, config, options)</c>
/// to actually run it.
/// </para>
/// <para>
/// <b>v0.10.1-beta: real data only.</b> Both presets now run against the real
/// <c>longmemeval_s_cleaned.json</c> dataset (no hand-authored "embedded subset"
/// is bundled — that was misleading because it produced scores that looked
/// paper-comparable but were not). Resolution order, lowest precedence first:
/// <list type="bullet">
///   <item><description>Canonical local default: <c>&lt;workspace-root&gt;/src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json</c></description></item>
///   <item><description><c>LONGMEMEVAL_DATASET_PATH</c> environment variable</description></item>
///   <item><description>Explicit <see cref="ExternalBenchmarkOptions.DatasetPath"/> override on the options record</description></item>
/// </list>
/// When none of these resolve to an existing file, the runner throws
/// <see cref="LongMemEvalDatasetNotFoundException"/> with a message that names the
/// canonical path, the env var, and the Hugging Face download URL.
/// </para>
/// <para>
/// <b>Presets</b>:
/// <list type="table">
///   <listheader><term>Preset</term><description>Use case</description></listheader>
///   <item>
///     <term><see cref="Subset"/></term>
///     <description>
///       Runs a stratified sample of the real ~500-question dataset
///       (default cap: <see cref="SubsetMaxQuestions"/>). Suitable for CI / iterative
///       development. Requires the dataset file on disk (see resolution order above).
///     </description>
///   </item>
///   <item>
///     <term><see cref="Full"/></term>
///     <description>
///       Runs the complete LongMemEval dataset (~500 questions, single-session
///       variant). Explicitly requires the <c>LONGMEMEVAL_DATASET_PATH</c>
///       environment variable to be set (no implicit fallback to the canonical
///       local path) so audit-grade runs are deliberate, not accidental.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>EvaluateAsync deviation</b>: This façade does <em>not</em> expose an
/// <c>EvaluateAsync(EvalInput) → EvalResult</c> Convention-2 adapter because
/// LongMemEval's semantics ("run all N questions, compute accuracy across question
/// types") do not map cleanly onto the single-shot <c>(EvalInput) → EvalResult</c>
/// shape that Convention 2 was designed for. Instead, LongMemEval registers via
/// the registry's <b>Shape B</b> path (per ADR-017 Convention 3, established in
/// Phase 8): callers obtain the runner via <c>BenchmarkFamilyRegistry.TryGet("longmemeval")
/// .RunnerFactory("subset")</c> or directly via <see cref="Subset"/> / <see cref="Full"/>,
/// then invoke <c>runner.RunAsync(agent, config)</c> to get the native
/// <c>ExternalBenchmarkResult</c>. The CLI and Mission Control persist this native
/// result as-is.
/// </para>
/// <para>
/// <b>Convention 1 (ADR-017)</b>: this class lives in <c>AgentEval.Benchmarks</c>
/// regardless of which assembly implements it (<c>AgentEval.Memory</c> in this case).
/// </para>
/// </remarks>
public static partial class LongMemEvalBenchmark
{
    // ── Subset preset constants ───────────────────────────────────────────────

    /// <summary>
    /// Maximum questions for the Subset preset (stratified across 6 types).
    /// v0.10.1-beta: raised from 30 to 50 so the Subset cap reflects the
    /// "representative sample of the real 500" intent. The Smoke override in
    /// <c>08_LongMemEvalBenchmark</c> further caps to 10 for fast CI feedback.
    /// </summary>
    public const int SubsetMaxQuestions = 50;

    /// <summary>Random seed for the Subset preset, ensuring reproducible sampling.</summary>
    private const int SubsetRandomSeed = 42;

    // ── Presets ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner"/>
    /// pre-configured for a stratified sample of the real LongMemEval dataset
    /// (default cap: <see cref="SubsetMaxQuestions"/> questions, stratified across 6 types).
    /// </summary>
    /// <remarks>
    /// <para>
    /// v0.10.1-beta: Subset now reads the real <c>longmemeval_s_cleaned.json</c> on disk
    /// (resolution order documented on the type). The hand-authored embedded subset that
    /// shipped in v0.10.0 was removed because it produced scores that looked paper-comparable
    /// but were not.
    /// </para>
    /// <para>
    /// The <paramref name="chatClient"/> is used for the query turn and the type-specific
    /// LLM judge (2 LLM calls per question). Pass the same <see cref="IChatClient"/>
    /// you use for the agent under test, or a dedicated cheaper model for the judge.
    /// </para>
    /// </remarks>
    /// <param name="chatClient">
    /// The <see cref="IChatClient"/> powering the LLM judge. Required.
    /// </param>
    /// <returns>
    /// A pre-configured <see cref="AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner"/>.
    /// Call <c>runner.RunAsync(agent, config, options)</c> to execute. Throws
    /// <see cref="LongMemEvalDatasetNotFoundException"/> when the dataset cannot be located.
    /// </returns>
    public static AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner Subset(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        // v0.10.1-beta: datasetPath: null → loader resolves via the env var → canonical
        // local path. The loader throws LongMemEvalDatasetNotFoundException with download
        // instructions if neither resolves; the Group-H sample catches that to print a
        // friendly box instead of crashing the menu.
        return AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner.Create(
            chatClient,
            datasetPath: null,
            defaultOptions: SubsetOptions);
    }

    /// <summary>
    /// Returns the pre-configured <see cref="AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner"/>
    /// options that the <see cref="Subset"/> preset passes to <c>RunAsync</c>.
    /// </summary>
    /// <remarks>
    /// Exposed as a separate property so callers can inspect the preset configuration
    /// and override individual fields via <c>with</c> expressions without re-specifying
    /// the full <see cref="ExternalBenchmarkOptions"/> record.
    /// </remarks>
    public static ExternalBenchmarkOptions SubsetOptions => new()
    {
        MaxQuestions = SubsetMaxQuestions,
        StratifiedSampling = true,
        RandomSeed = SubsetRandomSeed,
        PreserveSessionBoundaries = true,
        IncludeTimestamps = true,
        DatasetMode = "Subset"
    };

    /// <summary>
    /// Returns a <see cref="AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner"/>
    /// pre-configured for the complete LongMemEval dataset (~500 questions).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Explicitly requires the <c>LONGMEMEVAL_DATASET_PATH</c> environment variable.
    /// Unlike <see cref="Subset"/>, <see cref="Full"/> does NOT fall through to the
    /// canonical local path — audit-grade runs are deliberate, not accidental.
    /// </para>
    /// <para>
    /// Tip: set the env var via your shell rather than hard-coding paths in code:
    /// <code>export LONGMEMEVAL_DATASET_PATH=/data/longmemeval/longmemeval_s_cleaned.json</code>
    /// </para>
    /// </remarks>
    /// <param name="chatClient">
    /// The <see cref="IChatClient"/> powering the LLM judge. Required.
    /// </param>
    /// <returns>
    /// A pre-configured <see cref="AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner"/>.
    /// Call <c>runner.RunAsync(agent, config, options)</c> with <see cref="FullOptions"/>
    /// (or a custom <see cref="ExternalBenchmarkOptions"/> with your dataset path) to execute.
    /// </returns>
    /// <exception cref="InvalidOperationException">When <c>LONGMEMEVAL_DATASET_PATH</c> is unset or empty.</exception>
    public static AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner Full(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        var envPath = Environment.GetEnvironmentVariable(LongMemEvalDataLoader.DatasetPathEnvVar);
        if (string.IsNullOrWhiteSpace(envPath))
        {
            throw new InvalidOperationException(
                $"LongMemEvalBenchmark.Full() requires the {LongMemEvalDataLoader.DatasetPathEnvVar} environment " +
                $"variable to point at the full LongMemEval dataset JSON file. The dataset is NOT " +
                $"bundled with AgentEval — download {LongMemEvalDataLoader.CanonicalDatasetFileName} (~500 questions) from " +
                $"{LongMemEvalDataLoader.DatasetDownloadUrl} and set the env var to its absolute path. " +
                $"For development / CI use LongMemEvalBenchmark.Subset(chatClient) instead, which reads " +
                $"the dataset from the canonical local path under the workspace root.");
        }

        return AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner.Create(
            chatClient,
            datasetPath: envPath,
            defaultOptions: FullOptions);
    }

    /// <summary>
    /// Returns the pre-configured <see cref="ExternalBenchmarkOptions"/> that the
    /// <see cref="Full"/> preset passes to <c>RunAsync</c>.
    /// </summary>
    public static ExternalBenchmarkOptions FullOptions => new()
    {
        MaxQuestions = null,          // run all questions in the dataset
        StratifiedSampling = true,
        PreserveSessionBoundaries = true,
        IncludeTimestamps = true,
        DatasetMode = "Full"
    };
}
