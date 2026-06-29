// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles.Models;

namespace AgentEval.Compliance.EuAiAct.Articles.Building;

/// <summary>
/// Converts a <see cref="ScenarioSpec"/> (with its parent <see cref="ArticleMetadata"/>)
/// into an <see cref="IEval"/>.
/// <para>
/// Mode A (default): one <see cref="AtomicLlmEval"/> per scenario using the full criteria list.
/// </para>
/// <para>
/// Mode B (<c>useModeB=true</c>): when <see cref="ScenarioSpec.Granularity"/> is
/// <c>"composite"</c>, each criterion becomes its own <see cref="AtomicLlmEval"/>
/// wrapped in a <see cref="CompositeEval"/>. Atomic-granularity scenarios still produce
/// a single <see cref="AtomicLlmEval"/> even in Mode B.
/// </para>
/// <para>
/// Multi-judge path: when the optional <c>judges</c> list has more than one entry AND the
/// article severity is <c>"critical"</c>, each judge produces its own <see cref="AtomicLlmEval"/>
/// and the scenario is wrapped with a <see cref="MultiJudgeWrapper"/> using
/// <see cref="WeightedMedianAggregation"/>.
/// </para>
/// </summary>
public sealed class ScenarioToAtomicEval
{
    private readonly AgentEval.Core.IEvaluator _judge;
    private readonly string? _judgeModel;
    private readonly string _systemPromptId;
    private readonly string _perCriterionPromptId;
    private readonly bool _modeB;
    private readonly IReadOnlyList<(AgentEval.Core.IEvaluator Judge, double Weight)>? _judges;
    private readonly AgentEval.Core.IEvaluableAgent? _agent;

    /// <summary>
    /// Initialises a new <see cref="ScenarioToAtomicEval"/> with a single judge (Mode A or B).
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score each scenario.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="promptId">Prompt identifier for single-eval (Mode A) scenarios.</param>
    /// <param name="perCriterionPromptId">Prompt identifier for per-criterion (Mode B) sub-evals.</param>
    /// <param name="useModeB">When <c>true</c>, composite-granularity scenarios are split into per-criterion sub-evals.</param>
    /// <param name="judges">
    /// Optional list of judges with weights. When provided and the article is critical-severity,
    /// the scenario is wrapped with a <see cref="MultiJudgeWrapper"/> for multi-judge consensus.
    /// When <c>null</c> or empty, the primary <paramref name="judge"/> is used.
    /// </param>
    /// <param name="agent">
    /// Optional live agent-under-test. When supplied, each scenario drives this agent with its own
    /// article-specific prompt and grades the agent's real answer (live-agent judging); when <c>null</c>,
    /// the supplied fixed response is graded instead.
    /// </param>
    public ScenarioToAtomicEval(
        AgentEval.Core.IEvaluator judge,
        string? judgeModel = null,
        string promptId = "eu-ai-act-judge-system.v1",
        string perCriterionPromptId = "per-criterion.v1",
        bool useModeB = false,
        IReadOnlyList<(AgentEval.Core.IEvaluator Judge, double Weight)>? judges = null,
        AgentEval.Core.IEvaluableAgent? agent = null)
    {
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
        _judgeModel = judgeModel;
        _systemPromptId = promptId;
        _perCriterionPromptId = perCriterionPromptId;
        _modeB = useModeB;
        _judges = judges;
        _agent = agent;
    }

    /// <summary>
    /// Builds an <see cref="IEval"/> for <paramref name="scenario"/> scoped to
    /// <paramref name="article"/>'s pass threshold, pillar category, and severity.
    /// </summary>
    public IEval Build(ArticleMetadata article, ScenarioSpec scenario)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(scenario);

