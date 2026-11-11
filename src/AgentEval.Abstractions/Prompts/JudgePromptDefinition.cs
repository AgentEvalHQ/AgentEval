// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.RegularExpressions;
using AgentEval.Core;

namespace AgentEval.Prompts;

/// <summary>Versioned response shapes supported by the built-in judge implementations.</summary>
public enum JudgeResponseContract
{
    /// <summary>Overall score and individual criterion verdicts.</summary>
    CriteriaV1,
    /// <summary>A rubric score with optional label, reasoning and evidence.</summary>
    RubricV1,
    /// <summary>Faithfulness score and supported/unsupported claims.</summary>
    FaithfulnessV1,
    /// <summary>Relevance score, question coverage and off-topic content.</summary>
    RelevanceV1,
    /// <summary>Context precision score and relevant/irrelevant passages.</summary>
    ContextPrecisionV1,
    /// <summary>Context recall score and present/missing reference information.</summary>
    ContextRecallV1,
    /// <summary>Answer correctness score and correct/incorrect/missing facts.</summary>
    AnswerCorrectnessV1
}

/// <summary>Immutable text and reading policy for one judge instrument. No executable templates or file paths.</summary>
public sealed record JudgePromptDefinition
{
    private IReadOnlyList<JudgeCriterion> _criteria = Array.Empty<JudgeCriterion>();
    private IReadOnlyList<RubricSeverityBand> _severityBands = Array.Empty<RubricSeverityBand>();
    private IReadOnlyList<string> _dataSlots = Array.Empty<string>();

