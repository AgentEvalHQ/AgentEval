// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Single source of truth for resolving the <see cref="IEvaluator"/> used by
/// <c>agenteval bench …</c> and <c>agenteval bench … calibrate</c>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the per-command StubEvaluator-only wiring that the pass-2 Opus
/// review flagged: every bench entry point used to assign
/// <c>judge = new StubEvaluator()</c> unconditionally, even when AZURE_OPENAI_*
/// secrets were configured. CI pipelines that passed the secrets but received
/// a deterministic 75/100 stub verdict made the calibration gates dead weight.
/// </para>
/// <para>
/// Resolution order:
/// <list type="number">
///   <item><c>evaluatorOverride</c> (used by tests) — passed straight through.</item>
///   <item>If all three of AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY +
///         AZURE_OPENAI_DEPLOYMENT are set → build a real Azure OpenAI
///         <c>IChatClient</c> and wrap it in
///         <see cref="ChatClientEvaluator"/>.</item>
///   <item>Otherwise, allow <c>AGENTEVAL_ALLOW_STUB_JUDGE=1</c> opt-in to fall
///         back to <see cref="StubEvaluator"/>; without the opt-in, fail with
///         exit code 2 so CI cannot silently produce stub-graded evidence.</item>
/// </list>
/// </para>
/// </remarks>
internal static class JudgeFactory
{
    /// <summary>
    /// Resolves the judge to use for a bench run.
    /// </summary>
    /// <param name="evaluatorOverride">Optional override used by tests; bypasses env-var resolution.</param>
    /// <param name="judgeKind">Diagnostic label written into provenance (e.g. <c>"calibration"</c>, <c>"benchmark"</c>); only used for the error messages.</param>
    /// <param name="systemPrompt">
    /// Optional system prompt to wire into <see cref="ChatClientEvaluator"/>. Phase-6 Task 6.8:
    /// the GDPR / EU AI Act bench paths load their embedded judge prompts and pass them here
    /// so the LLM is actually steered by the "Cite articles / Be conservative / Flag evasive
    /// responses" rules. <c>null</c> (the default) preserves the prior behaviour of using
    /// <see cref="ChatClientEvaluator"/>'s built-in default system prompt.
    /// </param>
    /// <returns>
    /// <c>(judge, judgeModel, exitCode)</c>. When <c>judge</c> is <c>null</c>, the caller
    /// MUST return <c>exitCode</c> immediately (the helper already wrote the user-facing
    /// error to <see cref="Console.Error"/>). <c>judgeModel</c> is the deployment / "stub"
    /// label to record in provenance.
    /// </returns>
    internal static (IEvaluator? Judge, string JudgeModel, int ExitCode) Resolve(
        IEvaluator? evaluatorOverride,
        string judgeKind = "benchmark",
        string? systemPrompt = null)
    {
        if (evaluatorOverride is not null)
            return (evaluatorOverride, "override", 0);

        // Judge-specific creds take precedence over the generic AZURE_OPENAI_* set so the judge and
        // the agent-under-test can point at DIFFERENT endpoints in the same run — e.g. when
        // AZURE_OPENAI_* drives the SUT agent while AZURE_OPENAI_JUDGE_* points the grader at a
        // separate, capable judge model. When the JUDGE_* vars are unset this falls back to
        // AZURE_OPENAI_* (unchanged single-endpoint behaviour).
        var endpoint   = FirstSet("AZURE_OPENAI_JUDGE_ENDPOINT",   "AZURE_OPENAI_ENDPOINT");
        var apiKey     = FirstSet("AZURE_OPENAI_JUDGE_API_KEY",    "AZURE_OPENAI_API_KEY");
        var deployment = FirstSet("AZURE_OPENAI_JUDGE_DEPLOYMENT", "AZURE_OPENAI_DEPLOYMENT");

        // All three variables required for real Azure OpenAI judging.
        var allConfigured =
               !string.IsNullOrWhiteSpace(endpoint)
            && !string.IsNullOrWhiteSpace(apiKey)
            && !string.IsNullOrWhiteSpace(deployment);

        if (allConfigured)
        {
            try
            {
                var azureClient = new AzureOpenAIClient(new Uri(endpoint!), new AzureKeyCredential(apiKey!));
                IChatClient chatClient = azureClient.GetChatClient(deployment!).AsIChatClient();
                IEvaluator real = new ChatClientEvaluator(chatClient, systemPrompt);
                Console.Error.WriteLine(
                    $"✔ Azure OpenAI judge configured — endpoint={endpoint}, deployment={deployment} ({judgeKind})" +
                    (systemPrompt is null ? "." : $" [system prompt: {systemPrompt.Length} chars]."));
                return (real, deployment!, 0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"✖ Failed to construct Azure OpenAI judge: {ex.Message}\n" +
                    "  Check AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT values.");
                return (null, "", 2);
            }
        }

        // Partial config is almost always a misconfiguration; surface it explicitly
        // so CI doesn't silently fall through to the stub.
        var anyConfigured =
               !string.IsNullOrWhiteSpace(endpoint)
            || !string.IsNullOrWhiteSpace(apiKey)
            || !string.IsNullOrWhiteSpace(deployment);

        if (anyConfigured)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(endpoint))   missing.Add("AZURE_OPENAI_ENDPOINT");
            if (string.IsNullOrWhiteSpace(apiKey))     missing.Add("AZURE_OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(deployment)) missing.Add("AZURE_OPENAI_DEPLOYMENT");
            Console.Error.WriteLine(
                $"✖ Azure OpenAI judge partially configured — missing: {string.Join(", ", missing)}.\n" +
                "  Set all three of AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT,\n" +
                "  or unset all three and use AGENTEVAL_ALLOW_STUB_JUDGE=1 for stub mode.");
            return (null, "", 2);
        }

