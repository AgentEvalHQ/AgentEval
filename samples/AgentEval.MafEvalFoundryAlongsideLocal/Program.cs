// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// Hybrid eval sample — score ONE MAF agent run with an AgentEval Composite Eval AND Azure AI Foundry
// evals, then render BOTH in one source-tagged HTML report. Two patterns:
//   [A] MAF's native multi-evaluator mix: agent.EvaluateAsync(queries, IAgentEvaluator[])  (sequential)
//   [B] CompositeAgentEvaluator: concurrent + per-source isolation + bounded Foundry timeout (recommended)
//
// Env: AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT   (the judge + the fallback SUT agent)
//      FOUNDRY_PROJECT_ENDPOINT / FOUNDRY_MODEL          (Foundry evals + the Foundry-hosted SUT)
// If FOUNDRY_PROJECT_ENDPOINT is unset, the sample runs AgentEval-local-only (Foundry branch skipped).

extern alias azureidentity;         // disambiguate DefaultAzureCredential (also in Azure.Core via the Foundry beta)

using Azure;                        // AzureKeyCredential
using Azure.AI.OpenAI;              // AzureOpenAIClient
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

using AgentEval.Benchmarks;                 // AgenticBenchmark
using AgentEval.Core;                        // ChatClientEvaluator
using AgentEval.Core.Evals.Rendering;        // HtmlEvalResultRenderer, EvalResultRenderOptions
using AgentEval.Evals;                        // EvalResult
using AgentEval.Evals.Meta;                   // ChanceFloor — the 7.2 root declaration
using AgentEval.Output;                       // SubjectIdentity, SubjectKind
using AgentEval.MAF.Evaluators;               // .AsAgentEvaluator / .AsMeaiEvaluator / UnifiedEvalReport / CompositeAgentEvaluator

using FoundryEvals = Microsoft.Agents.AI.Foundry.FoundryEvals;
using AgentEval.MafEvalFoundryAlongsideLocal;   // this sample's own types (top-level statements are global)

// --selftest: the admitted leaf's invariants, offline. It runs BEFORE the first environment read
// because CreateJudgeChatClient() THROWS when AZURE_OPENAI_* is unset — without this entry point
// nothing in this sample can be exercised unconfigured.
if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
    return await ThreeDayItinerarySelfTest.RunAsync();

// ─────────────────────────────────────────────────────────────────────────────
// 1. Setup — SUT agent, Foundry project, and the AgentEval judge
// ─────────────────────────────────────────────────────────────────────────────
string? foundryEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
string model = Environment.GetEnvironmentVariable("FOUNDRY_MODEL") ?? "gpt-4o-mini";
bool foundryAvailable = !string.IsNullOrWhiteSpace(foundryEndpoint);

// The Foundry project client doubles as the SUT-agent host AND the Foundry evals backend.
AIProjectClient? projectClient = foundryAvailable
    ? new AIProjectClient(new Uri(foundryEndpoint!), new azureidentity::Azure.Identity.DefaultAzureCredential())
    : null;

// System-under-test agent. When Foundry is configured we host it there; otherwise fall back to an
// Azure OpenAI IChatClient-backed agent so the local half still runs.
AIAgent agent = projectClient is not null
    ? projectClient.AsAIAgent(model: model, instructions: "You are a helpful travel advisor.", name: "TravelAdvisor")
    : CreateFallbackAzureOpenAIAgent();

// The AgentEval judge (LLM-as-judge for the Composite Eval). Any IChatClient works.
IChatClient judge = CreateJudgeChatClient();
var chatConfig = new ChatConfiguration(judge);

string[] queries =
{
    "Plan a 3-day trip to Kyoto for a family with two kids.",
    "What's the cheapest way to get from Paris to London next week?",
};

// ─────────────────────────────────────────────────────────────────────────────
// 2. Build the two evaluators
// ─────────────────────────────────────────────────────────────────────────────

