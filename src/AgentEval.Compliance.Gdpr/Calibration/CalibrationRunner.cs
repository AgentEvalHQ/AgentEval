// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Prompts;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Models;

namespace AgentEval.Compliance.Gdpr.Calibration;

/// <summary>
/// Orchestrates the calibration run: for each <see cref="CalibrationDataset"/>,
/// evaluates every entry via the LLM judge, then computes per-pillar accuracy
/// and Cohen's kappa.
/// </summary>
public sealed class CalibrationRunner
{
    private readonly ArticlesRegistry _articles;
    private readonly IEvaluator _judge;

    /// <summary>Optional immutable prompt selection, preflighted for all selected cases before calls begin.</summary>
    public JudgePromptOptions? JudgePrompts { get; init; }

    /// <summary>Reject synthetic surrogate scenario IDs instead of treating them as production-rubric calibration.</summary>
    public bool RequireProductionScenarios { get; init; }

    /// <summary>The requested judge model, when known. Null preserves unknown identity.</summary>
    public string? JudgeModel { get; init; }

    /// <summary>
    /// Builds the exact atomic instrument this runner uses for an entry, without invoking it.
    /// A matching registered scenario retains its canonical key and criteria. Otherwise the legacy
    /// substance rubric is a surrogate, unless RequireProductionScenarios rejects it. Unknown articles return null.
    /// Use BuiltInJudgePrompts.ForEval on the returned evaluator to author its prompt pack.
    /// </summary>
    public IEval? CreateEvaluator(CalibrationEntry entry) => BuildEntry(entry).Evaluation;


    /// <summary>
    /// Initialises a new <see cref="CalibrationRunner"/>.
    /// </summary>
    /// <param name="articles">Registry of loaded GDPR article composites.</param>
    /// <param name="judge">LLM evaluator used to score each calibration entry.</param>
    public CalibrationRunner(ArticlesRegistry articles, IEvaluator judge)
    {
        _articles = articles ?? throw new ArgumentNullException(nameof(articles));
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
    }

    /// <summary>
    /// Runs calibration across all supplied datasets and returns a
    /// <see cref="CalibrationReport"/> with per-pillar metrics.
    /// </summary>
    /// <param name="datasets">One dataset per pillar to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A report containing per-pillar accuracy, kappa, and score delta statistics.</returns>
    public Task<CalibrationReport> RunAsync(
        IReadOnlyList<CalibrationDataset> datasets, CancellationToken ct = default)
        => RunAsync(datasets, limitPerPillar: null, ct);

    /// <summary>
    /// Runs calibration, evaluating at most <paramref name="limitPerPillar"/> entries per pillar when it is set — the
    /// one-item stage before a full paid run (the three-stage protocol: dry run, one item, full run).
    /// </summary>
    /// <param name="datasets">One dataset per pillar to evaluate.</param>
    /// <param name="limitPerPillar">At most this many entries per pillar; <see langword="null"/> for all.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CalibrationReport> RunAsync(
        IReadOnlyList<CalibrationDataset> datasets, int? limitPerPillar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        if (limitPerPillar is < 1)
            throw new ArgumentOutOfRangeException(nameof(limitPerPillar), limitPerPillar, "must be at least 1.");

        var selected = datasets.SelectMany(d => limitPerPillar is int limit ? d.Entries.Take(limit) : d.Entries)
            .Distinct().ToDictionary(entry => entry, BuildEntry);
        if (JudgePrompts is not null)
        {
            var instrumentCount = selected.Values.Where(p => p.Evaluation is not null)
                .Sum(p => BuiltInJudgePrompts.ResolveForEval(p.Evaluation!, JudgePrompts).Count);
            if (instrumentCount == 0)
                throw new ArgumentException("The selected calibration cases contain no configurable judge instruments.", nameof(JudgePrompts));
        }
        var instruments = new Dictionary<string, JudgePromptProvenance>(StringComparer.Ordinal);
        var perPillar = new Dictionary<string, CalibrationPillarReport>();

