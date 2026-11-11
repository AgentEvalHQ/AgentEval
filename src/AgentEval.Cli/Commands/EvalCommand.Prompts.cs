// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Models;
using AgentEval.Prompts;

namespace AgentEval.Cli.Commands;

internal static partial class EvalCommand
{
    // The same preflight resolver supplies the content-free declared suite identity. File paths
    // and case evidence never enter it; the pack has already been loaded into an immutable snapshot.
    private static JsonArray PromptInstruments(JudgePromptOptions options, IReadOnlyList<string>? metrics,
        IReadOnlyList<DatasetTestCase> cases, bool includeCriteriaJudge)
    {
        var resolver = new JudgePromptResolver();
        var resolved = new List<JudgePromptProvenance>();
        foreach (var metric in metrics ?? [])
        {
            var key = metric.ToLowerInvariant();
            if (key == "code_toxicity" && !JudgePromptResolver.CanUseEnglishPrefilter(options, key))
                throw new ArgumentException("Expected: unchanged English code_toxicity keyword rules. Actual: unsupported locale or a definition targeting those rules. " +
                    "Suggestions: remove this metric from the configured run or evaluate it separately with an explicit language policy. " +
                    "Because: prompt packs cannot replace this prefilter.");
            if (!key.StartsWith("llm_", StringComparison.Ordinal)) continue;
            var definition = BuiltInJudgePrompts.Rag.Concat(BuiltInJudgePrompts.Metrics).FirstOrDefault(d => d.EvaluatorKey == key)
                ?? throw new ArgumentException($"Metric '{key}' does not yet support prompt packs; requested localization cannot be ignored.");
            resolved.Add(resolver.Resolve(definition, options).Provenance);
        }
        if (includeCriteriaJudge)
        {
            var judgedCases = cases.Where(c => c.EvaluationCriteria is { Count: > 0 }).ToArray();
            // Dataset strings acquire ordinal IDs. Equal counts do not mean equal instruments:
            // a fixed translation must not silently replace another case's different instructions.
            if (judgedCases.Length > 1 && judgedCases.Skip(1).Any(c =>
                !c.EvaluationCriteria!.SequenceEqual(judgedCases[0].EvaluationCriteria!, StringComparer.Ordinal)))
                throw new ArgumentException("Expected: one fixed criterion roster for a generic CLI prompt pack. Actual: case criteria differ. " +
                    "Suggestions: split the dataset into matching rosters or use library per-evaluation definitions with stable IDs. " +
                    "Because: ordinal IDs alone cannot establish that different instructions are translations of the same criteria.");
            foreach (var testCase in judgedCases)
                resolved.Add(resolver.Resolve(BuiltInJudgePrompts.ForEvaluator("judge.criteria", testCase.EvaluationCriteria!), options).Provenance);
        }
        return new JsonArray(resolved.DistinctBy(p => p.Digest).OrderBy(p => p.EvaluatorKey, StringComparer.Ordinal)
            .ThenBy(p => p.Digest, StringComparer.Ordinal).Select(p => (JsonNode?)new JsonObject
            {
                ["key"] = p.EvaluatorKey, ["id"] = p.InstrumentId, ["version"] = p.Version,
                ["requestedLocale"] = p.RequestedLocale, ["locale"] = p.EffectiveLocale,
                ["fallbackUsed"] = p.FallbackUsed, ["digest"] = p.Digest
            }).ToArray());
    }
}
