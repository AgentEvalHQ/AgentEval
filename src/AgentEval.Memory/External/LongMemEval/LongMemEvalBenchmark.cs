// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
/// <b>Presets</b>:
/// <list type="table">
///   <listheader><term>Preset</term><description>Use case</description></listheader>
///   <item>
///     <term><see cref="Subset"/></term>
///     <description>
///       Runs the embedded subset that ships with AgentEval (~30 questions,
///       stratified across the six LongMemEval question types). No external
///       dataset needed — works out of the box. Suitable for CI pipelines
///       and iterative development.
///     </description>
///   </item>
///   <item>
///     <term><see cref="Full"/></term>
///     <description>
///       Runs the complete LongMemEval dataset (~500 questions, single-session
///       variant). The dataset is <b>not</b> bundled with AgentEval — consumers
///       must download it from the official LongMemEval repository and supply
///       the path via either:
///       <list type="bullet">
///         <item>The <c>LONGMEMEVAL_DATASET_PATH</c> environment variable, or</item>
///         <item>Overriding <see cref="AgentEval.Memory.External.Models.ExternalBenchmarkOptions.DatasetPath"/>
///               when calling <c>RunAsync</c>.</item>
///       </list>
///       If neither is set the runner falls back to the embedded subset, which
///       will not reproduce official LongMemEval scores.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>EvaluateAsync deviation</b>: Unlike <c>MitreBenchmark</c> and
/// <c>OwaspBenchmark</c>, this façade does <em>not</em> expose an
/// <c>EvaluateAsync(EvalInput) → EvalResult</c> Convention-2 adapter. LongMemEval
/// has its own well-defined "run all N questions, compute accuracy" semantics and
/// the runner already produces an <c>ExternalBenchmarkResult</c> that the CLI
/// and Mission Control can persist as-is. An <c>EvaluateAsync</c> adapter will
/// be wired in Phase 8 via <c>BenchmarkFamilyRegistry</c> when the canonical
/// adapter shape for external-dataset benchmarks is established.
/// </para>
/// <para>
/// <b>Convention 1 (ADR-017)</b>: this class lives in <c>AgentEval.Benchmarks</c>
/// regardless of which assembly implements it (<c>AgentEval.Memory</c> in this case).
/// </para>
/// </remarks>
public static partial class LongMemEvalBenchmark
{
    // ── Subset preset constants ───────────────────────────────────────────────

    /// <summary>Maximum questions for the Subset preset (stratified across 6 types).</summary>
    private const int SubsetMaxQuestions = 30;

    /// <summary>Random seed for the Subset preset, ensuring reproducible sampling.</summary>
    private const int SubsetRandomSeed = 42;

    // ── Presets ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner"/>
    /// pre-configured for the embedded LongMemEval subset (~30 questions, stratified).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The embedded subset ships with AgentEval — no external dataset download required.
    /// Questions are stratified across the six LongMemEval question types with a fixed
    /// random seed (<c>42</c>) for reproducibility.
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
    /// Call <c>runner.RunAsync(agent, config, options)</c> to execute.
    /// </returns>
    public static AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner Subset(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        return AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner.Create(
            chatClient,
            datasetPath: null);   // null → use embedded subset
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
    /// The full dataset is <b>not</b> bundled with AgentEval. Consumers must download it
    /// from the official LongMemEval repository (ICLR 2025) and supply the path via
    /// the <c>LONGMEMEVAL_DATASET_PATH</c> environment variable, or override
    /// <see cref="ExternalBenchmarkOptions.DatasetPath"/> when calling <c>RunAsync</c>.
    /// </para>
    /// <para>
    /// If neither the environment variable nor a <c>DatasetPath</c> override is set, the
    /// runner falls back to the embedded subset — this will not reproduce official
    /// LongMemEval benchmark scores.
    /// </para>
    /// <para>
    /// Tip: use the <c>LONGMEMEVAL_DATASET_PATH</c> environment variable to avoid
    /// hard-coding paths in application code:
    /// <code>export LONGMEMEVAL_DATASET_PATH=/data/longmemeval/longmemeval-s.json</code>
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
    public static AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner Full(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        // Resolve dataset path from env var; null falls back to embedded subset in the runner.
        var envPath = Environment.GetEnvironmentVariable("LONGMEMEVAL_DATASET_PATH");

        return AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner.Create(
            chatClient,
            datasetPath: envPath);
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