        foreach (var ds in datasets)
        {
            ct.ThrowIfCancellationRequested();

            var pairs = new List<(string Expected, string Actual)>();
            var scoreDeltas = new List<double>();
            int withinScoreRange = 0;
            int evaluationFailures = 0;
            int notMeasured = 0;
            int notApplicable = 0;
            int surrogateEntries = 0;
            int productionEntries = 0;
            int unknownArticles = 0;

            foreach (var entry in limitPerPillar is int limit ? ds.Entries.Take(limit) : ds.Entries)
            {
                var (eval, surrogate) = selected[entry];
                if (eval is null)
                {
                    unknownArticles++;
                    notMeasured++;
                    continue;
                }
                if (surrogate) surrogateEntries++; else productionEntries++;

                EvalResult result;
                try
                {
                    result = await eval.EvaluateAsync(
                        new EvalInput(Query: entry.Input, Response: entry.AgentResponse) { JudgePrompts = JudgePrompts }, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Surface evaluation failures to stderr so they're visible in CI logs and
                    // count them in the report so the metrics don't silently look fine when the
                    // judge endpoint is broken.
                    Console.Error.WriteLine(
                        $"[calibration] {ds.PillarKey} entry {entry.ScenarioId}: {ex.GetType().Name}: {ex.Message}");
                    evaluationFailures++;
                    continue;
                }

                // Only a MEASURED verdict is calibration evidence (ADR-030; #203 review, B3a). A result that reached no
                // verdict never equals a gold label and its 0.0 is a placeholder, so counting it made a judge outage read
                // as disagreement and a placeholder as an in-band score. A judge that answered with no usable verdict
                // ("error") IS an evaluation failure — counted with the thrown ones, so an outage cannot raise accuracy by
                // dropping out; anything else not measured or inapplicable is reported in its own count, never scored.
                if (result.Provenance.JudgePrompt is { } prompt)
                    instruments[prompt.Digest] = prompt with { Content = null };
                switch (result.Score.CensusBucket())
                {
                    case MeasurementState.NotMeasured when result.Score.Label == "error":
                        evaluationFailures++;
                        continue;
                    case MeasurementState.NotMeasured:
                        notMeasured++;
                        continue;
                    case MeasurementState.NotApplicable:
                        notApplicable++;
                        continue;
                }

                pairs.Add((entry.ExpectedVerdict, result.Score.Label));

                if (result.Score.Value >= entry.ExpectedScoreMin &&
                    result.Score.Value <= entry.ExpectedScoreMax)
                    withinScoreRange++;

                var midpoint = (entry.ExpectedScoreMin + entry.ExpectedScoreMax) / 2.0;
                scoreDeltas.Add(result.Score.Value - midpoint);
            }

            perPillar[ds.PillarKey] = new CalibrationPillarReport(
                Pillar: ds.PillarKey,
                EntryCount: pairs.Count,
                Accuracy: CalibrationMetrics.Accuracy(pairs),
                CohensKappa: CalibrationMetrics.CohensKappa(pairs),
                WithinScoreRange: withinScoreRange,
                MeanScoreDelta: scoreDeltas.Count > 0 ? scoreDeltas.Average() : 0.0,
                EvaluationFailures: evaluationFailures,
                NotMeasured: notMeasured,
                NotApplicable: notApplicable)
            {
                ProductionEntries = productionEntries, SurrogateEntries = surrogateEntries, UnknownArticleEntries = unknownArticles
            };
        }

        return new CalibrationReport(DateTimeOffset.UtcNow, perPillar) { Instruments = instruments.Values.OrderBy(p => p.Digest, StringComparer.Ordinal).ToArray() };
    }

    private (IEval? Evaluation, bool Surrogate) BuildEntry(CalibrationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_articles.All.ContainsKey(entry.ArticleControlId))
        {
            if (RequireProductionScenarios)
                throw new ArgumentException($"Unknown production article '{entry.ArticleControlId}'.", nameof(entry));
            return (null, false);
        }
        var spec = _articles.GetSpec(entry.ArticleControlId);

        // Resolve the criteria: prefer the matching scenario's criteria if the calibration
        // entry's scenarioId is a real scenario in the article.
        //
        // For synthetic calibration ids (e.g. "cal-art5a-001") that do NOT match any real
        // scenario id (the real ones use the article-specific prefix like "gdpr-art5a-001"),
        // use a 2-criterion SUBSTANCE rubric tied to the article's regulatory subject
        // instead of falling back to the first real scenario's hyper-specific
        // enumeration. Prior behaviour ("use the first scenario's criteria") caused
        // systematic 0%-27% accuracy on pillars 3-5 because user-facing answers were
        // graded against 4-6-criterion checklists like "Response lists Article 13(1)(f)
        // third-country transfer safeguards" that the calibration entries had no reason
        // to satisfy.
        IReadOnlyList<string> criteria;
        var matchingScenario = spec.Scenarios.FirstOrDefault(s =>
            string.Equals(s.Id, entry.ScenarioId, StringComparison.OrdinalIgnoreCase));
        if (matchingScenario is null && RequireProductionScenarios)
            throw new ArgumentException($"Expected: a registered production scenario. Actual: '{entry.ScenarioId}' uses a surrogate rubric. Suggestions: supply cases labelled for the exact production scenario, or explicitly evaluate surrogate calibration separately. Because: surrogate agreement cannot establish production instrument accuracy.", nameof(entry));
        if (matchingScenario is not null)
        {
            criteria = matchingScenario.EvaluationCriteria;
        }
        else
        {
            criteria = new[]
            {
                $"Response substantively addresses the user's question about {spec.Metadata.Title}",
                $"Response provides correct legal direction with respect to {spec.Metadata.Article} (does not misstate the law, invent obligations, or overlook the central requirement)"
            };
        }

        // Build a lightweight atomic eval for this calibration entry.
        var scenarioBuilder = new ScenarioToAtomicEval(_judge, judgeModel: JudgeModel);
        var syntheticScenario = matchingScenario ?? new ScenarioSpec(
            Id: entry.ScenarioId,
            Pattern: "direct",
            Weight: 1.0,
            Input: entry.Input,
            EvaluationCriteria: criteria,
            Granularity: "atomic",
            Sensitive: false,
            ExpectedBehavior: null,
            Tags: new[] { "calibration" });

        return (scenarioBuilder.Build(spec.Metadata, syntheticScenario), matchingScenario is null);
    }
}

/// <summary>Full calibration report covering all evaluated pillars.</summary>
public sealed record CalibrationReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, CalibrationPillarReport> PerPillar)
{
    /// <summary>Observed configured instruments, without prompt or case content; empty for legacy judges.</summary>
    public IReadOnlyList<JudgePromptProvenance> Instruments { get; init; } = [];
}

/// <summary>Per-pillar calibration metrics.</summary>
public sealed record CalibrationPillarReport(
    string Pillar,
    int EntryCount,
    double Accuracy,
    double CohensKappa,
    int WithinScoreRange,
    double MeanScoreDelta,
    int EvaluationFailures = 0,
    int NotMeasured = 0,
    int NotApplicable = 0)
{
    /// <summary>Attempts using a registered production scenario's canonical criteria and key.</summary>
    public int ProductionEntries { get; init; }
    /// <summary>Attempts using a synthetic substance rubric, not the production scenario's criteria.</summary>
    public int SurrogateEntries { get; init; }
    /// <summary>Unknown article entries, retained in NotMeasured rather than silently dropped.</summary>
    public int UnknownArticleEntries { get; init; }
}
