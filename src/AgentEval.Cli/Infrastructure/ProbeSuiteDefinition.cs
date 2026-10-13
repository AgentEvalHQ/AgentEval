// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.RedTeam;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Captures the materialized scan prepared by the CLI's attack factory; never regenerates probes.</summary>
internal static class ProbeSuiteDefinition
{
    internal static SuiteDefinitionManifest? Create(RedTeamOptions cli, ScanPlan plan, string resolvedModel)
    {
        var inputs = new JsonObject();
        var arms = new JsonArray();
        var ids = new List<string>();
        var index = 0;
        foreach (var arm in plan.Attacks)
        {
            // External library attacks may hide arbitrary runtime grader configuration. The CLI factory uses
            // shipped attacks/importers/transforms; do not classify arbitrary plugin instances as fully identified.
            if (arm.Attack.GetType().Assembly != typeof(RedTeamRunner).Assembly) return null;
            var probes = JsonSerializer.SerializeToNode(arm.Probes)!.AsArray();
            inputs["probes." + index++] = SuiteDefinitionManifest.Hash(AefJsonWriter.Document(new JsonObject { ["probes"] = probes }));
            ids.AddRange(arm.Probes.Select(p => arm.Control ? "benign/" + p.Id : arm.Attack.Name + "/" + p.Id));
            arms.Add(new JsonObject
            {
                ["name"] = arm.Attack.Name, ["type"] = arm.Attack.GetType().FullName,
                ["control"] = arm.Control, ["severity"] = arm.Attack.DefaultSeverity.ToString(),
                ["maxTurns"] = arm.Attack is IMultiTurnAttack multi ? multi.MaxTurns : 1,
                ["measurable"] = arm.Attack.NotMeasurableReason is null,
            });
        }
        if (ids.Count == 0) return null;
        foreach (var assembly in new[]
        {
            typeof(ProbeSuiteDefinition).Assembly, typeof(RedTeamRunner).Assembly,
            typeof(AgentEval.Evals.AtomicLlmEval).Assembly, typeof(AgentEval.Core.IEvaluableAgent).Assembly,
            typeof(AefProbeRunWriter).Assembly, typeof(SuiteDefinitionManifest).Assembly,
        }.Distinct())
            inputs["implementation." + assembly.GetName().Name] = SuiteDefinitionManifest.Hash(File.ReadAllBytes(assembly.Location));
        var policy = plan.Options;
        var definition = new JsonObject
        {
            ["arms"] = arms, ["intensity"] = policy.Intensity.ToString(),
            ["maxProbesPerAttack"] = policy.MaxProbesPerAttack, ["parallelism"] = policy.Parallelism,
            ["failFast"] = policy.FailFast, ["benignControls"] = policy.RunBenignControls,
            ["timeoutPerProbeTicks"] = policy.TimeoutPerProbe.Ticks, ["timeoutPerTurnTicks"] = policy.TimeoutPerTurn.Ticks,
            ["overallTimeoutTicks"] = policy.OverallTimeout?.Ticks, ["conversationLimitTicks"] = policy.MaxConversationDuration.Ticks,
            ["delayTicks"] = policy.DelayBetweenProbes.Ticks, ["explain"] = policy.ExplainFindings,
            ["judgeEnabled"] = policy.JudgeClient is not null, ["judgeMode"] = policy.Mode.ToString(),
            ["judgeRubric"] = policy.Rubric.ToString(), ["judgeTimeoutTicks"] = policy.JudgeTimeout?.Ticks,
            ["judgeModel"] = policy.JudgeClient is null ? null : cli.JudgeModel ?? resolvedModel,
            ["judgeProvider"] = policy.JudgeClient is null ? null : "openai-compatible",
            ["attackerModel"] = policy.AttackerClient is null ? null : cli.AttackerModel ?? resolvedModel,
            ["attackerProvider"] = policy.AttackerClient is null ? null : "openai-compatible",
        };
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "probe",
            ["suiteRef"] = AefEvalWriter.Ref("redteam", $"{cli.Attacks ?? "default"}/{cli.Intensity}"),
            ["definitionVersion"] = "probe-definition-1", ["frozen"] = policy.AttackerClient is null,
            ["inputs"] = inputs, ["definition"] = definition,
            ["caseIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        }));
    }
}
