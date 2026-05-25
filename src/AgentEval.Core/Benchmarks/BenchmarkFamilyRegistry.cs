// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Core.Benchmarks;

// ─────────────────────────────────────────────────────────────────────────────
// Cost tier — exposed at the family level so CLI / Mission Control surfaces
// can warn operators before they kick off an expensive run.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Coarse cost tier for a benchmark family. Used by CLI <c>bench --list</c> and
/// Mission Control rendering to warn operators before kicking off expensive runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier semantics</b> (per ADR-017 Convention 3, established in Phase 8):
/// </para>
/// <list type="bullet">
///   <item><see cref="Free"/> — pure-code evaluators, no LLM tokens consumed (e.g. telemetry, judge-quality).</item>
///   <item><see cref="Low"/> — handful of LLM calls (smoke presets, single-question diagnostics).</item>
///   <item><see cref="Medium"/> — tens to a few hundred LLM calls (standard presets, agentic execution).</item>
///   <item><see cref="High"/> — thousands of LLM calls and/or hours of wall-clock time (audit-grade presets,
///         full external-dataset runs like <c>LongMemEvalBenchmark.Full</c>).</item>
/// </list>
/// </remarks>
public enum CostTier
{
    /// <summary>Pure-code; no LLM cost.</summary>
    Free,

    /// <summary>A handful of LLM calls (CI-friendly smoke presets).</summary>
    Low,

    /// <summary>Tens to a few hundred LLM calls (standard presets).</summary>
    Medium,

    /// <summary>Thousands of LLM calls and/or hours of wall-clock time (audit-grade / full dataset).</summary>
    High,
}

// ─────────────────────────────────────────────────────────────────────────────
// Preset metadata — exposed by `bench {family} --help` and Mission Control.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Metadata for a single preset of a benchmark family. Used by CLI help and Mission
/// Control rendering. Phase 8 (ADR-017 Convention 3) — created together with the
/// <see cref="BenchmarkFamilyRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TODO (plan-13 T4.1b item 22 / lastreview/16 §8)</b>: add a
/// <c>RequiresProgrammaticConstruction</c> bool that surfaces presets which
/// throw from their registry-driven <see cref="BenchmarkFamily.CompositeFactory"/>
/// because they need programmatic config the registry can't supply (policy
/// resolvers, domain-pack composition, etc.). Today such presets register the
/// flag-equivalent behaviour ad-hoc by throwing from the factory; surfacing it
/// at metadata level lets <c>agenteval bench --list</c> show the constraint
/// inline instead of failing at run time. Tracked under v0.11.0 hardening
/// because adding the property is BREAKING for any external consumer that
/// constructs <see cref="BenchmarkPreset"/> via positional arguments.
/// </para>
/// </remarks>
/// <param name="Name">CLI-friendly preset name (lowercase, hyphen-separated). Example: <c>"agentic-execution"</c>.</param>
/// <param name="Description">One-line operator-facing description.</param>
/// <param name="CostTier">Optional override of the family's default cost tier — set when one preset is materially cheaper / more expensive than the others (e.g. OWASP <c>smoke</c> is much cheaper than OWASP <c>audit</c>).</param>
public sealed record BenchmarkPreset(string Name, string Description, CostTier? CostTier = null);

