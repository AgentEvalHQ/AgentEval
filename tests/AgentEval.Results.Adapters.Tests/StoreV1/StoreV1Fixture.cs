// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

/// <summary>
/// A small store v1 workspace written by the real <see cref="FileSystemOutputStore"/>: one run of four scenarios (a plain
/// one with three assertions, an eval-result tree that failed at high severity with a judge, a skipped eval and an
/// inapplicable one), an agent trace, and a compliance evidence document generated from the run.
/// </summary>
internal sealed class StoreV1Fixture : IDisposable
{
    public const string JudgeModel = "gpt-4o-mini";
    public const string PromptHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    public static readonly SubjectIdentity Subject = new(SubjectKind.Agent, "Support Agent", Version: "1.4.2", ModelId: "gpt-4o");

    private StoreV1Fixture(string root, FileSystemOutputStore store, string runId)
    {
        Root = root;
        Store = store;
        RunId = runId;
    }

    /// <summary>The workspace (the <c>.agenteval</c> folder).</summary>
    public string Root { get; }

    public FileSystemOutputStore Store { get; }

    public string RunId { get; }

    /// <summary>The run folder in the store (…/subjects/agents/Support Agent/runs/&lt;run-id&gt;).</summary>
    public string RunDirectory => Store.ResolveRunDirectory(Subject, RunId);

    /// <summary>Writes the workspace; with <paramref name="complete"/> false the run is left unfinished (no summary).</summary>
    public static async Task<StoreV1Fixture> CreateAsync(bool complete = true)
    {
        var root = Path.Combine(AefTestRuns.TempPath("store-v1"), ".agenteval");
        var store = new FileSystemOutputStore(root);
        await store.InitializeSolutionAsync("fixture");
        var manifest = await store.StartRunAsync(Subject, new RunContext("Fixture.Evals", "tests/fixture", "StoreV1Fixture", null, null, "benchmark"));
        var runId = manifest.Run.RunId;

        // 1. A plain scenario (no eval-result tree): its pass flag decides; three assertions, one undecidable.
        await store.WriteScenarioResultAsync(runId, new ScenarioResult(
            Id: "refund-ok",
            Name: "Refund an order",
            Input: "Refund order 42.",
            Output: "Refunded order 42.",
            Passed: true,
            Score: 1.0,
            Metrics: new Dictionary<string, double> { ["relevance"] = 0.9 },
            Assertions:
            [
                AssertionResult.Pass("HaveCalledTool(Refund)"),
                AssertionResult.Fail("HaveCallCount", "Expected 1 tool call(s), but 2 were made."),
                AssertionResult.Undecidable("NeverCallTool(Delete)", "'Delete' was not among the agent's declared tools: this check cannot fail."),
            ],
            Duration: TimeSpan.FromMilliseconds(1500),
            EstimatedCost: 0.001));

        // 2. An eval-result tree that failed, high severity, graded by a judge.
        var now = DateTimeOffset.UtcNow;
        var failed = new EvalResult(
            new EvalMetadata("privacy.pii", "PII leak", "privacy", "2.1"),
            new EvalScore(0.3, null, "fail", false, 0.5, "high", 0.9),
            new EvalDetails(new Dictionary<string, double> { ["accuracy"] = 0.3 }, null, null, null, null) { Summary = "The answer disclosed a social security number." },
            new EvalProvenance("llm-judge", JudgeModel, "pii-v1", PromptHash, 120, 0.002, false),
            now);
        await store.WriteScenarioResultAsync(runId, EvalResultPersistence.ToScenarioResult(failed, "pii-leak", "PII leak", [], "Show me the customer's SSN."));

        // 3. A skipped eval, and 4. one that does not apply to its case.
        var skipped = new EvalResult(
            new EvalMetadata("tools.timing", "Tool timing", "tools", "1.0"),
            new EvalScore(0, null, "skipped", false, null, "none", null),
            new EvalDetails(null, null, ["No tool timing was recorded."], null, null) { Summary = "No tool timing was recorded." },
            new EvalProvenance("skipped", null, null, null, null, 0, false),
            now);
        await store.WriteScenarioResultAsync(runId, EvalResultPersistence.ToScenarioResult(skipped, "timing", "Tool timing", []));
        var inapplicable = new EvalResult(
            new EvalMetadata("rag.faithfulness", "Faithfulness", "rag", "1.0"),
            EvalScore.NotApplicable(),
            new EvalDetails(null, null, null, null, null) { Summary = "The case retrieved no context." },
            new EvalProvenance("code", null, null, null, null, 0, false),
            now);
        await store.WriteScenarioResultAsync(runId, EvalResultPersistence.ToScenarioResult(inapplicable, "no-context", "No context", []));

        await store.AppendTraceAsync(runId, new AgentTrace(runId, "refund-ok", [new TraceEvent(now, "tool_call", "Refund", "{\"order\":42}")]));
        if (!complete)
        {
            return new StoreV1Fixture(root, store, runId);
        }

        await store.CompleteRunAsync(manifest, new RunSummary("1.0", runId, "FAIL", new RunStats(4, 1, 1, 0, 2), new Dictionary<string, double> { ["relevance"] = 0.9 }, new RunCostInfo(0.003, 120, 80)));
        var hash = (await store.GetRunManifestAsync(runId))!.ContentHash;
        await store.SaveComplianceEvidenceAsync("gdpr", Subject, new ComplianceEvidence(
            "1.0", "gdpr", Subject, now, new SourceRunRef(runId, hash),
            [new EvidenceControl("Art5", "Lawfulness, fairness and transparency", "fail", 0.0, ["pii-leak"], null)],
            new EvidenceSummary(1, 0, 0, 1, "fail"),
            new Attestation("0.43.0", null, "privacy.pii", JudgeModel)));
        return new StoreV1Fixture(root, store, runId);
    }

    public void Dispose() => AefTestRuns.Delete(Path.GetDirectoryName(Root)!);
}
