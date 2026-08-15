// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Core.Benchmarks;
using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// Registers the TypedMemEval family with <see cref="BenchmarkFamilyRegistry"/> on assembly load
/// (ADR-017 Convention 3), one preset per vertical.
/// </summary>
/// <remarks>
/// <para>
/// Shape B, like LongMemEval: the runner's semantics are "N questions → a typed outcome vector",
/// which does not map onto the Convention-2 <c>(EvalInput) → EvalResult</c> shape. Use
/// <see cref="TypedMemEvalEvalResultAdapter"/> when a report tree is wanted.
/// </para>
/// <para>
/// One preset per vertical rather than one per size, because the verticals are not difficulty
/// levels of one thing — they measure different mechanisms, and there is deliberately no
/// cross-family composite score to make a "full" preset mean anything.
/// </para>
/// </remarks>
internal static class TypedMemEvalBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "typedmemeval",
            description:
                $"TypedMemEval {TypedMemEvalVerticalDescriptor.CorpusRevision} (AgentEval) — five embedded corpora isolating prospective, episodic, " +
                "arithmetic, working-memory and forgetting behaviour. Not LongMemEval; results are not " +
                "comparable with LongMemEval numbers.",
            defaultCostTier: CostTier.Medium,
            presets: TypedMemEvalVerticals.All
                .Select(descriptor => new BenchmarkPreset(
                    descriptor.Slug,
                    $"{descriptor.DisplayName} {descriptor.Revision} — {descriptor.QuestionCount} embedded questions, no download",
                    CostTier.Medium))
                .ToArray(),
            runnerType: typeof(TypedMemEvalRunner),
            runnerFactory: preset =>
            {
                var client = TypedMemEvalRunnerHostingContext.Current?.ChatClient
                    ?? throw new InvalidOperationException(
                        "TypedMemEval requires an IChatClient for its judge. Populate " +
                        "TypedMemEvalRunnerHostingContext before calling RunnerFactory, or construct " +
                        "TypedMemEvalRunner directly and call RunAsync(agent, vertical).");

                var slug = preset.Trim().ToLowerInvariant();
                var descriptor = TypedMemEvalVerticals.All
                    .FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.Ordinal))
                    ?? throw new ArgumentException(
                        $"Unknown TypedMemEval vertical '{preset}'. Known verticals: " +
                        $"{string.Join(", ", TypedMemEvalVerticals.All.Select(d => d.Slug))}.");

                return new TypedMemEvalRunner(client) { DefaultVertical = descriptor.Vertical };
            },
            evaluateAsync: null,  // Shape B — no Convention-2 adapter.
            docLinkUrl: "https://github.com/AgentEvalHQ/AgentEval/blob/main/docs/benchmarks/typedmemeval/getting-started.md",
            owningAssemblyName: typeof(TypedMemEvalRunner).Assembly.GetName().Name));
    }
}

/// <summary>
/// Ambient context supplying the judge client to the registry's Shape B runner factory.
/// </summary>
/// <remarks>
/// Use a <c>using</c> block to keep the scope clean:
/// <code>
/// using (new TypedMemEvalRunnerHostingContext(client))
/// {
///     var runner = (TypedMemEvalRunner)family.RunnerFactory!("forgetting");
///     await runner.RunAsync(agent);
/// }
/// </code>
/// </remarks>
public sealed class TypedMemEvalRunnerHostingContext : IDisposable
{
    private static readonly AsyncLocal<TypedMemEvalRunnerHostingContext?> s_current = new();

    /// <summary>The active context for the current async flow, or null when none is set.</summary>
    public static TypedMemEvalRunnerHostingContext? Current => s_current.Value;

    /// <summary>The judge client the runner factory will pick up.</summary>
    public IChatClient ChatClient { get; }

    private readonly TypedMemEvalRunnerHostingContext? _previous;

    /// <summary>Pushes a context bound to <paramref name="chatClient"/>.</summary>
    public TypedMemEvalRunnerHostingContext(IChatClient chatClient)
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _previous = s_current.Value;
        s_current.Value = this;
    }

    /// <summary>Restores the previously-set context (supports nesting).</summary>
    public void Dispose() => s_current.Value = _previous;
}
