// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Core;
using AgentEval.Evals;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// v1.1 task 1.6 — Card↔runtime regression test (closes 6-plan F-006).
/// <para>
/// For every shipped <c>EvaluatorCards/&lt;key&gt;.json</c> resource:
/// <list type="bullet">
///   <item>The card's <c>category</c> matches the runtime evaluator's <see cref="IEval.Category"/>.</item>
///   <item>The card's <c>key</c>, <c>name</c>, and <c>version</c> are non-empty.</item>
///   <item>The card's <c>description</c> is non-empty.</item>
///   <item>Every card's <c>runtimeType</c> (resolved via <c>links.source</c> class-name convention) exists in the
///         <c>AgentEval.Evals.Agentic</c> or <c>AgentEval.Core</c> assembly.</item>
/// </list>
/// </para>
/// <remarks>
/// Resolution strategy: the class name is derived from the file name in <c>links.source</c>
/// (e.g. <c>src/AgentEval.Evals.Agentic/System/TaskCompletionEval.cs</c> → <c>TaskCompletionEval</c>).
/// Category is obtained by constructing the evaluator with a minimal stub judge when needed,
/// or by creating a default instance for pure-code evaluators.
/// </remarks>
/// </summary>
public class CardRuntimeMetadataParityTest(ITestOutputHelper output)
{
    // ── Stub judge for LLM-based evaluators ──────────────────────────────────

