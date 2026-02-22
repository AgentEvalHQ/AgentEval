// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Configuration options for a red-team scan.
/// </summary>
public class ScanOptions
{
    /// <summary>
    /// Attack types to run. If null or empty, uses all built-in attacks.
    /// </summary>
    public IReadOnlyList<IAttackType>? AttackTypes { get; init; }

    /// <summary>
    /// Scan intensity - controls probe count and difficulty.
    /// Default: Moderate.
    /// </summary>
    public Intensity Intensity { get; init; } = Intensity.Moderate;

    /// <summary>
    /// Maximum probes per attack type. 0 = no limit.
    /// Default: 0 (use all probes for the intensity level).
    /// </summary>
    public int MaxProbesPerAttack { get; init; } = 0;

    /// <summary>
    /// Timeout per probe execution.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan TimeoutPerProbe { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay between probes (for rate limiting).
    /// Default: Zero.
    /// </summary>
    public TimeSpan DelayBetweenProbes { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Number of parallel probe executions.
    /// Default: 1 (sequential for easier debugging).
    /// </summary>
    /// <remarks>
    /// Parallel execution is not yet implemented. This property is reserved for future use.
    /// Currently, probes are always executed sequentially regardless of this value.
    /// </remarks>
    [Obsolete("Parallel execution is not yet implemented. This property is reserved for future use.")]
    public int Parallelism { get; init; } = 1;

    /// <summary>
    /// Whether to stop on first failure.
    /// Default: false (run all probes).
    /// </summary>
    public bool FailFast { get; init; } = false;

    /// <summary>
    /// Whether to include prompt/response in failure details.
    /// Set to false for sensitive content.
    /// Default: true.
    /// </summary>
    public bool IncludeEvidence { get; init; } = true;

    /// <summary>
    /// IChatClient for LLM-judge evaluators (optional).
    /// Required only if using LLMJudgeEvaluator.
    /// </summary>
    public object? JudgeClient { get; init; }

    /// <summary>
    /// Optional callback invoked after each probe completes.
    /// Use this for real-time progress display. 
    /// Alternative to IProgress for simpler API.
    /// </summary>
    public Action<ScanProgress>? OnProgress { get; init; }

    /// <summary>
    /// How often to report progress. 1 = every probe, 5 = every 5th probe.
    /// Default: 1 (report on every probe).
    /// </summary>
    public int ProgressReportInterval { get; init; } = 1;

    /// <summary>
    /// Creates options with default values.
    /// </summary>
    public static ScanOptions Default => new();

    /// <summary>
    /// Creates options for a quick scan.
    /// </summary>
    public static ScanOptions Quick => new()
    {
        Intensity = Intensity.Quick,
        TimeoutPerProbe = TimeSpan.FromSeconds(15)
    };

    /// <summary>
    /// Creates options for a comprehensive scan.
    /// </summary>
    public static ScanOptions Comprehensive => new()
    {
        Intensity = Intensity.Comprehensive,
        TimeoutPerProbe = TimeSpan.FromSeconds(60)
    };
}
