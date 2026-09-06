// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

using System.Collections.Concurrent;
using AgentEval.Core;

// ─────────────────────────────────────────────────────────────────────────────
// EvalRegistration — one entry: a key, the eval type it names, and a FACTORY.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One registration in an <see cref="IEvalRegistry"/>: a string key (e.g.
/// <c>"task_completion"</c>), the <see cref="Type"/> of the <see cref="IEval"/> it names, and a
/// <b>factory</b> that builds that eval once a judge exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a factory and not an instance.</b> ADR-031 C1 specified this registry as
/// <c>Key → IEval</c> and named, as its first deliverable, the deletion of the 40-entry
/// hand-authored dictionary in <c>BenchAgenticCalibrateCommand</c>. That signature cannot hold
/// those 40 entries: every one of them is <c>new XEval(judge, judgeModel: …)</c> — a construction
/// over a judge that <b>does not exist at module-initialisation time</b>, because the judge is
/// resolved from the environment when the command runs. A registry of instances would have
/// nothing to register. The pattern C1 tells you to copy verbatim,
/// <see cref="AgentEval.Core.Benchmarks.BenchmarkFamilyRegistry"/>, is itself a <i>factory</i>
/// registry (<c>Func&lt;string, IEvaluator?, CompositeEval&gt;</c>), so the ADR's own model
/// contradicted the signature it prescribed. The corrected shape —
/// <c>Key → Func&lt;IEvaluator?, string?, IEval&gt;</c> — is what this type carries.
/// ADR-031 C1 has been updated; <c>MEASUREMENT_STATUS</c> §67.6 records the derivation.
/// </para>
/// <para>
/// <b>Equality semantics.</b> Two registrations are content-equal when their <see cref="Key"/>
/// (case-insensitively, matching the registry's own key comparer) and their
/// <see cref="EvalType"/>'s <see cref="Type.FullName"/> match. The delegate is deliberately
/// <b>not</b> compared: a lambda is a fresh object on every module-init, and comparing
/// <see cref="Delegate.Method"/> across assembly-load contexts is unreliable. Comparing the
/// declared eval type is both stable and the thing a conflict is actually about — "two
/// registrars claim the same key for different evaluators". This mirrors
/// <c>BenchmarkFamily.ContentEquals</c>, which excludes its delegates for the same reason.
/// </para>
/// </remarks>
public sealed class EvalRegistration
{
    /// <summary>The registry key, e.g. <c>"task_completion"</c>. Matched case-insensitively.</summary>
    public string Key { get; }

    /// <summary>
    /// The declared type of the <see cref="IEval"/> this entry builds. Used for content equality,
    /// for diagnostics, and by <see cref="IEvalRegistry.All"/> consumers that want to list what a
    /// key dispatches to without constructing anything. Two different keys may legitimately name
    /// the same type (calibration-table aliases such as <c>prompt_leak</c> →
    /// <c>SystemPromptLeakageEval</c>).
    /// </summary>
    public Type EvalType { get; }

    /// <summary>
    /// Builds the eval. Called at resolution time with the judge and judge-model identifier that
    /// the run resolved. Never called during registration.
    /// </summary>
    public Func<IEvaluator?, string?, IEval> Factory { get; }

    /// <summary>Name of the .NET assembly that registered this entry (diagnostics only).</summary>
    public string? OwningAssemblyName { get; }

    /// <summary>Constructs a registration.</summary>
    /// <param name="key">Registry key. Required, non-blank.</param>
    /// <param name="evalType">The <see cref="IEval"/> type this key dispatches to. Required, and must implement <see cref="IEval"/>.</param>
    /// <param name="factory">Factory invoked at resolution time. Required.</param>
    /// <param name="owningAssemblyName">Optional owning assembly name, for diagnostics.</param>
    /// <exception cref="ArgumentException">When <paramref name="key"/> is blank, or <paramref name="evalType"/> does not implement <see cref="IEval"/>.</exception>
    /// <exception cref="ArgumentNullException">When <paramref name="evalType"/> or <paramref name="factory"/> is null.</exception>
    public EvalRegistration(
        string key,
        Type evalType,
        Func<IEvaluator?, string?, IEval> factory,
        string? owningAssemblyName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(evalType);
        ArgumentNullException.ThrowIfNull(factory);

        if (!typeof(IEval).IsAssignableFrom(evalType))
        {
            throw new ArgumentException(
                $"Registration '{key}' declares EvalType '{evalType.FullName}', which does not implement IEval.",
                nameof(evalType));
        }

        Key = key;
        EvalType = evalType;
        Factory = factory;
        OwningAssemblyName = owningAssemblyName;
    }