// AgentEval Composite Eval — the multi-dimension, deterministic (temp 0), evidence-bearing grader.
// AgenticExecution is AgentEval's agentic composite (its CapturedResults hold the full weighted tree).
var preset = AgenticBenchmark.AgenticExecution(new ChatClientEvaluator(judge), judgeModel: model);

// ── The admitted leaf ────────────────────────────────────────────────────────────────────────
// One DETERMINISTIC check, admitted through AgentEval's floor-gated door and dropped into the
// preset's component list. Two things this deliberately does NOT do:
//
//   · it does not admit the composite ROOT. FloorAdmittedEval refuses a result carrying
//     sub-results, because one floor stamped on a root would certify every floorless leaf beneath
//     it. The floor rides on the LEAF and travels up inside the captured tree.
//   · it does not read its floor back off its own output. The floor is supplied here, before
//     anything runs.
//
// ⚠ WEIGHT is stated, not defaulted. AgenticExecution declares six canonical weights summing to
// 1.00 (0.25 / 0.20 / 0.20 / 0.15 / 0.10 / 0.10); admitting a leaf at EvalComponent's default of
// 1.0 would make one boolean structure check outweigh all six judged dimensions combined and
// quietly change what the preset's number means. 0.10 matches its smallest declared dimension, so
// the preset keeps its shape. The composite is re-keyed and re-named all the same — a tree with an
// extra component is not the published preset.
var admittedLeaf = new EvalComponent(
    FloorAdmittedEval.Admit(new ThreeDayItineraryEval(), ThreeDayItineraryEval.DeclaredFloor),
    Weight: 0.10);

var localComposite = new CompositeEval(
    key: preset.Key + ".with_admitted_leaf",
    name: preset.Name + " + 1 admitted leaf",
    category: preset.Category,
    version: preset.Version,
    components: [.. preset.Components, admittedLeaf],
    aggregation: preset.Aggregation,
    threshold: preset.Threshold);

Console.WriteLine(
    $"composite: {preset.Components.Count} preset component(s) + 1 admitted leaf " +
    $"('{ThreeDayItineraryEval.EvalKey}', weight {admittedLeaf.Weight:0.00}, floor: " +
    $"{ThreeDayItineraryEval.DeclaredFloor.Kind}), threshold {preset.Threshold:0.00}");

var compositeEvaluator = localComposite.AsMeaiEvaluator(
    // 7.2 (Q6): the ROOT floor, DECLARED. Not derivable, and the reason is the finding — see
    //   AgentEvalCompositeEvaluator.DeclaredRootFloor. It is recorded beside the verdict and
    //   applied to nothing; the evaluator also reports how many LEAVES carry a floor, read off
    //   the tree that ran rather than off this declaration.
    ChanceFloor.NotDerivable(
        "this composite mixes LLM-judged dimensions with one deterministic leaf, and its root score is a "
    + "WEIGHTED SUM of the two. There is no draw model for that: an arm that understood nothing "
    + "would score whatever the judge happened to give it, which is not a quantity chance can be "
    + "asked about. The DETERMINISTIC leaf carries its own derivable-or-declared floor; the root "
    + "does not, and saying so is the point."));   // -> AgentEvalCompositeEvaluator (captures tree)

IAgentEvaluator local = compositeEvaluator.AsAgentEvaluator(chatConfig);    // conversation-preserving

// Foundry cloud evaluator — BOUNDED timeout (it's a polling cloud job; the default is 300s).
IAgentEvaluator? foundry = projectClient is not null
    ? new FoundryEvals(projectClient, model, splitter: null, pollIntervalSeconds: 5, timeoutSeconds: 120,
                       FoundryEvals.TaskAdherence, FoundryEvals.Relevance)
    : null;

const string reportTitle = "Hybrid eval — AgentEval Composite ⊕ Azure AI Foundry";
var subject = new SubjectIdentity(SubjectKind.Agent, agent.Name ?? "agent", ModelId: model, Framework: "MAF");
var renderOpts = new EvalResultRenderOptions(Subject: subject, Title: reportTitle);

