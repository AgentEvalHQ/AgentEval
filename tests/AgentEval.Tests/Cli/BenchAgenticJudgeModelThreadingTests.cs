// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// T3.2 (2026-05-25) — reflection-based regression test that asserts every
// `IEval` shipped under `AgentEval.Evals.Agentic` that accepts a `judgeModel`
// constructor parameter actually STORES the threaded value. Prior to T3.2,
// `BenchAgenticCommand` discarded `judgeModelName` from `JudgeFactory.Resolve`
// because the threading-through-leaves work was incomplete; the regression is
// "future refactor accidentally drops the parameter again and provenance
// silently loses the deployment id".
//
// Threading invariant (what this test pins):
//   ┌─────────────┐   judgeModel    ┌──────────────────┐   _judgeModel
//   │ CLI         │ ──────────────▶ │ Preset factory   │ ─────────────▶
//   │ JudgeFactory│                 │ (AgenticBenchmark)│   IEval ctor
//   └─────────────┘                 └──────────────────┘   wraps AtomicLlmEval
//
// We construct every IEval that takes (IEvaluator, string? judgeModel, …)
// with `judgeModel = "test-deployment-x"` and assert the string is reachable
// somewhere on the constructed instance graph via reflection. The walk is
// shallow-recursive (handles the wrapper-around-AtomicLlmEval shape used by
// every leaf evaluator in the AgentEval.Evals.Agentic catalogue).

using System.Collections;
using System.Reflection;
using AgentEval.Core;
using AgentEval.Evals; // IEval
using AgentEval.Evals.Agentic.Safety.Policy;
using Xunit;

namespace AgentEval.Tests.Cli;

public class BenchAgenticJudgeModelThreadingTests
{
    private const string ThreadedJudgeModel = "test-deployment-x";

    [Fact]
    public void EveryAgenticIEval_WithJudgeModelCtor_StoresThreadedValue()
    {
        // Locate the AgentEval.Evals.Agentic assembly via any type defined in it.
        var agenticAssembly = typeof(AgentEval.Evals.Agentic.System.TaskCompletionEval).Assembly;
        Assert.Equal("AgentEval.Evals.Agentic", agenticAssembly.GetName().Name);

        var ievalType = typeof(IEval);
        var candidates = agenticAssembly.GetTypes()
            .Where(t => t.IsClass
                        && !t.IsAbstract
                        && ievalType.IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(candidates);

        var stubJudge = new StubEvaluator();
        var failures = new List<string>();
        var threadedCount = 0;
        var skippedCount = 0;

        foreach (var type in candidates)
        {
            // Find a ctor whose 1st param is IEvaluator and which has a
            // `judgeModel` parameter (by name, type string?). Skip types
            // without such a ctor — those are the composite/aggregate evals
            // (TelemetryCompositeEval, StochasticStabilityEval, etc.) that
            // do not invoke an LLM judge so judgeModel threading is N/A.
            var ctor = type.GetConstructors()
                .Where(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length >= 2
                        && typeof(IEvaluator).IsAssignableFrom(ps[0].ParameterType)
                        && ps.Any(p => p.Name == "judgeModel" && p.ParameterType == typeof(string));
                })
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor is null)
            {
                skippedCount++;
                continue;
            }

            // Build arg array: IEvaluator = stub; judgeModel = threaded value;
            // every other parameter gets a default (null for ref types, default
            // for value types). Some evaluators accept `subjectId` (Safety
            // family) which must be non-null; supply a sentinel.
            object?[] args;
            try
            {
                args = BuildCtorArgs(ctor, stubJudge);
            }
            catch (Exception ex)
            {
                failures.Add($"{type.FullName}: failed to materialise ctor args ({ex.Message})");
                continue;
            }

            object instance;
            try
            {
                instance = ctor.Invoke(args)!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                failures.Add($"{type.FullName}: ctor threw {tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
                continue;
            }
            catch (Exception ex)
            {
                failures.Add($"{type.FullName}: ctor threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // Walk the instance graph (private fields, properties) up to
            // depth 8 looking for the threaded model identifier. The shapes
            // we encounter:
            //   - WrappingEval._inner = AtomicLlmEval → depth 2.
            //   - WrappingEval._inner = CompositeEval → .Components →
            //     [EvalComponent] → .Eval = AtomicLlmEval → depth 5.
            //   - QaCompositeEval / ToolCallAccuracyAggregateEval = deeper
            //     composite of composites → depth 7+.
            if (!ContainsThreadedValue(instance, ThreadedJudgeModel, maxDepth: 8))
            {
                failures.Add($"{type.FullName}: threaded judgeModel='{ThreadedJudgeModel}' not stored anywhere on the instance graph (depth ≤ 4).");
                continue;
            }
            threadedCount++;
        }

        // At least one evaluator must have been verified — otherwise the
        // assembly scanning is broken and the test is silently green.
        Assert.True(threadedCount >= 5,
            $"Expected ≥ 5 agentic IEval implementations to thread judgeModel correctly. " +
            $"Threaded: {threadedCount}, Skipped (no judgeModel ctor): {skippedCount}, Failures: {failures.Count}.");

        Assert.True(failures.Count == 0,
            $"judgeModel threading regression: {failures.Count} evaluator(s) lost the value.\n  " +
            string.Join("\n  ", failures));
    }

    private static object?[] BuildCtorArgs(ConstructorInfo ctor, IEvaluator judge)
    {
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (typeof(IEvaluator).IsAssignableFrom(p.ParameterType))
            {
                args[i] = judge;
            }
            else if (p.Name == "judgeModel" && p.ParameterType == typeof(string))
            {
                args[i] = ThreadedJudgeModel;
            }
            else if (p.Name == "subjectId" && p.ParameterType == typeof(string))
            {
                // Safety / ProhibitedActions need a non-empty subjectId.
                args[i] = "judge-model-threading-test-subject";
            }
            else if (typeof(IPolicyResolver).IsAssignableFrom(p.ParameterType))
            {
                // ProhibitedActionsEval guards against a null policy resolver;
                // supply a static empty-policy resolver.
                args[i] = new StaticPolicyResolver(new ProhibitedActionPolicy([], [], [], []));
            }
            else if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
            }
            else if (p.ParameterType.IsValueType)
            {
                args[i] = Activator.CreateInstance(p.ParameterType);
            }
            else
            {
                // Last resort: null for ref types. Many of these ctors do
                // ArgumentNullException-guard the LLM-judge slot only; null
                // is acceptable for content-safety client / policy resolver.
                args[i] = null;
            }
        }
        return args;
    }

