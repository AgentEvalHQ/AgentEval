// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
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
    public async Task<CalibrationReport> RunAsync(
        IReadOnlyList<CalibrationDataset> datasets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(datasets);

        var perPillar = new Dictionary<string, CalibrationPillarReport>();

        foreach (var ds in datasets)
        {
            ct.ThrowIfCancellationRequested();

            var pairs = new List<(string Expected, string Actual)>();
            var scoreDeltas = new List<double>();
            int withinScoreRange = 0;
            int evaluationFailures = 0;

            foreach (var entry in ds.Entries)
            {
                // Defensive skip: calibration data may contain synthetic IDs not in the registry.
                if (!_articles.All.TryGetValue(entry.ArticleControlId, out _))
                    continue;

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
                var scenarioBuilder = new ScenarioToAtomicEval(_judge, judgeModel: "calibration");
                var syntheticScenario = new ScenarioSpec(
                    Id: entry.ScenarioId,
                    Pattern: "direct",
                    Weight: 1.0,
                    Input: entry.Input,
                    EvaluationCriteria: criteria,
                    Granularity: "atomic",
                    Sensitive: false,
                    ExpectedBehavior: null,
                    Tags: new[] { "calibration" });

                var eval = scenarioBuilder.Build(spec.Metadata, syntheticScenario);

                EvalResult result;
                try
                {
                    result = await eval.EvaluateAsync(
                        new EvalInput(Query: entry.Input, Response: entry.AgentResponse), ct);
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
                EvaluationFailures: evaluationFailures);
        }

        return new CalibrationReport(DateTimeOffset.UtcNow, perPillar);
    }
}

/// <summary>Full calibration report covering all evaluated pillars.</summary>
public sealed record CalibrationReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, CalibrationPillarReport> PerPillar);

/// <summary>Per-pillar calibration metrics.</summary>
public sealed record CalibrationPillarReport(
    string Pillar,
    int EntryCount,
    double Accuracy,
    double CohensKappa,
    int WithinScoreRange,
    double MeanScoreDelta,
    int EvaluationFailures = 0);
