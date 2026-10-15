// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Memory.External.Models;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Distinct arm definitions bound to one selected source workload, independent of pair execution IDs and outcomes.</summary>
internal static class PairedMemorySuiteDefinition
{
    internal static SuiteDefinitionManifest Create(string preset, LongMemEvalPairedPlan pairing,
        LongMemEvalArm arm, ExternalBenchmarkPlan armPlan, string? judgeProvider)
    {
        if (!Enum.IsDefined(arm)
            || !pairing.Source.QuestionIds.SequenceEqual(armPlan.QuestionIds, StringComparer.Ordinal)
            || !JsonNode.DeepEquals(pairing.Source.Configuration, armPlan.Configuration)
            || (arm == LongMemEvalArm.Normal && pairing.Source.SelectedInputsDigest != armPlan.SelectedInputsDigest))
            throw new ArgumentException("Expected: a prepared arm of the selected paired workload. Actual: arm, roster, options or normal inputs differ. Suggestions: capture both arms from the paired runner's preparation callbacks. Because: reloading or resampling cannot establish pairing identity.");
        var role = arm == LongMemEvalArm.Normal ? "normal" : "oracle";
        var manifest = ExternalMemorySuiteDefinition.Create("longmemeval", preset + "/" + role, armPlan,
            pairing.JudgeModelId, judgeProvider).ToJson();
        manifest["definitionVersion"] = "paired-memory-definition-1";
        manifest["inputs"]!["pairedSource"] = pairing.Source.SelectedInputsDigest;
        manifest["definition"]!["paired"] = new JsonObject
        {
            ["role"] = role, ["executionOrder"] = "normal-before-oracle",
            ["oracleProjection"] = "gold-only",
            ["answerModel"] = JsonSerializer.SerializeToNode(arm == LongMemEvalArm.Normal
                ? pairing.NormalAnswerModel : pairing.OracleAnswerModel),
        };
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(manifest));
    }
}