    private static bool ContainsThreadedValue(object root, string needle, int maxDepth)
    {
        // Iterative BFS with a depth cap and a visited-by-reference set to
        // prevent stack overflow on graphs with cycles (e.g. evaluator that
        // holds a back-reference to a parent composite).
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, int Depth)>();
        queue.Enqueue((root, 0));

        // Skip walking into system / third-party types — only inspect our
        // own assemblies and primitives. This keeps the walk bounded.
        var ourAssemblies = new[] { "AgentEval", "AgentEval.Core", "AgentEval.Abstractions", "AgentEval.Evals.Agentic" };

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;
            if (node is null) continue;
            if (node is string s)
            {
                if (string.Equals(s, needle, StringComparison.Ordinal)) return true;
                continue;
            }
            var t = node.GetType();
            if (!t.IsClass || t == typeof(object)) continue;
            // Skip framework types; their internals are not relevant.
            if (t.Namespace is { } ns && (ns.StartsWith("System") || ns.StartsWith("Microsoft.")))
            {
                // BUT still walk IEnumerable contents (List<T>, etc.) — the
                // T is what matters, not the container itself.
                if (node is IEnumerable systemEnum and not string)
                {
                    foreach (var item in systemEnum)
                    {
                        if (item is null) continue;
                        if (!visited.Add(item)) continue;
                        queue.Enqueue((item, depth + 1));
                    }
                }
                continue;
            }
            if (!visited.Add(node)) continue;

            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value;
                try { value = f.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                queue.Enqueue((value, depth + 1));
            }
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? value;
                try { value = p.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                queue.Enqueue((value, depth + 1));
            }
            if (node is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is null) continue;
                    queue.Enqueue((item, depth + 1));
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Minimal IEvaluator stub used only for instantiation; never actually
    /// invoked because the test never calls EvaluateAsync.
    /// </summary>
    private sealed class StubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EvaluationResult { OverallScore = 0, Summary = "stub" });
    }
}
