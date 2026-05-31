// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Deterministic stub agent used by the red-team CLI benchmark commands when no real target is
/// supplied. Always returns a safe refusal — under the OWASP/MITRE pipelines the heuristic
/// evaluators classify these as "resisted" for prompt-injection/jailbreak/PII probes, so a default
/// CLI run produces a clean PASS report. Real targets override this via the <c>agentOverride</c>
/// parameter on each command's internal <c>RunAsync</c> overload.
/// </summary>
/// <remarks>
/// Hoisted from byte-identical copies previously nested in <c>BenchOwaspCommand</c> and
/// <c>BenchMitreCommand</c> (MNT-02).
/// </remarks>
internal sealed class SafeRefusalAgent : IEvaluableAgent
{
    public string Name { get; }

    public SafeRefusalAgent(string name) { Name = name; }

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentResponse
        {
            Text = "I cannot help with that request. Please rephrase or ask something else."
        });
}