// ─────────────────────────────────────────────────────────────────────────────
// 3a. PATTERN A — MAF-native "mix" (simplest). Sequential + all-or-nothing; fine for a reliable demo.
//     The agent runs ONCE; each evaluator scores the same outputs; one result per evaluator.
// ─────────────────────────────────────────────────────────────────────────────
if (foundry is not null)
{
    IReadOnlyList<AgentEvaluationResults> mixed = await agent.EvaluateAsync(
        queries, new IAgentEvaluator[] { local, foundry });

    foreach (var r in mixed)
        Console.WriteLine($"[A] {r.ProviderName}: {r.Passed}/{r.Total} passed" +
                          (r.ReportUrl is not null ? $"  (Foundry portal: {r.ReportUrl})" : ""));

    // UnifiedEvalReport takes a per-source list — zip the native-mix results with their source labels.
    var perSourceA = new (string Source, AgentEvaluationResults Result)[]
    {
        ("agenteval-local", mixed[0]),
        ("foundry", mixed[1]),
    };
    EvalResult unifiedA = UnifiedEvalReport.Build(perSourceA, compositeEvaluator, title: reportTitle);
    await File.WriteAllBytesAsync("report-mixed-A.html",
        await new HtmlEvalResultRenderer().RenderAsync(unifiedA, renderOpts));
}

// ─────────────────────────────────────────────────────────────────────────────
// 3b. PATTERN B — CompositeAgentEvaluator (recommended for the real hybrid).
//     Runs the inners CONCURRENTLY with per-source ISOLATION + a bounded Foundry timeout:
//       - local returns in ms while Foundry polls  -> wall-clock ≈ Foundry time, not local+Foundry
//       - a Foundry failure/timeout does NOT lose the local (Composite) results — it becomes a
//         visible "skipped" branch. That is the difference from Pattern A.
// ─────────────────────────────────────────────────────────────────────────────
var inners = new List<(string Source, IAgentEvaluator Evaluator, TimeSpan? Timeout)>
{
    ("agenteval-local", local, null),                               // local is fast; no ceiling needed
};
if (foundry is not null)
    inners.Add(("foundry", foundry, TimeSpan.FromSeconds(150)));    // hard ceiling ≥ FoundryEvals' own timeoutSeconds

var composite = new CompositeAgentEvaluator(inners, name: "Foundry ⊕ AgentEval");
AgentEvaluationResults hybrid = await agent.EvaluateAsync(queries, composite);   // ONE call, agent runs once

Console.WriteLine($"[B] merged {hybrid.Passed}/{hybrid.Total} passed across {composite.CapturedPerSource.Count} source(s)");

// The report consumes the per-source detail the composite captured (one code path with Pattern A).
EvalResult unifiedB = UnifiedEvalReport.Build(composite.CapturedPerSource, compositeEvaluator, title: reportTitle);
await File.WriteAllBytesAsync("report-hybrid-B.html",
    await new HtmlEvalResultRenderer().RenderAsync(unifiedB, renderOpts));

Console.WriteLine(foundryAvailable
    ? "Done — wrote report-hybrid-B.html with both AgentEval-local and Foundry branches."
    : "Done — AgentEval-local only (set FOUNDRY_PROJECT_ENDPOINT to include the Foundry branch).");

return 0;   // --selftest returns its own exit code above; a completed run is a success

// ─────────────────────────────────────────────────────────────────────────────
// Helpers (Azure OpenAI–backed judge + fallback SUT agent)
// ─────────────────────────────────────────────────────────────────────────────
static IChatClient CreateJudgeChatClient()
{
    var ep = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not set (judge model).");
    var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY not set.");
    var dep = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini";
    return new AzureOpenAIClient(new Uri(ep), new AzureKeyCredential(key)).GetChatClient(dep).AsIChatClient();
}

static AIAgent CreateFallbackAzureOpenAIAgent() =>
    CreateJudgeChatClient().AsAIAgent(name: "TravelAdvisor", instructions: "You are a helpful travel advisor.");