    /// <summary>Stable evaluator key; independent of language and instrument version.</summary>
    public required string EvaluatorKey { get; init; }
    /// <summary>Author-assigned instrument identity.</summary>
    public required string Id { get; init; }
    /// <summary>Semantic version of this instrument.</summary>
    public required string Version { get; init; }
    /// <summary>Explicit BCP-47 locale. Never inferred from the process culture or input text.</summary>
    public required string Locale { get; init; }
    /// <summary>The fixed machine-readable response shape.</summary>
    public required JudgeResponseContract ResponseContract { get; init; }
    /// <summary>The numeric scale requested from the judge.</summary>
    public required RubricScoreScale Scale { get; init; }
    /// <summary>Pass boundary, normalized to [0, 1] regardless of response scale.</summary>
    public double PassAt { get; init; } = 0.70;
    /// <summary>Optional review boundary on [0, 1], strictly below PassAt.</summary>
    public double? ReviewAt { get; init; }
    /// <summary>Trusted system instructions; data placeholders are forbidden here.</summary>
    public required string SystemText { get; init; }
    /// <summary>User-message template. The implementation fences every substituted data value.</summary>
    public required string UserTemplate { get; init; }
    /// <summary>Corrective user instruction for a single bounded parse retry.</summary>
    public required string RetryText { get; init; }
    /// <summary>Localized instruction treating fenced spans as untrusted evidence, never instructions.</summary>
    public required string SafetyText { get; init; }
    /// <summary>Snapshotted, stable criterion IDs and localizable instructions.</summary>
    public IReadOnlyList<JudgeCriterion> Criteria
    {
        get => _criteria;
        init => _criteria = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    /// <summary>Snapshotted severity bands, ordered from highest threshold to lowest.</summary>
    public IReadOnlyList<RubricSeverityBand> SeverityBands
    {
        get => _severityBands;
        init => _severityBands = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    /// <summary>Data slots required by this definition; a translation may not drop the built-in definition's slots.</summary>
    public IReadOnlyList<string> DataSlots
    {
        get => _dataSlots;
        init => _dataSlots = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    /// <summary>Whether this is authored/calibrated, or an exported draft awaiting translation and review.</summary>
    public string Status { get; init; } = "draft";

    /// <summary>Stable dimension key for a rubric that grades one dimension at a time.</summary>
    public string? Dimension { get; init; }

    /// <summary>Validates policy, bounds and the non-executable template contract before a model call.</summary>
    public void Validate()
    {
        static void Require(bool condition, string actual, string suggestion)
        {
            if (!condition) throw new ArgumentException("Expected: a valid versioned judge definition. Actual: " + actual +
                ". Suggestions: " + suggestion + ". Because: invalid prompt configuration must fail before evaluation.");
        }
        Require(!string.IsNullOrWhiteSpace(EvaluatorKey) && EvaluatorKey.Length <= 256 &&
            Regex.IsMatch(EvaluatorKey, @"\A[A-Za-z0-9][A-Za-z0-9._/-]*\z", RegexOptions.CultureInvariant), "invalid evaluator key", "use a stable ASCII key");
        Require(!string.IsNullOrWhiteSpace(Id) && Id.Length <= 256, "missing or oversized instrument ID", "name the instrument");
        Require(Version is not null && Version.Length <= 64 && Regex.IsMatch(Version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?\z", RegexOptions.CultureInvariant),
            "invalid version", "use a semantic version such as 1.0.0");
        _ = NormalizeLocale(Locale);
        Require(Enum.IsDefined(ResponseContract) && Enum.IsDefined(Scale), "unknown response contract or scale", "select a supported contract and scale");
        Require(ResponseContract != JudgeResponseContract.CriteriaV1 || Scale == RubricScoreScale.Percent,
            "criteria replies require the 0–100 scale", "use Percent for CriteriaV1");
        Require(double.IsFinite(PassAt) && PassAt is >= 0 and <= 1, "invalid pass threshold", "use a finite value in [0, 1]");
        Require(ReviewAt is null || (double.IsFinite(ReviewAt.Value) && ReviewAt >= 0 && ReviewAt < PassAt), "invalid review threshold", "place review below pass");
        Require(Status is "draft" or "authored" or "calibrated", "unknown translation status", "use draft, authored or calibrated");
        foreach (var text in new[] { SystemText, UserTemplate, RetryText, SafetyText })
            Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 131072, "missing or oversized message", "supply each message within 128 Ki characters");
        foreach (var text in new[] { SystemText, RetryText, SafetyText })
            Require(!text.Contains("{{", StringComparison.Ordinal) && !text.Contains("}}", StringComparison.Ordinal),
                "a data placeholder outside the user template", "keep data slots exclusively in UserTemplate");
        var slots = TemplateSlots(UserTemplate);
        Require(slots.Contains("safety", StringComparer.Ordinal), "missing safety instruction slot", "include {{safety}} in UserTemplate");
        Require(slots[0] == "safety", "safety instruction follows data", "place {{safety}} before all data slots");
        Require(slots.Distinct(StringComparer.Ordinal).Count() == slots.Count, "duplicate template slots", "render each slot once");
        Require(DataSlots.Count <= 8 && DataSlots.All(s => s is "input" or "output" or "context" or "reference"),
            "unsupported data slot", "use input, output, context or reference");
        Require(DataSlots.Distinct(StringComparer.Ordinal).Count() == DataSlots.Count && DataSlots.All(s => slots.Contains(s, StringComparer.Ordinal)),
            "missing or repeated data declarations", "retain each declared data slot in UserTemplate");
        Require(slots.Where(s => s is "input" or "output" or "context" or "reference").All(s => DataSlots.Contains(s, StringComparer.Ordinal)),
            "undeclared data slots", "declare every data slot in DataSlots");
        Require(Criteria.Count <= 256 && Criteria.All(c => c is not null && c.Text.Length <= 32768) &&
            Criteria.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == Criteria.Count, "invalid criterion list", "use unique IDs and bounded instructions");
        Require(Criteria.Count == 0 || slots.Contains("criteria", StringComparer.Ordinal), "criteria omitted by template", "include {{criteria}}");
        Require(Dimension is null || (Dimension.Length is > 0 and <= 256 && slots.Contains("dimension", StringComparer.Ordinal)),
            "invalid or omitted dimension", "retain the dimension key and {{dimension}} slot");
        Require(SeverityBands.Count <= 32 && SeverityBands.All(b => b is not null), "invalid severity bands", "supply at most 32 non-null bands");
        ToRubric().Validate();
    }

    /// <summary>Returns the rubric reader policy represented by this definition.</summary>
    public EvalRubric ToRubric() => new()
    {
        Id = Id, Text = SystemText, Scale = Scale, PassAt = PassAt, ReviewAt = ReviewAt,
        SeverityBands = SeverityBands, Dimensional = Dimension is not null
    };

    /// <summary>Normalizes an explicit locale without consulting current culture.</summary>
    public static string NormalizeLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > 64 || locale.Contains('_'))
            throw new ArgumentException("Expected: an explicit BCP-47 locale. Actual: invalid locale. Suggestions: use en or sv-SE. Because: locale selection must be reproducible.", nameof(locale));
        return CultureInfo.GetCultureInfo(locale).Name;
    }

    /// <summary>Finds and validates the small fixed vocabulary of user-template slots.</summary>
    public static IReadOnlyList<string> TemplateSlots(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var matches = Regex.Matches(template, @"\{\{([A-Za-z]+)\}\}", RegexOptions.CultureInvariant);
        var slots = matches.Select(m => m.Groups[1].Value).ToArray();
        var remainder = Regex.Replace(template, @"\{\{([A-Za-z]+)\}\}", "", RegexOptions.CultureInvariant);
        if (remainder.Contains("{{", StringComparison.Ordinal) || remainder.Contains("}}", StringComparison.Ordinal) ||
            slots.Any(s => s is not ("input" or "output" or "context" or "reference" or "criteria" or "dimension" or "safety")))
            throw new ArgumentException("Expected: known, well-formed prompt slots. Actual: an unsupported template expression. " +
                "Suggestions: use only input/output/context/reference/criteria/dimension/safety slots. Because: templates do not execute expressions.", nameof(template));
        return Array.AsReadOnly(slots);
    }
}
