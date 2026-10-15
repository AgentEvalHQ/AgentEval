// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Benchmarks;
using AgentEval.Tracing;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Declares trace reconciliation policy and its planned checks, never observed trace contents.</summary>
internal static class TraceSuiteDefinition
{
    internal static SuiteDefinitionManifest Create(string preset, SamplePreset resolvedPreset,
        IReadOnlyList<string>? workflowExecutors = null)
    {
        var workflow = workflowExecutors is not null;
        var family = workflow ? "workflow-trace-fidelity" : "trace-fidelity";
        var cases = workflow
            ? workflowExecutors!.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => "workflow_trace_fidelity.executor." + id).ToArray()
            : TraceFidelityRubric.Classes.Select(key => "trace_fidelity." + key).ToArray();
        // The existing adapter exports the root itself when an empty workflow has no executor leaves.
        if (cases.Length == 0) cases = ["workflow_trace_fidelity"];
        var implementations = new JsonObject();
        foreach (var type in new[] { typeof(TraceSuiteDefinition), typeof(TraceFidelityRunner),
            typeof(AgentEval.Evals.IEval), typeof(SuiteDefinitionManifest), typeof(AgentEval.Results.Adapters.Native.AefCompositeRunWriter) })
        {
            var assembly = type.Assembly;
            implementations["implementation." + assembly.GetName().Name] = SuiteDefinitionManifest.Hash(File.ReadAllBytes(assembly.Location));
        }
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:" + family + "/" + preset,
            ["definitionVersion"] = "trace-reconciliation-1", ["frozen"] = true, ["inputs"] = implementations,
            ["caseIds"] = new JsonArray(cases.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["definition"] = new JsonObject
            {
                ["producer"] = family, ["preset"] = resolvedPreset.ToString(),
                ["tokenToleranceFraction"] = TraceFidelityRubric.TokenToleranceFraction,
                ["policyImplementation"] = (workflow ? typeof(WorkflowTraceFidelityReconciler) : typeof(TraceFidelityRunner)).FullName,
                ["inputRole"] = "observed-traces", ["trialsPerCase"] = 1,
            },
        }));
    }
}
