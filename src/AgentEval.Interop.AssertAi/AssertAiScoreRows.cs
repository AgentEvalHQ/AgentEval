// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Interop.AssertAi;

/// <summary>
/// Reads <c>scores.jsonl</c> rows with ASSERT's own rules (<c>assert_ai/core/judge.py</c>,
/// <c>infer_judge_status</c> and <c>has_successful_judge_verdict</c>, assert-ai 0.3.0): a row is <c>ok</c> only when
/// its verdict has a node list and a value of the right kind for every dimension the run asked for.
/// </summary>
public static class AssertAiScoreRows
{
    /// <summary>ASSERT's built-in judge dimensions, used when a row names none.</summary>
    public static readonly IReadOnlyList<string> BuiltInDimensions = ["policy_violation", "overrefusal"];

    /// <summary>Reads one score row.</summary>
    /// <param name="row">The JSON object.</param>
    /// <param name="line">Its line number, for messages.</param>
    public static AssertAiScoreRow Parse(JsonObject row, int line = 0)
    {
        ArgumentNullException.ThrowIfNull(row);
        var scoreKeys = StringList(row["score_keys"]);
        var notApplicable = StringList(row["not_applicable_score_keys"]);
        var scales = row["dimension_scales"] as JsonObject;
        var verdictNode = row["verdict"] as JsonObject;
        var status = InferStatus(row["judge_status"], verdictNode, scoreKeys, notApplicable, scales);

        var factors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (row["dimensions"] is JsonObject dims)
        {
            foreach (var (name, value) in dims)
            {
                if (AssertAiJson.Str(value) is { } level)
                {
                    factors[name] = level;
                }
            }
        }

        return new AssertAiScoreRow(
            new AssertAiCaseKey(AssertAiJson.Str(row["type"]) ?? string.Empty, AssertAiJson.Str(row["test_case_id"]) ?? string.Empty),
            AssertAiJson.Str(row["behavior"]) ?? string.Empty,
            AssertAiJson.Str(row["judge_model"]) ?? string.Empty,
            AssertAiJson.Str(row["target"]) ?? string.Empty,
            AssertAiJson.Str(row["tester_model"]) ?? string.Empty,
            scoreKeys ?? BuiltInDimensions,
            AssertAiJson.Str(row["judge_status"]) ?? string.Empty,
            status,
            AssertAiJson.Str(row["judge_error"]),
            status == AssertAiJudgeStatus.Ok && verdictNode is not null ? ParseVerdict(verdictNode) : null,
            factors,
            line,
            row);
    }

    /// <summary>ASSERT's <c>infer_judge_status</c>.</summary>
    internal static AssertAiJudgeStatus InferStatus(
        JsonNode? judgeStatus, JsonObject? verdict, IReadOnlyList<string>? scoreKeys, IReadOnlyList<string>? notApplicable, JsonObject? scales)
    {
        var status = AssertAiJson.Str(judgeStatus);
        var success = HasSuccessfulVerdict(verdict, scoreKeys, notApplicable, scales);
        if (status == "scoring_skipped")
        {
            return AssertAiJudgeStatus.ScoringSkipped;
        }

        if (status == "ok")
        {
            return success ? AssertAiJudgeStatus.Ok : AssertAiJudgeStatus.JudgeFailed;
        }

        if (!string.IsNullOrEmpty(status))
        {
            return AssertAiJudgeStatus.JudgeFailed;   // filter_skipped, judge_failed, and anything newer
        }

        return success ? AssertAiJudgeStatus.Ok : AssertAiJudgeStatus.JudgeFailed;
    }