    /// <summary>
    /// Content-equality check used by <see cref="IEvalRegistry.Register"/> to decide whether a
    /// re-register is idempotent (same content → no-op) or a real conflict (different content →
    /// throw). The delegate is intentionally excluded — see the remarks on this type.
    /// </summary>
    internal bool ContentEquals(EvalRegistration other)
    {
        if (other is null) return false;
        if (!string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(EvalType.FullName, other.EvalType.FullName, StringComparison.Ordinal);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IEvalRegistry — the contract.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A registry of <see cref="IEval"/> <b>factories</b> keyed by string, so that a table of
/// evaluator keys lives with the evaluators that own them instead of being hand-authored in a CLI
/// command. ADR-031 C1, built to the corrected signature (see <see cref="EvalRegistration"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Instance, not static.</b> <see cref="EvalRegistry.Shared"/> is the process-wide registry that
/// <c>[ModuleInitializer]</c> registrars populate, but the interface is implemented by an ordinary
/// class so a test can construct an isolated registry and never touch shared state. That is a
/// deliberate divergence from <see cref="AgentEval.Core.Benchmarks.BenchmarkFamilyRegistry"/>,
/// which is static and therefore needs an <c>internal Reset()</c> and ordered tests.
/// <see cref="EvalRegistry.Reset"/> still exists for the shared instance, but nothing in the
/// registry's own test suite depends on it.
/// </para>
/// </remarks>
public interface IEvalRegistry
{
    /// <summary>
    /// Registers an entry. Idempotent when an entry with the same key and the same
    /// <see cref="EvalRegistration.EvalType"/> is already present; throws when the key is claimed
    /// for a different eval type.
    /// </summary>
    /// <param name="registration">Required.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="registration"/> is null.</exception>
    /// <exception cref="InvalidOperationException">When the key is already registered for a different eval type.</exception>
    void Register(EvalRegistration registration);

    /// <summary>Returns the registration under <paramref name="key"/>, or null when the key is unknown.</summary>
    /// <param name="key">Registry key. Matched case-insensitively. Required, non-blank.</param>
    EvalRegistration? TryGet(string key);

    /// <summary>All current registrations, ordered by key.</summary>
    IReadOnlyList<EvalRegistration> All { get; }

    /// <summary>
    /// Resolves <paramref name="key"/> to a freshly-built <see cref="IEval"/>, or null when the key
    /// is unknown. This is the shape <c>CalibrationRunner</c>'s
    /// <c>Func&lt;string, IEval?&gt;</c> resolver wants, with the judge supplied here rather than
    /// at registration time.
    /// </summary>
    /// <param name="key">Registry key. Required, non-blank.</param>
    /// <param name="judge">The resolved judge, threaded into the eval's constructor.</param>
    /// <param name="judgeModel">The judge deployment / model identifier, threaded into the eval's constructor for provenance.</param>
    IEval? Resolve(string key, IEvaluator? judge, string? judgeModel);
}

// ─────────────────────────────────────────────────────────────────────────────
// EvalRegistry — the implementation, plus the process-wide Shared instance.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The default <see cref="IEvalRegistry"/>. Thread-safe (backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>); keys are compared with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, matching the dictionary this registry replaced.
/// </summary>
public sealed class EvalRegistry : IEvalRegistry
{
    private readonly ConcurrentDictionary<string, EvalRegistration> _entries
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The process-wide registry. <c>[ModuleInitializer]</c> registrars populate this one, and the
    /// CLI resolves from it. Tests that need isolation construct their own <see cref="EvalRegistry"/>
    /// instead.
    /// </summary>
    public static EvalRegistry Shared { get; } = new();

    /// <inheritdoc />
    public void Register(EvalRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        _entries.AddOrUpdate(
            registration.Key,
            // Add: new key, accept.
            _ => registration,
            // Update: existing key. Idempotent when content-equal, throw otherwise.
            (_, existing) =>
            {
                if (existing.ContentEquals(registration))
                    return existing;
                throw new InvalidOperationException(
                    $"EvalRegistry: key '{registration.Key}' is already registered for eval type " +
                    $"'{existing.EvalType.FullName}' and cannot be re-registered for " +
                    $"'{registration.EvalType.FullName}'. This usually means two registrars claim the " +
                    $"same evaluator key. Existing owner: '{existing.OwningAssemblyName ?? "<unknown>"}'; " +
                    $"new owner: '{registration.OwningAssemblyName ?? "<unknown>"}'.");
            });
    }

    /// <inheritdoc />
    public EvalRegistration? TryGet(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _entries.TryGetValue(key, out var registration) ? registration : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<EvalRegistration> All =>
        _entries.Values.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <inheritdoc />
    public IEval? Resolve(string key, IEvaluator? judge, string? judgeModel)
    {
        var registration = TryGet(key);
        return registration?.Factory(judge, judgeModel);
    }

    /// <summary>
    /// Empties the registry. Intended for test isolation of <see cref="Shared"/>; production code
    /// never calls it. Prefer constructing a fresh <see cref="EvalRegistry"/> in a test over
    /// resetting the shared one.
    /// </summary>
    internal void Reset() => _entries.Clear();
}
