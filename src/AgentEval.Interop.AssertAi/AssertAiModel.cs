// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Interop.AssertAi;

/// <summary>
/// The identity of one ASSERT test case: its <c>type</c> (<c>prompt</c> or <c>scenario</c>) and its
/// <c>test_case_id</c>. ASSERT numbers test cases by position (<c>test_case_000001</c>, …) and keys every row by
/// both values, so the same id can exist once as a prompt and once as a scenario.
/// </summary>
public readonly record struct AssertAiCaseKey(string Type, string TestCaseId)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Type}:{TestCaseId}";
}

/// <summary>One category of an ASSERT taxonomy (<c>behavior_categories[i]</c>).</summary>
/// <param name="Index">Its position in <c>behavior_categories</c>, which ASSERT's <c>node_index</c> refers to.</param>
/// <param name="Name">The name with surrounding white space removed, as ASSERT matches it.</param>
/// <param name="Definition">What the category means.</param>
/// <param name="Permissible">
/// True only when the file holds the JSON boolean <c>true</c>, as ASSERT reads it. A permissible category is something
/// the target is allowed to do: "violated" then means it failed to help (over-refusal). A category that is not
/// permissible is something it must not do: "violated" then means harm.
/// </param>
public sealed record AssertAiCategory(int Index, string Name, string? Definition, bool Permissible);

/// <summary>An ASSERT taxonomy (<c>taxonomy.json</c>).</summary>
/// <param name="BehaviorName">The behavior it describes.</param>
/// <param name="Categories">The categories that are JSON objects, in file order.</param>
/// <param name="Raw">The file as read.</param>
public sealed record AssertAiTaxonomy(string? BehaviorName, IReadOnlyList<AssertAiCategory> Categories, JsonObject Raw)
{
    /// <summary>Reads a taxonomy object the way ASSERT's metrics read it.</summary>
    public static AssertAiTaxonomy FromJson(JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var categories = new List<AssertAiCategory>();
        if (raw["behavior_categories"] is JsonArray list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is JsonObject entry)
                {
                    categories.Add(new AssertAiCategory(
                        i,
                        (AssertAiJson.Str(entry["name"]) ?? string.Empty).Trim(),
                        AssertAiJson.Str(entry["definition"]),
                        AssertAiJson.Bool(entry["permissible"]) == true));
                }
            }
        }

        var behavior = raw["behavior"] is JsonObject b ? AssertAiJson.Str(b["name"]) : AssertAiJson.Str(raw["behavior"]);
        return new AssertAiTaxonomy(behavior, categories, raw);
    }
}

/// <summary>The judge's finding for one taxonomy category on one test case (<c>verdict.node_judgments[i]</c>).</summary>
/// <param name="NodeIndex">The category's position in the taxonomy, when the row gives an integer.</param>
/// <param name="NodeName">The category name, trimmed.</param>
/// <param name="Relevant">
/// False when the row says the category is not relevant to this case. A row without the key counts as relevant, as in
/// ASSERT's metrics.
/// </param>
/// <param name="Violated">The verdict when it is a JSON boolean; anything else (null for an irrelevant category) is
/// <see langword="null"/> and counts for nothing.</param>
/// <param name="Confidence"><c>high</c>, <c>medium</c> or <c>low</c>.</param>
/// <param name="EvidenceTurns">The transcript message numbers the judge cited.</param>
/// <param name="Reasoning">The judge's explanation.</param>
public sealed record AssertAiNodeJudgment(
    int? NodeIndex, string NodeName, bool Relevant, bool? Violated, string? Confidence, IReadOnlyList<int> EvidenceTurns, string? Reasoning);

/// <summary>The judge's verdict on one test case (<c>verdict</c> of an <c>ok</c> score row).</summary>
/// <param name="Dimensions">Each judge dimension's value as written: a boolean flag (<c>true</c> = the bad thing
/// happened), an ordinal grade, or null when not applicable.</param>
/// <param name="Justification">The overall justification.</param>
/// <param name="DimensionJustifications">Why each dimension has its value.</param>
/// <param name="NodeJudgments">One finding per taxonomy category, when the row has the list.</param>
/// <param name="Narrative">The judge's short narrative.</param>
/// <param name="Warnings">ASSERT's citation and judgment warnings.</param>
/// <param name="Raw">The verdict object as read, including citations.</param>
public sealed record AssertAiVerdict(
    IReadOnlyDictionary<string, JsonNode?> Dimensions,
    string? Justification,
    IReadOnlyDictionary<string, string> DimensionJustifications,
    IReadOnlyList<AssertAiNodeJudgment>? NodeJudgments,
    string? Narrative,
    IReadOnlyList<string> Warnings,
    JsonObject Raw);

/// <summary>ASSERT's three effective judge outcomes for a score row.</summary>
public enum AssertAiJudgeStatus
{
    /// <summary>The judge ran and its verdict holds every dimension the run asked for.</summary>
    Ok,

    /// <summary>The judge ran and failed, or returned a verdict ASSERT could not use, or refused the input
    /// (<c>filter_skipped</c>, which ASSERT reads as a judge failure). Never a pass and never a score.</summary>
    JudgeFailed,

    /// <summary>The transcript was not judged: the target or tester refused the input, or the target errored.</summary>
    ScoringSkipped,
}

/// <summary>One row of <c>scores.jsonl</c>.</summary>
/// <param name="Key">The test case.</param>
/// <param name="Behavior">The behavior the case tests.</param>
/// <param name="JudgeModel">The judge model.</param>
/// <param name="Target">The target under test (a model name or endpoint URL).</param>
/// <param name="TesterModel">The tester model; empty for prompt cases. ASSERT splits its metrics on this, not on
/// <c>type</c>.</param>
/// <param name="ScoreKeys">The dimensions the judge had to return.</param>
/// <param name="RawJudgeStatus">The <c>judge_status</c> as written.</param>
/// <param name="Status">The status ASSERT itself derives from the row (its <c>infer_judge_status</c>).</param>
/// <param name="JudgeError">Why the judge failed or the case was skipped.</param>
/// <param name="Verdict">The verdict, for an <see cref="AssertAiJudgeStatus.Ok"/> row.</param>
/// <param name="Factors">The case's stratification levels (<c>dimensions</c> of the row).</param>
/// <param name="Line">The line of <c>scores.jsonl</c> it came from.</param>
/// <param name="Raw">The row as read.</param>
public sealed record AssertAiScoreRow(
    AssertAiCaseKey Key,
    string Behavior,
    string JudgeModel,
    string Target,
    string TesterModel,
    IReadOnlyList<string> ScoreKeys,
    string RawJudgeStatus,
    AssertAiJudgeStatus Status,
    string? JudgeError,
    AssertAiVerdict? Verdict,
    IReadOnlyDictionary<string, string> Factors,
    int Line,
    JsonObject Raw)
{
    /// <summary>True for a multi-turn case driven by a tester model; ASSERT reports these apart from prompt cases.</summary>
    public bool IsScenario => TesterModel.Length > 0;
}
