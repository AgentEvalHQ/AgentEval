// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Writing;

/// <summary>
/// Computes a run's summary.json (contracts/aef/1/spec/03-run.md, §3.6, [SUM-2]–[SUM-9]) from its result lines and
/// metrics for the lanes and entries a producer reports (<see cref="AefSummary"/>): per entry the figures of
/// <see cref="AefSummaryCalculator"/> (<c>N</c>, <c>n</c>, <c>notMeasured</c>, <c>sum</c>, <c>sumSq</c>, and
/// <c>value</c> unless the aggregate method is the producer's), the producer's verdict on them (<c>not_measured</c> when
/// <c>n</c> is 0), and its <c>stderr</c>, <c>ci</c> and <c>rule</c>. <see cref="AefRunWriter.Close"/> writes its run's
/// summary with it; it also summarises a run someone else wrote.
/// </summary>
public static class AefSummaryWriter
{
    /// <summary>
    /// Checks what a summary reports, before any figure is computed: no lane named twice, no two entries with one lane,
    /// metric and path, no two usage entries with one role and model ([SUM-9]), and every value valid against the writer
    /// summary schema.
    /// </summary>
    /// <exception cref="ArgumentException">One of these is broken.</exception>
    public static void Check(AefSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var lanes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in summary.Lanes)
        {
            // SUM-9 (ruled under W3-2): a summary names each lane once.
            if (!lanes.Add(lane.Lane))
            {
                throw new ArgumentException($"The lane {lane.Lane} is listed twice: a summary names each lane once ([SUM-9]).", nameof(summary));
            }

            var keys = new HashSet<(string, string)>();
            foreach (var entry in lane.Entries)
            {
                if (!keys.Add((entry.Metric, entry.Path)))
                {
                    throw new ArgumentException(
                        $"Lane {lane.Lane} has two entries for metric {entry.Metric} at path {entry.Path}: no two entries have one lane, metric and path ([SUM-9]).",
                        nameof(summary));
                }
            }
        }

        var usage = new HashSet<(AefUsageRole, string?)>();
        foreach (var entry in summary.Usage ?? [])
        {
            if (!usage.Add((entry.Role, entry.Model)))
            {
                throw new ArgumentException($"Two usage entries have role {AefWire.Name(entry.Role)} and model {entry.Model ?? "(none)"} ([SUM-9]).", nameof(summary));
            }
        }