// ─────────────────────────────────────────────────────────────────────────────
// BenchmarkFamily — registration entry.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Registration entry for a benchmark family. Every benchmark family — current
/// (Agentic / GDPR / EU AI Act / OWASP / MITRE / LongMemEval / Memory / Performance)
/// and future (HIPAA / PCI-DSS / ISO 42001 / NIS2 / SOC 2 / UK AI Bill / ...) —
/// constructs one of these and hands it to
/// <see cref="BenchmarkFamilyRegistry.Register(BenchmarkFamily)"/>, typically from a
/// <c>[ModuleInitializer]</c>-attributed static method in its owning assembly so the
/// family auto-registers on assembly load.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two registration shapes</b> are supported (per ADR-017 Convention 3, with the
/// shape dichotomy formalised in Phase 8's gate-review of Phase 7):
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Shape A — <see cref="AgentEval.Evals.CompositeEval"/>-native</b>: factory returns a
///     <see cref="AgentEval.Evals.CompositeEval"/> that the runner can <see cref="IEval.EvaluateAsync"/>
///     directly. Examples: Agentic, GDPR, EU AI Act, Performance (via Convention-2 adapter),
///     OWASP (via run-builder façade), MITRE (via run-builder façade). Supply
///     <see cref="CompositeFactory"/>.
///   </item>
///   <item>
///     <b>Shape B — external-dataset / multi-turn</b>: factory returns a runner-style
///     type with a different invocation contract (e.g. <c>RunAsync(IEvaluableAgent,
///     AgentBenchmarkConfig, ExternalBenchmarkOptions, CancellationToken)</c>). Examples:
///     LongMemEval (returns <c>LongMemEvalBenchmarkRunner</c>), Memory (preset = a
///     <c>MemoryBenchmark</c> config record consumed by <c>MemoryBenchmarkRunner</c>).
///     Supply <see cref="RunnerType"/> + <see cref="RunnerFactory"/>.
///   </item>
/// </list>
/// <para>
/// Both shapes are equally first-class. The CLI's <c>bench --list</c> + per-family
/// <c>--help</c> surfaces operate on the metadata fields, not the factories, so
/// the listing experience is uniform regardless of which shape the family uses.
/// </para>
/// <para>
/// <b>Equality semantics</b>: two <see cref="BenchmarkFamily"/> instances are
/// content-equal when their <see cref="Name"/>, <see cref="Description"/>,
/// <see cref="DefaultCostTier"/>, and <see cref="Presets"/> (by name, description,
/// tier) match. The delegates are NOT compared (they're constructed fresh on every
/// module-init in scenarios where the same assembly loads in two test contexts).
/// This lets <see cref="BenchmarkFamilyRegistry.Register(BenchmarkFamily)"/> be
/// idempotent on duplicate-but-same-content register calls while still throwing
/// on real name conflicts.
/// </para>
/// </remarks>
public sealed class BenchmarkFamily
{
    /// <summary>CLI-friendly family name (lowercase, hyphen-separated). Example: <c>"eu-ai-act"</c>.</summary>
    public string Name { get; }

    /// <summary>One-line operator-facing description.</summary>
    public string Description { get; }

    /// <summary>Default cost tier for the family; individual presets may override via <see cref="BenchmarkPreset.CostTier"/>.</summary>
    public CostTier DefaultCostTier { get; }

    /// <summary>Presets exposed by this family. At least one preset is required.</summary>
    public IReadOnlyList<BenchmarkPreset> Presets { get; }

    /// <summary>
    /// Shape A factory: given a preset name and optional judge, returns a
    /// <see cref="AgentEval.Evals.CompositeEval"/>. Null for Shape B families.
    /// </summary>
    public Func<string, IEvaluator?, CompositeEval>? CompositeFactory { get; }

    /// <summary>
    /// Shape B runner type — the .NET <see cref="Type"/> of the runner instance returned by
    /// <see cref="RunnerFactory"/>. Set for Shape B; null for Shape A. Used by Mission
    /// Control rendering and by the CLI to produce typed-output hints.
    /// </summary>
    public Type? RunnerType { get; }

    /// <summary>
    /// Shape B factory: given a preset name, returns a runner-style instance. Caller is
    /// responsible for invoking the appropriate <c>RunAsync</c>/<c>RunBenchmarkAsync</c>
    /// method on the returned object. Null for Shape A families.
    /// </summary>
    public Func<string, object>? RunnerFactory { get; }

    /// <summary>
    /// Canonical Convention-2 adapter (per ADR-017 Convention 2). When provided, this
    /// delegate produces an <see cref="EvalResult"/> from a generic
    /// <see cref="EvalInput"/>, letting the family flow through the unified output-store /
    /// audit-chain / Mission Control rendering pipeline. Shape A families implement this
    /// trivially by delegating to <see cref="AgentEval.Evals.CompositeEval"/>'s built-in
    /// <c>EvaluateAsync</c>; Shape B families may leave it null when their semantics
    /// (N questions → accuracy, multi-turn dialogue, ...) don't map cleanly onto
    /// <c>(EvalInput) → EvalResult</c>.
    /// </summary>
    public Func<EvalInput, IEvaluator?, CancellationToken, Task<EvalResult>>? EvaluateAsync { get; }

