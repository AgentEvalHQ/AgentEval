// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// An <see cref="IAttackType"/> that wants its probes run with canary tools advertised (Wave B) — e.g.
/// ExcessiveAgency or IndirectInjection. When the SUT is an <see cref="IToolCapableAgent"/>, the runner invokes
/// <c>InvokeWithToolsAsync(prompt, GetCanaryTools(...))</c> instead of the plain text path; for a text-only SUT the
/// attack degrades to its verbal probes automatically (the canary tools are simply not advertised).
/// </summary>
public interface IToolAwareAttack : IAttackType
{
    /// <summary>
    /// The canary tools to advertise for this attack's probes (the forbidden functions a probe tries to lure the
    /// agent into calling, plus any benign "source" tools that deliver injected content for Pillar 4). MUST be pure
    /// and thread-safe: the runner may call it concurrently (once per probe) from multiple worker threads under
    /// <c>Parallelism &gt; 1</c>, so return fresh instances and read no shared mutable state.
    /// </summary>
    IReadOnlyList<CanaryTool> GetCanaryTools(Intensity intensity);
}
