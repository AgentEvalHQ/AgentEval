// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json.Nodes;
using AgentEval.Memory.External.Models;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Versioned, content-free diagnostics computed only from the observed typed question vector.</summary>
internal static class TypedMemoryDiagnostics
{
    internal const string ExtensionKey = "agenteval.typedmemeval.diagnostics";
    internal const string SchemaVersion = "1.0";

    internal static JsonObject Create(IReadOnlyList<QuestionResult> observed, string vertical, bool complete)
    {
        var details = observed.Where(q => q.TypedOutcome is not null).Select(q => q.TypedOutcome!).ToArray();
        if (details.Any(d => !Enum.IsDefined(d.Outcome)))
            throw new ArgumentException("Expected: a defined typed-memory outcome. Actual: unknown outcome value. Suggestions: update the producer and diagnostic contract together. Because: outcome counts must sum to their declared denominator.");
        var value = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion, ["vertical"] = vertical.ToLowerInvariant(),
            ["scope"] = "observed-questions", ["complete"] = complete, ["gating"] = false,
            ["observedQuestions"] = observed.Count, ["missingTypedQuestions"] = observed.Count - details.Length,
            ["outcomes"] = Counts(details.Select(d => d.Outcome)),
        };
        if (string.Equals(vertical, "workingmemory", StringComparison.OrdinalIgnoreCase))
        {
            var buckets = new JsonObject();
            foreach (var group in details.Where(d => d.DistanceSessions.HasValue)
                .GroupBy(d => d.DistanceSessions!.Value).OrderBy(g => g.Key))
                buckets[group.Key.ToString(CultureInfo.InvariantCulture)] = Counts(group.Select(d => d.Outcome));
            value["byDistanceSessions"] = buckets;
            value["missingDistanceQuestions"] = details.Count(d => !d.DistanceSessions.HasValue);
        }
        if (string.Equals(vertical, "forgetting", StringComparison.OrdinalIgnoreCase))
            value["forgetting"] = new JsonObject
            {
                ["n"] = details.Length,
                ["staleRecall"] = details.Count(d => d.IsStaleRecall),
                ["overForgetting"] = details.Count(d => d.IsOverForgetting),
                ["misattributedForgetting"] = details.Count(d => d.IsMisattributedForgetting),
            };
        return new JsonObject { [ExtensionKey] = value };
    }

    private static JsonObject Counts(IEnumerable<TypedMemEvalOutcome> outcomes)
    {
        var counts = TypedMemEvalOutcomeCounts.From(outcomes);
        return new JsonObject
        {
            ["n"] = counts.N, ["correct"] = counts.Correct, ["wrong"] = counts.Wrong,
            ["abstained"] = counts.Abstained, ["missed"] = counts.Missed, ["premature"] = counts.Premature,
            ["inconclusive"] = counts.Inconclusive, ["unrun"] = counts.Unrun,
        };
    }
}
