// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles.Models;

namespace AgentEval.GdprBenchmark.Articles.Building;

/// <summary>
/// Converts a <see cref="ScenarioSpec"/> (with its parent <see cref="ArticleMetadata"/>)
/// into an <see cref="AtomicLlmEval"/>. Phase 1 / Mode A only — one eval per scenario
/// using the full criteria list. Mode B (per-criterion) is Phase 7 (G7.3).
/// </summary>
public sealed class ScenarioToAtomicEval
{
    private readonly AgentEval.Core.IEvaluator _judge;
    private readonly string? _judgeModel;
    private readonly string _promptId;

    /// <summary>
    /// Initialises a new <see cref="ScenarioToAtomicEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score each scenario.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="promptId">Prompt identifier recorded in provenance.</param>
    public ScenarioToAtomicEval(
        AgentEval.Core.IEvaluator judge,
        string? judgeModel = null,
        string promptId = "gdpr-judge-system.v1")
    {
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
        _judgeModel = judgeModel;
        _promptId = promptId;
    }

    /// <summary>
    /// Builds an <see cref="AtomicLlmEval"/> for <paramref name="scenario"/> scoped to
    /// <paramref name="article"/>'s pass threshold and pillar category.
    /// </summary>
    /// <param name="article">The parent article metadata.</param>
    /// <param name="scenario">The scenario to convert.</param>
    /// <returns>An <see cref="IEval"/> ready to evaluate agent responses.</returns>
    public IEval Build(ArticleMetadata article, ScenarioSpec scenario)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(scenario);

        return new AtomicLlmEval(
            evaluator: _judge,
            key: scenario.Id,
            name: $"{article.Article} — {scenario.Id}",
            category: $"compliance.{article.Pillar}",
            version: "1.0.0",
            criteria: scenario.EvaluationCriteria,
            passThreshold: article.PassThreshold,
            judgeModel: _judgeModel,
            promptId: _promptId);
    }
}