    private static bool HasSuccessfulVerdict(
        JsonObject? verdict, IReadOnlyList<string>? scoreKeys, IReadOnlyList<string>? notApplicable, JsonObject? scales)
    {
        if (verdict is null || verdict["node_judgments"] is not JsonArray || verdict["dimensions"] is not JsonObject dimensions)
        {
            return false;
        }

        var notApplicableNames = new HashSet<string>(notApplicable ?? [], StringComparer.Ordinal);
        foreach (var name in scoreKeys ?? BuiltInDimensions)
        {
            var value = dimensions.TryGetPropertyValue(name, out var v) ? v : null;
            if (DimensionValueIsValid(value, scales?[name] as JsonObject))
            {
                continue;
            }

            var markedNotApplicable = dimensions.ContainsKey(name) && value is null
                && verdict["dimension_applicability"] is JsonObject applicability
                && AssertAiJson.Bool(applicability[name]) == false;
            if (!(notApplicableNames.Contains(name) && markedNotApplicable))
            {
                return false;
            }
        }

        return true;
    }

    // _dimension_value_is_valid: a declared ordinal grade of the scale's type, else a strict boolean.
    private static bool DimensionValueIsValid(JsonNode? value, JsonObject? scale)
    {
        var grades = new List<JsonNode>();
        if (scale is not null && AssertAiJson.Str(scale["type"]) == "ordinal" && scale["values"] is JsonArray values)
        {
            foreach (var entry in values)
            {
                if (entry is JsonObject e && e["value"] is { } g && AssertAiJson.Bool(g) is null
                    && (AssertAiJson.Int(g) is not null || AssertAiJson.Str(g) is not null))
                {
                    grades.Add(g);
                }
            }
        }

        if (grades.Count > 0)
        {
            if (AssertAiJson.Str(grades[0]) is not null)
            {
                return AssertAiJson.Str(value) is { } s && grades.Any(g => AssertAiJson.Str(g) == s);
            }

            return AssertAiJson.Int(value) is { } n && grades.Any(g => AssertAiJson.Int(g) == n);
        }

        return AssertAiJson.Bool(value) is not null;
    }

    private static AssertAiVerdict ParseVerdict(JsonObject verdict)
    {
        var dimensions = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (verdict["dimensions"] is JsonObject dims)
        {
            foreach (var (name, value) in dims)
            {
                dimensions[name] = value?.DeepClone();
            }
        }

        var justifications = new Dictionary<string, string>(StringComparer.Ordinal);
        if (verdict["dimension_justifications"] is JsonObject js)
        {
            foreach (var (name, value) in js)
            {
                if (AssertAiJson.Str(value) is { } text)
                {
                    justifications[name] = text;
                }
            }
        }

        List<AssertAiNodeJudgment>? nodes = null;
        if (verdict["node_judgments"] is JsonArray list)
        {
            nodes = [];
            foreach (var item in list)
            {
                if (item is not JsonObject node)
                {
                    continue;
                }

                var index = AssertAiJson.Int(node["node_index"]);
                var turns = node["evidence_turns"] is JsonArray t
                    ? t.Select(AssertAiJson.Int).Where(x => x is not null).Select(x => (int)x!.Value).ToList()
                    : [];
                nodes.Add(new AssertAiNodeJudgment(
                    index is { } i && i is >= int.MinValue and <= int.MaxValue ? (int)i : null,
                    (AssertAiJson.Str(node["node_name"]) ?? string.Empty).Trim(),
                    // results.py: `if "relevant" in node and node.get("relevant") is not True: continue`
                    !node.ContainsKey("relevant") || AssertAiJson.Bool(node["relevant"]) == true,
                    AssertAiJson.Bool(node["violated"]),
                    AssertAiJson.Str(node["confidence"]),
                    turns,
                    AssertAiJson.Str(node["reasoning"])));
            }
        }

        var warnings = new List<string>();
        foreach (var key in new[] { "citation_warnings", "judgment_warnings" })
        {
            if (verdict[key] is JsonArray w)
            {
                warnings.AddRange(w.Select(AssertAiJson.Str).Where(s => s is not null)!);
            }
        }

        return new AssertAiVerdict(
            dimensions,
            AssertAiJson.Str(verdict["justification"]),
            justifications,
            nodes,
            AssertAiJson.Str(verdict["narrative"]),
            warnings,
            (JsonObject)verdict.DeepClone());
    }

    private static List<string>? StringList(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (AssertAiJson.Str(item) is not { } s)
            {
                return null;   // ASSERT ignores the whole list unless every entry is a string
            }

            list.Add(s);
        }

        return list;
    }

}
