// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// An evaluator card's <c>defaultThreshold</c> is the threshold the evaluator's own result carries (#203 review, B9a). The
/// B9 census found 25 cards that disagreed with the code — coherence's card said 0.75 while the check passes at 0.60 —
/// and the cards are published as the evaluators' reference.
/// </summary>
public class EvaluatorCardThresholdTests
{
    private sealed class EmptyPolicy : AgentEval.Evals.Agentic.Safety.Policy.IPolicyResolver
    {
        public AgentEval.Evals.Agentic.Safety.Policy.ProhibitedActionPolicy GetPolicyFor(string subjectId) => new([], [], [], []);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not found");
    }

    // Every public agentic IEval, built with the arguments a caller would most simply pass.
    internal static Dictionary<string, IEval> BuildAll(IEvaluator judge, List<string> unbuilt)
    {
        var built = new Dictionary<string, IEval>(StringComparer.Ordinal);
        foreach (var type in typeof(AgenticRubrics).Assembly.GetTypes().Append(typeof(F1ScoreEval))
                     .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IEval).IsAssignableFrom(t)))
        {
            var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor is null)
                continue;
            try
            {
                var args = ctor.GetParameters().Select(p =>
                    p.ParameterType == typeof(IEvaluator) ? judge
                    : p.ParameterType == typeof(AgentEval.Evals.Agentic.Safety.Policy.IPolicyResolver) ? new EmptyPolicy()
                    : p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType == typeof(string) ? "card-census"
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : throw new InvalidOperationException($"cannot supply {p.ParameterType.Name} {p.Name}")).ToArray();
                var eval = (IEval)ctor.Invoke(args);
                built.TryAdd(eval.Key, eval);
            }
            catch (Exception ex)
            {
                unbuilt.Add($"{type.Name}: {(ex.InnerException ?? ex).Message}");
            }
        }

        return built;
    }

    // The threshold an evaluator's result carries: a composite's own, else the first judge leaf's or a declared constant.
    private static double? ThresholdOf(IEval eval)
    {
        // Judge drift passes on the raw delta (max_delta < passThreshold) and stores the score threshold 1 − passThreshold,
        // since its score is 1 − max_delta. Every other agentic check compares its score with >=.
        if (eval is AgentEval.Evals.Agentic.JudgeQuality.JudgeDriftEval)
            return 1.0 - (double)eval.GetType().GetField("_passThreshold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(eval)!;
        if (eval is CompositeEval composite)
            return composite.Threshold;
        for (var t = eval.GetType(); t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType == typeof(CompositeEval) && f.GetValue(f.IsStatic ? null : eval) is CompositeEval inner)
                    return inner.Threshold;
                if (f.FieldType == typeof(AtomicLlmEval) && f.GetValue(f.IsStatic ? null : eval) is AtomicLlmEval leaf)
                    return (double)typeof(AtomicLlmEval).GetField("_passThreshold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(leaf)!;
            }
        }

        foreach (var name in new[] { "_passThreshold", "PassThreshold", "_threshold", "Threshold", "DefaultThreshold" })
        {
            var f = eval.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (f is not null && f.FieldType == typeof(double))
                return (double)f.GetValue(f.IsStatic ? null : eval)!;
            var prop = eval.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (prop is not null && prop.PropertyType == typeof(double))
                return (double)prop.GetValue(prop.GetMethod!.IsStatic ? null : eval)!;
        }

        return null;
    }

    [Fact]
    public void EveryCardsDefaultThreshold_IsTheThresholdItsEvaluatorRunsAt()
    {
        AgenticEvalRegistration.Register();
        var unbuilt = new List<string>();
        var evals = BuildAll(new ChatClientEvaluator(new AgentEval.Tests.Evals.RubricJudgeTests.RecordingChatClient(_ => "{}")), unbuilt);
        var problems = new List<string>();
        var unknown = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "AgentEval.Evals.Agentic", "EvaluatorCards"), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var key = doc.RootElement.GetProperty("key").GetString()!;
            if (!doc.RootElement.TryGetProperty("defaultThreshold", out var card))
                continue;
            if (!evals.TryGetValue(key, out var eval))
            {
                unknown.Add(key);
                continue;
            }

            // Every agentic score is 1 for the best outcome (telemetry and drift scores are 1 − the rate): higher is better.
            if (!doc.RootElement.GetProperty("higherIsBetter").GetBoolean())
                problems.Add($"{key}: card says lower is better, but its score is higher-is-better");

            var actual = ThresholdOf(eval);
            if (actual is null)
                unknown.Add(key + " (no threshold found)");
            else if (Math.Abs(actual.Value - card.GetDouble()) > 1e-9)
                problems.Add($"{key}: card {card.GetDouble()}, runs at {actual}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} cards disagree: " + string.Join(" | ", problems));
        Assert.True(unknown.Count == 0, "cards whose evaluator threshold the census cannot read: " + string.Join(", ", unknown)
            + (unbuilt.Count > 0 ? " — types the census could not build (none has a card unless listed above): " + string.Join(" | ", unbuilt) : ""));
    }
}
