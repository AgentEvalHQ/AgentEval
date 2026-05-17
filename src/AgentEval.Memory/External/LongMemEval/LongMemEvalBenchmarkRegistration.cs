// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Core.Benchmarks;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Module-initializer hook that registers <see cref="LongMemEvalBenchmark"/> with
/// <see cref="BenchmarkFamilyRegistry"/> on assembly load (Phase 8 / ADR-017 Convention 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape B registration</b> (external-dataset / multi-turn). The registry's
/// <c>RunnerFactory</c> returns a fully-configured <see cref="LongMemEvalBenchmarkRunner"/>
/// pre-baked with the preset's <see cref="ExternalBenchmarkOptions"/>. Callers obtain a runner
/// by name from the registry, then invoke <c>runner.RunAsync(agent, config)</c> — no need
/// to manually pass <c>SubsetOptions</c> / <c>FullOptions</c> any more (closed the Phase-7
/// dead-options footgun).
/// </para>
/// <para>
/// <b>No EvaluateAsync adapter</b>: LongMemEval's "N questions → accuracy" semantics do
/// not map onto the Convention-2 <c>(EvalInput) → EvalResult</c> shape. Mission Control
/// renders the native <c>ExternalBenchmarkResult</c> directly. The registry exposes the
/// family in <c>bench --list</c> regardless — Shape B families are equally first-class.
/// </para>
/// <para>
/// <b>IChatClient resolution</b>: the runner needs an <see cref="IChatClient"/>, not the
/// registry's generic <c>IEvaluator</c>. The factory below pulls the client from
/// <see cref="LongMemEvalRunnerHostingContext"/> — a per-call ambient context that callers
/// (CLI, Mission Control) populate before calling <c>RunnerFactory</c>. Callers that don't
/// populate the context get a clear exception with remediation guidance.
/// </para>
/// </remarks>
internal static class LongMemEvalBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "longmemeval",
            description: "LongMemEval (ICLR 2025) — academic memory benchmark (Subset ships embedded; Full requires download)",
            defaultCostTier: CostTier.Medium,
            presets:
            [
                new("subset", "Embedded 30-question stratified sample — no download, CI-friendly", CostTier.Medium),
                new("full", "Full ~500-question dataset (requires LONGMEMEVAL_DATASET_PATH env var)", CostTier.High),
            ],
            // Shape B only — no CompositeFactory, no EvaluateAsync adapter.
            runnerType: typeof(LongMemEvalBenchmarkRunner),
            runnerFactory: preset =>
            {
                var client = LongMemEvalRunnerHostingContext.Current?.ChatClient
                    ?? throw new InvalidOperationException(
                        "LongMemEvalBenchmark requires an IChatClient. Populate LongMemEvalRunnerHostingContext " +
                        "before calling RunnerFactory, or call the typed factory LongMemEvalBenchmark.Subset(client) " +
                        "/ LongMemEvalBenchmark.Full(client) directly.");
                return preset.Trim().ToLowerInvariant() switch
                {
                    "subset" => LongMemEvalBenchmark.Subset(client),
                    "full"   => LongMemEvalBenchmark.Full(client),
                    _ => throw new ArgumentException(
                        $"Unknown LongMemEval preset '{preset}'. Known presets: subset, full.")
                };
            },
            evaluateAsync: null,  // Shape B — no Convention-2 adapter (see remarks above).
            docLinkUrl: "https://arxiv.org/abs/2410.10813",
            owningAssemblyName: typeof(LongMemEvalBenchmark).Assembly.GetName().Name));
    }
}

/// <summary>
/// Ambient context for the registry's Shape B <c>RunnerFactory</c>. Callers
/// (CLI, Mission Control, tests) populate <see cref="ChatClient"/> before invoking
/// <c>BenchmarkFamilyRegistry.TryGet("longmemeval").RunnerFactory(preset)</c>; the factory
/// reads the value from this context and discards it on disposal.
/// </summary>
/// <remarks>
/// Use a <c>using</c> block to keep the scope clean:
/// <code>
/// using (new LongMemEvalRunnerHostingContext(client))
/// {
///     var runner = (LongMemEvalBenchmarkRunner)family.RunnerFactory!("subset");
///     await runner.RunAsync(agent, config);
/// }
/// </code>
/// </remarks>
public sealed class LongMemEvalRunnerHostingContext : IDisposable
{
    private static readonly AsyncLocal<LongMemEvalRunnerHostingContext?> _current = new();

    /// <summary>The active context for the current async flow, or null when none is set.</summary>
    public static LongMemEvalRunnerHostingContext? Current => _current.Value;

    /// <summary>The <see cref="IChatClient"/> the LongMemEval runner factory will pick up.</summary>
    public IChatClient ChatClient { get; }

    private readonly LongMemEvalRunnerHostingContext? _previous;

    /// <summary>Constructs a context bound to <paramref name="chatClient"/> and pushes it onto the AsyncLocal stack.</summary>
    public LongMemEvalRunnerHostingContext(IChatClient chatClient)
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _previous = _current.Value;
        _current.Value = this;
    }

    /// <summary>Restores the previously-set context (supports nested usage).</summary>
    public void Dispose()
    {
        _current.Value = _previous;
    }
}
