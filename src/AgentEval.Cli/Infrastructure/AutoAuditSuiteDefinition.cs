// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Benchmarks;
using AgentEval.Tracing;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Declares the fixed auto-audit instrument independently of endpoint observations.</summary>
internal static class AutoAuditSuiteDefinition
{
    internal static SuiteDefinitionManifest Create(bool scripted)
    {
        var inputs = new JsonObject();
        foreach (var type in new[] { typeof(AutoAuditSuiteDefinition), typeof(AutoAuditLive),
            typeof(AgentEval.Evals.IEval), typeof(Microsoft.Extensions.AI.FunctionInvokingChatClient),
            typeof(Microsoft.Extensions.AI.ChatOptions), typeof(SuiteDefinitionManifest),
            typeof(AgentEval.Results.Adapters.Native.AefCompositeRunWriter) })
            inputs["implementation." + type.Assembly.GetName().Name] = SuiteDefinitionManifest.Hash(File.ReadAllBytes(type.Assembly.Location));
        if (!scripted)
        {
            inputs["instructions"] = SuiteDefinitionManifest.Hash(Encoding.UTF8.GetBytes(AutoAuditLive.Instructions));
            inputs["request"] = SuiteDefinitionManifest.Hash(Encoding.UTF8.GetBytes(AutoAuditLive.Request));
        }
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:autoaudit/support-task",
            ["definitionVersion"] = "autoaudit-definition-1", ["frozen"] = true, ["inputs"] = inputs,
            ["caseIds"] = new JsonArray("fidelity", "safety"),
            ["definition"] = new JsonObject
            {
                ["producer"] = "autoaudit", ["scenario"] = scripted ? "scripted-showcase" : "support-task",
                ["scenarioImplementation"] = (scripted ? typeof(AutoAuditDemo) : typeof(AutoAuditLive)).FullName,
                ["verdictImplementation"] = typeof(AutoAuditRunner).FullName,
                ["tracePreset"] = SamplePreset.AuditGrade.ToString(),
                ["maxToolIterations"] = scripted ? null : AutoAuditLive.MaxToolIterations,
                ["tokenToleranceFraction"] = TraceFidelityRubric.TokenToleranceFraction,
                ["gatePolicy"] = "Redact", ["gate"] = typeof(AgentEval.Guardrails.Gates.RegexPiiGate).FullName,
                ["trialsPerCase"] = 1, ["verdictPolicy"] = "scored-without-pass-mark;incomplete-is-error",
            },
        }));
    }
}