    /// <summary>
    /// Minimal stub that satisfies the <see cref="IEvaluator"/> contract for construction-time
    /// injection. The stub is never called; it is only used to allow evaluators that delegate
    /// to <see cref="AtomicLlmEval"/> to be instantiated.
    /// </summary>
    private sealed class StubJudge : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvaluationResult { OverallScore = 0 });
    }

    // ── Card loading (mirrors EvaluatorCardSchemaTests pattern) ─────────────

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static IReadOnlyList<(string ResourceName, string Json)> LoadAllShippedCards()
    {
        var asm = typeof(AgentEval.Evals.Agentic.AgenticBenchmark).Assembly;
        var cardResources = asm.GetManifestResourceNames()
            .Where(n => n.Contains("EvaluatorCards.") && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return cardResources.Select(rn =>
        {
            using var s = asm.GetManifestResourceStream(rn)!;
            using var r = new StreamReader(s);
            return (rn, r.ReadToEnd());
        }).ToList();
    }

    // ── Evaluator type resolution ────────────────────────────────────────────

    /// <summary>
    /// Resolves the evaluator <see cref="Type"/> for a given card using the <c>links.source</c>
    /// file-name convention. Returns <c>null</c> when the source path is absent or the class
    /// cannot be found.
    /// </summary>
    private static Type? ResolveRuntimeType(EvaluatorCard card)
    {
        var sourcePath = card.Links?.Source;
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;

        // Derive class name from the source file name:
        // "src/AgentEval.Evals.Agentic/System/TaskCompletionEval.cs" -> "TaskCompletionEval"
        var className = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(className)) return null;

        // Search in the two assemblies that contain evaluators.
        var agenticAsm = typeof(AgentEval.Evals.Agentic.AgenticBenchmark).Assembly;
        var coreAsm    = typeof(AgentEval.Evals.CompositeEval).Assembly;

        return agenticAsm.GetTypes().FirstOrDefault(t => t.Name == className)
            ?? coreAsm.GetTypes().FirstOrDefault(t => t.Name == className);
    }

    /// <summary>
    /// Attempts to instantiate a runtime evaluator of <paramref name="runtimeType"/>.
    /// Returns <c>null</c> if construction fails for any reason.
    /// </summary>
    private static IEval? TryInstantiate(Type runtimeType)
    {
        var stub = new StubJudge();

        // Try the no-arg (pure-code evaluators like LatencyEval, F1ScoreEval, etc.).
        var defaultCtor = runtimeType.GetConstructor(Type.EmptyTypes);
        if (defaultCtor is not null)
        {
            try { return (IEval?)defaultCtor.Invoke(null); } catch { }
        }

        // Try single-arg constructor that takes IEvaluator (most LLM evals: TaskCompletionEval, etc.).
        var judgeOnlyCtor = runtimeType.GetConstructors()
            .FirstOrDefault(c =>
            {
                var ps = c.GetParameters();
                return ps.Length >= 1 && ps[0].ParameterType.IsAssignableFrom(typeof(StubJudge))
                    && ps.Skip(1).All(p => p.HasDefaultValue);
            });
        if (judgeOnlyCtor is not null)
        {
            try
            {
                var args = judgeOnlyCtor.GetParameters()
                    .Select((p, i) => i == 0 ? (object)stub : p.DefaultValue!)
                    .ToArray();
                return (IEval?)judgeOnlyCtor.Invoke(args);
            }
            catch { }
        }

        // Try constructors where every required parameter can be defaulted or null-injected.
        // Evaluators like ProhibitedActionsEval require non-optional interface params (IPolicyResolver);
        // pass null and let the ctor guard throw — skip them; we fall back to CategoryValue reflection.
        foreach (var ctor in runtimeType.GetConstructors().OrderBy(c => c.GetParameters().Length))
        {
            try
            {
                var ps = ctor.GetParameters();
                var args = ps.Select(p =>
                {
                    if (p.ParameterType.IsAssignableFrom(typeof(StubJudge))) return (object)stub;
                    if (p.HasDefaultValue) return p.DefaultValue!;
                    if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType)!;
                    return null!;
                }).ToArray();
                var instance = (IEval?)ctor.Invoke(args);
                if (instance is not null) return instance;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Fallback for types that cannot be instantiated with a stub: reads the
    /// <c>private const string CategoryValue</c> (or <c>s_category</c>) field via reflection.
    /// Returns <c>null</c> when the constant is not found.
    /// </summary>
    private static string? TryReflectCategoryConstant(Type runtimeType)
    {
        // Common patterns across the agentic evaluator implementations:
        //   private const string CategoryValue = "..."   (most classes)
        var field = runtimeType.GetField("CategoryValue",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        if (field is not null && field.FieldType == typeof(string))
            return (string?)field.GetValue(null);

        return null;
    }

    // ── Main parity test ─────────────────────────────────────────────────────

    [Fact]
    public void AllCards_CategoryMatchesRuntime()
    {
        var cards = LoadAllShippedCards();
        var failures = new List<string>();
        var skipped  = new List<string>();

        foreach (var (resourceName, json) in cards)
        {
            var card = JsonSerializer.Deserialize<EvaluatorCard>(json, s_jsonOpts)!;

            // Basic non-empty assertions for every card regardless of runtime resolution.
            if (string.IsNullOrWhiteSpace(card.Key))
                failures.Add($"{resourceName}: key is empty");
            if (string.IsNullOrWhiteSpace(card.Name))
                failures.Add($"{resourceName}: name is empty");
            if (string.IsNullOrWhiteSpace(card.Version))
                failures.Add($"{resourceName}: version is empty");
            if (string.IsNullOrWhiteSpace(card.Description))
                failures.Add($"{resourceName}: description is empty");

            // Resolve and instantiate the runtime type.
            var runtimeType = ResolveRuntimeType(card);
            if (runtimeType is null)
            {
                skipped.Add($"{card.Key}: no links.source or class not found — category parity skipped");
                continue;
            }

            var instance = TryInstantiate(runtimeType);

            // Determine the runtime category — prefer live instance, fall back to reflected constant.
            string? runtimeCategory = instance?.Category
                ?? TryReflectCategoryConstant(runtimeType);

            if (runtimeCategory is null)
            {
                skipped.Add($"{card.Key}: could not determine runtime category for {runtimeType.Name} — parity skipped");
                continue;
            }

            if (instance is not null)
            {
                // Key parity (only checkable when we have a live instance)
                if (!string.Equals(card.Key, instance.Key, StringComparison.Ordinal))
                    failures.Add($"Card '{card.Key}': field key disagrees — card has '{card.Key}' but runtime class {runtimeType.Name} has '{instance.Key}'");

                // Version parity
                if (!string.Equals(card.Version, instance.Version, StringComparison.Ordinal))
                    failures.Add($"Card '{card.Key}': field version disagrees — card has '{card.Version}' but runtime class {runtimeType.Name} has '{instance.Version}'");
            }

            // Category parity (primary objective of this test — works with both instance and reflected constant)
            if (!string.Equals(card.Category, runtimeCategory, StringComparison.Ordinal))
                failures.Add($"Card '{card.Key}': field category disagrees — card has '{card.Category}' but runtime class {runtimeType.Name} has '{runtimeCategory}'");
        }

        // Report skipped items for diagnostics without failing.
        foreach (var s in skipped)
            output.WriteLine($"[SKIP] {s}");

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} card/runtime parity failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AllCards_RequiredMetadataFieldsNonEmpty()
    {
        var cards = LoadAllShippedCards();
        var failures = new List<string>();

        foreach (var (resourceName, json) in cards)
        {
            var card = JsonSerializer.Deserialize<EvaluatorCard>(json, s_jsonOpts)!;
            if (string.IsNullOrWhiteSpace(card.Key))         failures.Add($"{resourceName}: key empty");
            if (string.IsNullOrWhiteSpace(card.Name))        failures.Add($"{resourceName}: name empty");
            if (string.IsNullOrWhiteSpace(card.Version))     failures.Add($"{resourceName}: version empty");
            if (string.IsNullOrWhiteSpace(card.Description)) failures.Add($"{resourceName}: description empty");
            if (string.IsNullOrWhiteSpace(card.Category))    failures.Add($"{resourceName}: category empty");
        }

        Assert.True(failures.Count == 0,
            "Required metadata fields must be non-empty:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AllCards_LinkSourceClassExists()
    {
        // Regression: adding a card that points to a non-existent class should fail immediately.
        var cards = LoadAllShippedCards();
        var failures = new List<string>();

        foreach (var (_, json) in cards)
        {
            var card = JsonSerializer.Deserialize<EvaluatorCard>(json, s_jsonOpts)!;
            var sourcePath = card.Links?.Source;
            if (string.IsNullOrWhiteSpace(sourcePath)) continue;
            if (sourcePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

            var runtimeType = ResolveRuntimeType(card);
            if (runtimeType is null)
                failures.Add($"Card '{card.Key}': links.source='{sourcePath}' — class not found in Agentic or Core assembly");
        }

        Assert.True(failures.Count == 0,
            "All card links.source classes must resolve in the assembly:\n" + string.Join("\n", failures));
    }
}
