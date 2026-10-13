// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Memory.External.Models;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Binds materialized external-memory selections without reloading or sampling a second time.</summary>
internal static class ExternalMemorySuiteDefinition
{
    internal static SuiteDefinitionManifest Create(string family, string preset, ExternalBenchmarkPlan plan,
        string? judgeModel, string? judgeProvider)
    {
        var inputs = new JsonObject { ["selectedQuestions"] = plan.SelectedInputsDigest };
        if (plan.CorpusDigest is not null) inputs["corpus"] = plan.CorpusDigest;
        foreach (var type in new[] { typeof(ExternalMemorySuiteDefinition), typeof(ExternalBenchmarkPlan),
            typeof(AgentEval.Evals.CompositeEval), typeof(AgentEval.Evals.IEval),
            typeof(SuiteDefinitionManifest), typeof(AgentEval.Results.Adapters.Native.AefCompositeRunWriter) })
            inputs["implementation." + type.Assembly.GetName().Name] = SuiteDefinitionManifest.Hash(File.ReadAllBytes(type.Assembly.Location));
        var cases = plan.QuestionIds.Count == 0 ? new[] { family + "." + preset.ToLowerInvariant() } : plan.QuestionIds;
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:" + family + "/" + preset,
            ["definitionVersion"] = "external-memory-definition-1", ["frozen"] = true, ["inputs"] = inputs,
            ["caseIds"] = new JsonArray(cases.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["definition"] = new JsonObject
            {
                ["producer"] = family, ["preset"] = preset, ["configuration"] = plan.Configuration,
                ["judgeModel"] = judgeModel, ["judgeProvider"] = judgeProvider,
                ["selection"] = "materialized-before-execution", ["questionCount"] = plan.QuestionIds.Count,
                ["trialsPerCase"] = 1,
            },
        }));
    }
}