        // The shape, against the schema, with the figures of no line (n = 0) in place of the computed ones.
        var shape = Json("run", summary, (_, entry) => EntryJson(entry, new AefSummaryFigures(0, 0, 0, 0, null, true, []), null));
        if (AefSchemas.Writer.Validate("summary", shape) is { } failure)
        {
            throw new ArgumentException($"summary.json would not be valid against the writer summary schema ([VER-2]): {failure}.", nameof(summary));
        }
    }

    /// <summary>
    /// The summary.json document of a run: <c>schemaVersion</c>, the run's <c>runId</c> ([SUM-2]), and per lane and
    /// entry, in the order given, the computed figures and the producer's verdict ([SUM-6]: <c>not_measured</c> when
    /// <c>n</c> is 0, and <see cref="AefSummaryEntry.Decide"/> is not called; <c>scored</c> when it is null).
    /// </summary>
    /// <param name="runId">run.json's <c>runId</c>.</param>
    /// <param name="results">The run's result lines, in results.ndjson order (each valid against the result schema).</param>
    /// <param name="metrics">metrics.json's metrics: each id declared once, with its kind.</param>
    /// <param name="summary">What the summary reports.</param>
    /// <exception cref="ArgumentException">
    /// <see cref="Check"/> refuses <paramref name="summary"/>; an entry names a metric <paramref name="metrics"/> does
    /// not declare ([SUM-1]); or a number the producer gives is not finite ([ENC-3]).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An entry whose aggregate method is the producer's has measured lines and no value from the producer, or one whose
    /// value AEF defines has one ([SUM-8]).
    /// </exception>
    public static JsonObject Build(string runId, IReadOnlyList<JsonObject> results, IReadOnlyDictionary<string, AefMetricKind> metrics, AefSummary summary)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(metrics);
        Check(summary);
        var lanes = summary.Lanes.Select(l => l.Lane).ToList();
        var json = Json(runId, summary, (lane, entry) =>
        {
            if (!metrics.TryGetValue(entry.Metric, out var kind))
            {
                throw new ArgumentException($"Summary lane {lane}: the metric {entry.Metric} is not declared in metrics.json ([SUM-1]).", nameof(summary));
            }

            var figures = AefSummaryCalculator.Compute(results, lanes, lane, entry.Metric, AefWire.Name(kind), entry.Path, entry.Aggregate?.Method);
            var decision = figures.Measured == 0 ? null : entry.Decide?.Invoke(figures);
            if (figures.ValueDefined ? decision?.Value is not null : decision?.Value is null)
            {
                throw new InvalidOperationException(figures.ValueDefined
                    ? $"Summary {lane}/{entry.Metric}/{entry.Path}: the value of this entry is computed ([SUM-5], [SUM-8]); the producer gives none."
                    : $"Summary {lane}/{entry.Metric}/{entry.Path}: the aggregate method {entry.Aggregate!.Method} is the producer's, which gives its value ([SUM-8]).");
            }

            return EntryJson(entry, figures, decision);
        });
        if (AefSchemas.Writer.Validate("summary", json) is { } failure)
        {
            throw new ArgumentException($"summary.json is not valid against the writer summary schema ([VER-2]): {failure}.", nameof(summary));
        }

        return json;
    }

    // One entry in the schema's member order: the figures, the producer's verdict (not_measured when n is 0, scored
    // when it applied no rule, [SUM-6]), its stderr and ci, and sum and sumSq.
    private static JsonObject EntryJson(AefSummaryEntry entry, AefSummaryFigures figures, AefSummaryDecision? decision)
    {
        var verdict = figures.Measured == 0 ? AefSummaryVerdict.NotMeasured : decision?.Verdict ?? AefSummaryVerdict.Scored;
        var value = figures.ValueDefined ? figures.Value : decision?.Value;
        var json = new JsonObject
        {
            ["metric"] = entry.Metric,
            ["n"] = AefWire.Integer(figures.Measured, "n"),
            ["N"] = AefWire.Integer(figures.N, "N"),
            ["notMeasured"] = AefWire.Integer(figures.NotMeasured, "notMeasured"),
            ["value"] = value is { } v ? AefWire.Number(v, "value") : null,
        };
        json.Put("stderr", decision?.Stderr);
        json.Put("ci", decision?.Ci?.ToJson());
        json["verdict"] = AefWire.Node(verdict);
        json.Put("rule", entry.Rule);
        json["sum"] = AefWire.Number(figures.Sum, "sum");
        if (double.IsFinite(figures.SumOfSquares))
        {
            // [SUM-5]: optional; the squares of values beyond about 1.34e154 overflow binary64, and JSON has no infinity.
            json["sumSq"] = AefWire.Number(figures.SumOfSquares, "sumSq");
        }
        json["path"] = entry.Path;
        if (entry.Aggregate is { } aggregate)
        {
            var method = new JsonObject { ["method"] = aggregate.Method };
            method.Put("k", aggregate.K);
            json["aggregate"] = method;
        }

        return json;
    }

    private static JsonObject Json(string runId, AefSummary summary, Func<string, AefSummaryEntry, JsonObject> entryJson)
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = AefRunWriter.SchemaVersion,
            ["runId"] = runId,
            ["lanes"] = AefWire.Array(summary.Lanes, lane => new JsonObject
            {
                ["lane"] = lane.Lane,
                ["metrics"] = AefWire.Array(lane.Entries, entry => entryJson(lane.Lane, entry)),
            }),
        };
        json.Put("ext", AefWire.Ext(summary.Ext));
        if (summary.Cost is { } cost)
        {
            var costJson = new JsonObject { ["totalUsd"] = AefWire.Number(cost.TotalUsd, "cost.totalUsd") };
            costJson.Put("source", cost.Source);
            json["cost"] = costJson;
        }

        json.Put("usage", summary.Usage is null ? null : AefWire.Array(summary.Usage, u => u.ToJson()));
        return json;
    }
}