        // Multi-judge path: when multiple judges are supplied AND the article is critical-severity,
        // build per-judge atomics and wrap with MultiJudgeWrapper(WeightedMedianAggregation).
        //
        // KNOWN v1 LIMITATION (inherited from GDPR plan-03 G7.6): when both multi-judge AND
        // Mode-B are requested for the same Critical-severity scenario, multi-judge takes
        // precedence and Mode-B (per-criterion split) is silently skipped. Full multi-judge
        // x Mode-B would nest a CompositeEval of per-criterion atomics inside each judge,
        // then wrap N judge composites in MultiJudgeWrapper — 3 judges x N criteria = 3N LLM
        // calls per scenario. Tracked as a Phase 11+ enhancement when a real consumer
        // demands the cost trade-off.
        IEval built;
        if (_judges is { Count: > 1 } && article.Severity == "critical")
        {
            built = BuildMultiJudge(article, scenario);
        }
        else if (_modeB && scenario.Granularity == "composite")
        {
            built = BuildModeB(article, scenario);
        }
        else
        {
            built = BuildAtomic(article, scenario, _judge, _judgeModel, _systemPromptId, scenario.Id);
        }

        // When an agent is supplied, drive it with this scenario's own prompt and grade its real
        // answer; otherwise grade the runner's fixed response as before.
        return _agent is null
            ? built
            : new AgentScenarioEval(_agent, scenario.Input, built);
    }

    private IEval BuildMultiJudge(ArticleMetadata article, ScenarioSpec scenario)
    {
        var judgeComponents = _judges!.Select((entry, idx) =>
            new EvalComponent(
                // BUG-34: record the REAL judge model (not a positional "judge-N" label) so
                // EstimatedCost resolves the correct JudgeCostMap rate and provenance is audit-grade.
                // The positional label lives in the eval key ("-j{n}").
                Eval: BuildAtomic(
                    article, scenario,
                    entry.Judge,
                    judgeModel: _judgeModel,
                    promptId: _systemPromptId,
                    key: $"{scenario.Id}-j{idx + 1}"),
                Weight: entry.Weight,
                Required: true))
            .ToList();

        return new MultiJudgeWrapper(
            key: scenario.Id,
            name: $"{article.Article} — {scenario.Id} (multi-judge)",
            category: $"compliance.{article.Pillar}",
            version: "1.0.0",
            judges: judgeComponents,
            aggregation: WeightedMedianAggregation.Instance);
    }

    private IEval BuildModeB(ArticleMetadata article, ScenarioSpec scenario)
    {
        var perCritComponents = scenario.EvaluationCriteria.Select((criterion, idx) =>
            new EvalComponent(
                Eval: new AtomicLlmEval(
                    evaluator: _judge,
                    key: $"{scenario.Id}-c{idx + 1}",
                    name: $"{article.Article} — {scenario.Id} — criterion {idx + 1}",
                    category: $"compliance.{article.Pillar}",
                    version: "1.0.0",
                    criteria: new[] { criterion },
                    passThreshold: article.PassThreshold,
                    judgeModel: _judgeModel,
                    promptId: _perCriterionPromptId,
                    failureSeverity: article.Severity),
                Weight: 1.0 / scenario.EvaluationCriteria.Count,
                Required: true))
            .ToList();

        return new CompositeEval(
            key: scenario.Id,
            name: $"{article.Article} — {scenario.Id} (Mode-B)",
            category: $"compliance.{article.Pillar}",
            version: "1.0.0",
            components: perCritComponents,
            aggregation: WeightedSumAggregation.Instance,
            threshold: article.PassThreshold);
    }

    private static AtomicLlmEval BuildAtomic(
        ArticleMetadata article,
        ScenarioSpec scenario,
        AgentEval.Core.IEvaluator evaluator,
        string? judgeModel,
        string promptId,
        string key) =>
        new AtomicLlmEval(
            evaluator: evaluator,
            key: key,
            name: $"{article.Article} — {key}",
            category: $"compliance.{article.Pillar}",
            version: "1.0.0",
            criteria: scenario.EvaluationCriteria,
            passThreshold: article.PassThreshold,
            judgeModel: judgeModel,
            promptId: promptId,
            failureSeverity: article.Severity);
}