        // No real-judge config — gate the stub behind an explicit opt-in so CI
        // cannot silently produce stub-graded evidence.
        var allowStub = Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE");
        var stubAllowed =
               string.Equals(allowStub, "1", StringComparison.Ordinal)
            || string.Equals(allowStub, "true", StringComparison.OrdinalIgnoreCase);

        if (!stubAllowed)
        {
            Console.Error.WriteLine(
                "✖ No LLM evaluator configured (AZURE_OPENAI_* unset).\n" +
                "  Set AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT\n" +
                "  to enable real judging, or set AGENTEVAL_ALLOW_STUB_JUDGE=1 to run with\n" +
                "  a deterministic stub (results are not meaningful — CI must NOT do this).");
            return (null, "", 2);
        }

        Console.Error.WriteLine(
            $"⚠ AGENTEVAL_ALLOW_STUB_JUDGE=1 — using stub evaluator for {judgeKind}. " +
            "Results are not a real judgement; do not rely on the verdict in CI.");
        return (new StubEvaluator(), "stub", 0);
    }

    /// <summary>Returns the value of the first environment variable in <paramref name="names"/>
    /// that is set to a non-whitespace value, or null if none are.</summary>
    private static string? FirstSet(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    /// <summary>
    /// Deterministic placeholder evaluator. Returns score=75 with every criterion
    /// "met" so the pipeline produces a complete (but meaningless) evidence file
    /// for smoke-testing the workflow without LLM cost.
    /// </summary>
    /// <remarks>
    /// Only reachable when <c>AGENTEVAL_ALLOW_STUB_JUDGE=1</c> is opted-in
    /// explicitly. The "Stub: assumed met" explanation is intentionally obvious
    /// in artefacts so consumers can spot stub-graded evidence at a glance.
    /// </remarks>
    internal sealed class StubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var criteriaList = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 75,
                Summary = "Stub evaluation — no real LLM judge configured.",
                CriteriaResults = criteriaList
                    .Select(c => new CriterionResult
                    {
                        Criterion = c,
                        Met = true,
                        Explanation = "Stub: assumed met."
                    })
                    .ToList()
            });
        }
    }
}