    /// <summary>Optional public documentation link surfaced in CLI help + Mission Control.</summary>
    public string? DocLinkUrl { get; }

    /// <summary>Name of the .NET assembly that owns this family (for diagnostics + debugging).</summary>
    public string? OwningAssemblyName { get; }

    /// <summary>Constructs a benchmark family registration.</summary>
    /// <param name="name">CLI-friendly family name (lowercase, hyphen-separated). Required.</param>
    /// <param name="description">One-line operator-facing description. Required.</param>
    /// <param name="defaultCostTier">Default cost tier for the family.</param>
    /// <param name="presets">At least one preset. Required.</param>
    /// <param name="compositeFactory">Shape A factory; supply for CompositeEval-native families.</param>
    /// <param name="runnerType">Shape B runner type; supply for runner-style families.</param>
    /// <param name="runnerFactory">Shape B runner factory; supply for runner-style families.</param>
    /// <param name="evaluateAsync">Optional Convention-2 adapter.</param>
    /// <param name="docLinkUrl">Optional documentation link.</param>
    /// <param name="owningAssemblyName">Optional owning assembly (for diagnostics).</param>
    public BenchmarkFamily(
        string name,
        string description,
        CostTier defaultCostTier,
        IReadOnlyList<BenchmarkPreset> presets,
        Func<string, IEvaluator?, CompositeEval>? compositeFactory = null,
        Type? runnerType = null,
        Func<string, object>? runnerFactory = null,
        Func<EvalInput, IEvaluator?, CancellationToken, Task<EvalResult>>? evaluateAsync = null,
        string? docLinkUrl = null,
        string? owningAssemblyName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(presets);
        if (presets.Count == 0)
            throw new ArgumentException("A benchmark family must declare at least one preset.", nameof(presets));

        // Shape check: at least one factory must be supplied.
        if (compositeFactory is null && runnerFactory is null)
        {
            throw new ArgumentException(
                $"Family '{name}' must supply either a CompositeFactory (Shape A) or a RunnerFactory (Shape B). " +
                "See ADR-017 Convention 3 for the two registration shapes.");
        }
        if (runnerFactory is not null && runnerType is null)
        {
            throw new ArgumentException(
                $"Family '{name}' supplied a RunnerFactory (Shape B) but no RunnerType. " +
                "Both are required for Shape B registration.");
        }

        // Detect duplicate preset names early — this is a programmer-error case that
        // would silently shadow the second-registered preset under the registry.
        var dupes = presets
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupes.Count > 0)
        {
            throw new ArgumentException(
                $"Family '{name}' declares duplicate preset names: [{string.Join(", ", dupes)}].",
                nameof(presets));
        }

