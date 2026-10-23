// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    private static EvalResult? PrepareTree(ScenarioResult scenario) => StoreV1EvalTree.TryParse(scenario.Output);

    private static IEnumerable<EvalResult> Descendants(EvalResult? tree)
    {
        foreach (var child in tree?.Details?.SubResults ?? [])
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string AddTreeMetrics(List<AefMetric> metrics, IReadOnlyList<EvalResult?> trees)
    {
        var ids = metrics.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var descendants = trees.SelectMany(Descendants).ToList();
        foreach (var name in descendants.SelectMany(n => n.Details.Dimensions?.Keys ?? [])
            .Select(DimensionMetric).OfType<string>())
        {
            if (ids.Add(name)) metrics.Add(new AefMetric
            {
                Id = name, Kind = AefMetricKind.Score, Direction = AefMetricDirection.None, Scale = AefScale.Unbounded,
                Description = "A recorded EvalDetails dimension; its direction and scale were not declared.",
            });
        }
        // Root ScenarioResult scores may be unbounded. Do not change their meaning when adding normalized children.
        var id = "agenteval.eval.score";
        for (var suffix = 1; ids.Contains(id); suffix++)
            id = "agenteval.eval.score:" + suffix.ToString(CultureInfo.InvariantCulture);
        if (descendants.Count > 0) metrics.Add(new AefMetric
        {
            Id = id, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
            Description = "The recorded descendant EvalScore's normalized score (0 to 1).",
        });
        return id;
    }

    internal sealed record ProjectedEvalNode(AefResult Result, IReadOnlyList<ProjectedEvalNode> Children);

    internal static ProjectedEvalNode ProjectTree(EvalResult node, string caseId, string path, string metric,
        string lane, AefContentCapture capture)
    {
        var children = (node.Details.SubResults ?? []).Select((child, i) => ProjectTree(child, caseId,
            path + "/" + (i + 1).ToString(CultureInfo.InvariantCulture), metric, lane, capture)).ToList();
        var state = EvalScoreState.Of(node.Score);
        var measured = IsMeasured(state);
        var scores = new List<AefScore> { new() { Metric = metric, Value = node.Score.Value } };
        foreach (var (name, value) in node.Details.Dimensions ?? new Dictionary<string, double>())
            if (DimensionMetric(name) is { } dimension && double.IsFinite(value))
                scores.Add(new() { Metric = dimension, Value = value });
        var facts = EvalResultPersistence.ComparabilityOf(node, null);
        var (_, reason, severity) = TreeState(node, capture);
        return new(new AefResult
        {
            CaseId = caseId, Path = path, Lane = lane,
            Evaluator = new(node.Metric.Key, node.Metric.Version),
            State = state, Reason = reason, Severity = severity,
            Scores = measured ? scores : null,
            VerdictRule = node.Score.Threshold is { } threshold
                ? new("the eval's own rule against its threshold", threshold, "AgentEval EvalScore.Threshold") : null,
            Annotator = measured && children.Count == 0 ? Annotator(facts, node) : null,
            Component = new(0, false),
            Aggregation = children.Count == 0 ? null : Aggregation(node.Score.Value, node.Score.Threshold, state,
                children.Select(c => c.Result).ToList()),
            Usage = node.Provenance.EstimatedCost is { } cost && cost >= 0 && double.IsFinite(cost)
                ? [new() { Role = AefUsageRole.Other, CostUsd = cost, CostSource = "AgentEval store v1 EvalProvenance.EstimatedCost; may include child costs" }] : null,
            Ext = new JsonObject
            {
                [ExtName] = new JsonObject
                {
                    ["evalTreeProjectionVersion"] = 1,
                    ["evaluatedAt"] = node.EvaluatedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["confidence"] = node.Score.Confidence,
                    ["ordinal"] = node.Score.Ordinal,
                    ["tokensUsed"] = node.Provenance.TokensUsed,
                    ["cacheHit"] = node.Provenance.CacheHit,
                    ["producerType"] = node.Provenance.Type,
                    ["componentPolicyRecorded"] = false,
                },
            },
        }, children);
    }

    private static void WriteTree(AefResultHandle parent, ProjectedEvalNode node)
    {
        var handle = parent.AddChild(node.Result);
        foreach (var child in node.Children) WriteTree(handle, child);
    }
}