        Name = name;
        Description = description;
        DefaultCostTier = defaultCostTier;
        Presets = presets;
        CompositeFactory = compositeFactory;
        RunnerType = runnerType;
        RunnerFactory = runnerFactory;
        EvaluateAsync = evaluateAsync;
        DocLinkUrl = docLinkUrl;
        OwningAssemblyName = owningAssemblyName;
    }

    /// <summary>
    /// Content-equality check used by <see cref="BenchmarkFamilyRegistry.Register(BenchmarkFamily)"/>
    /// to decide whether a re-register is idempotent (same content → no-op) or a real
    /// conflict (different content → throw). Delegates are intentionally excluded —
    /// see remarks on the type.
    /// </summary>
    internal bool ContentEquals(BenchmarkFamily other)
    {
        if (other is null) return false;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
        if (!string.Equals(Description, other.Description, StringComparison.Ordinal)) return false;
        if (DefaultCostTier != other.DefaultCostTier) return false;
        if (Presets.Count != other.Presets.Count) return false;
        for (int i = 0; i < Presets.Count; i++)
        {
            if (!Presets[i].Equals(other.Presets[i])) return false;
        }
        return true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BenchmarkFamilyRegistry — the canonical "where is every benchmark registered" mechanism.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The single source of truth for "where is every benchmark family registered". Every
/// benchmark family in AgentEval — current (Agentic, GDPR, EU AI Act, OWASP, MITRE,
/// LongMemEval, Memory, Performance) and future (HIPAA, PCI-DSS, ISO 42001, NIS2, SOC 2,
/// UK AI Bill, …) — plugs in here by handing a <see cref="BenchmarkFamily"/> to
/// <see cref="Register(BenchmarkFamily)"/>, typically from a
/// <c>[ModuleInitializer]</c>-attributed static method in its owning assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is canonical</b>: ADR-017 Convention 3 establishes
/// <see cref="BenchmarkFamilyRegistry"/> as the single source of truth for benchmark
/// family discovery. <c>agenteval bench --list</c>, per-family <c>--help</c>, Mission
/// Control's family-discovery surface, and any future external-registrar plugin
/// mechanism (e.g. a third-party <c>AgentEval.Compliance.Hipaa</c> NuGet package
/// auto-registers on assembly load via <c>[ModuleInitializer]</c>) all read from
/// this registry. Implementing a new benchmark family without registering here is a
/// contract violation caught by <c>BenchmarkNamespaceContractTests</c>.
/// </para>
/// <para>
/// <b>Thread safety</b>: <see cref="Register(BenchmarkFamily)"/>, <see cref="TryGet"/>,
/// and <see cref="All"/> are thread-safe (backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>).
/// Concurrent register calls on the same name with the same content are idempotent;
/// concurrent register calls with different content for the same name throw.
/// </para>
/// <para>
/// <b>Two registration shapes</b> are supported — see <see cref="BenchmarkFamily"/>
/// for details. Shape A (<see cref="CompositeEval"/>-native) covers Agentic / GDPR /
/// EU AI Act / OWASP / MITRE / Performance; Shape B (runner-style) covers
/// LongMemEval / Memory.
/// </para>
/// <para>
/// <b>Discovery convention</b>: families register from <c>[ModuleInitializer]</c>-attributed
/// static methods in their owning assembly. This means the registry is populated lazily
/// the first time any code in the owning assembly is touched (a JIT-time guarantee in
/// .NET). The CLI's <c>bench --list</c> entry-point touches at least one type in each
/// expected assembly to force load before enumerating <see cref="All"/>.
/// </para>
/// </remarks>
public static class BenchmarkFamilyRegistry
{
    private static readonly ConcurrentDictionary<string, BenchmarkFamily> _families
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a benchmark family. Idempotent on duplicate-but-same-content register
    /// calls (helpful in scenarios where the same assembly loads twice in different test
    /// contexts). Throws on a name conflict where the existing registration has different
    /// content (catches typos and copy-paste errors).
    /// </summary>
    /// <param name="family">Required.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="family"/> is null.</exception>
    /// <exception cref="InvalidOperationException">When a family with the same name but different content is already registered.</exception>
    public static void Register(BenchmarkFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);

        _families.AddOrUpdate(
            family.Name,
            // Add: new key, accept.
            _ => family,
            // Update: existing key. Idempotent if content-equal, throw otherwise.
            (_, existing) =>
            {
                if (existing.ContentEquals(family))
                    return existing;
                throw new InvalidOperationException(
                    $"BenchmarkFamilyRegistry: family '{family.Name}' is already registered with different content. " +
                    $"This usually means two assemblies tried to claim the same family name. " +
                    $"Existing owner: '{existing.OwningAssemblyName ?? "<unknown>"}'; new owner: '{family.OwningAssemblyName ?? "<unknown>"}'.");
            });
    }

    /// <summary>Returns the family registered under <paramref name="name"/>, or null when no such family is registered.</summary>
    /// <param name="name">Case-sensitive family name. Required.</param>
    public static BenchmarkFamily? TryGet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _families.TryGetValue(name, out var family) ? family : null;
    }

    /// <summary>Enumerates all currently registered families, ordered alphabetically by name.</summary>
    public static IReadOnlyList<BenchmarkFamily> All =>
        _families.Values.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Resets the registry to empty. Intended for test isolation — never called from production
    /// code. Exposed as <c>internal</c> via <c>InternalsVisibleTo</c> rather than public to keep
    /// the production surface clean.
    /// </summary>
    internal static void Reset() => _families.Clear();
}
