// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands.Targets;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Resolves an optional agent override for a <c>bench</c> subcommand from either the shared <c>--sut</c> seam
/// (Track 2 PR3 — <c>strategy/CopilotStudio/Bench-Eval-Integration-and-Live-Connector-Plan.md</c> §3.4 "bench
/// Tier 1") or a generic OpenAI-compatible <c>--endpoint</c>/<c>--model</c>/<c>--api-key</c> set (Part C —
/// <c>strategy/CLI-Custom-Benchmarks-CopilotStudio-OpenAI-and-Metrics-Remediation-Design.md</c> §2). Despite
/// the "Tier1" name (kept for history — it shipped for <c>bench owasp</c>/<c>mitre</c>/<c>nist</c> first), the
/// <see cref="Resolve"/> logic itself is verb/tier-agnostic and is also reused verbatim by <c>bench gdpr</c>/
/// <c>eu-ai-act</c> (Tier 2, §2.2 of <c>strategy/CopilotStudio/Remaining-Backlog-Implementation-Plan-2026-07-16.md</c>)
/// with <c>endpoint</c>/<c>model</c>/<c>apiKey</c> passed as <see langword="null"/> — a separate
/// <c>BenchTier2SutResolver</c> would have been byte-identical duplication. Pure/testable — kept out of
/// <c>Program.cs</c>'s top-level statements (which aren't a unit-testable surface) on purpose.
/// <c>BenchOwaspCommand</c>/<c>BenchMitreCommand</c>/<c>BenchNistCommand</c>/<c>BenchCommand</c>/
/// <c>BenchEuAiActCommand</c> are themselves untouched by this resolver: each already has its own
/// <c>agentOverride</c> seam that wins over <c>--azure-from-env</c>/the stub/the supplied response, so this
/// class only needs to decide WHAT (if anything) to pass as that override.
/// </summary>
internal static class BenchTier1SutResolver
{
    /// <summary>
    /// Resolves the agent override, if any. <c>--sut</c> wins when set (its own <see cref="ISutTarget.Validate"/>
    /// runs, surfaced here as a friendly error rather than an exception so the caller can print it and exit
    /// cleanly — mirroring every other bench command's "Error: ..." + exit 1 convention); otherwise a non-empty
    /// <paramref name="endpoint"/> builds a plain OpenAI-compatible agent. Neither set → <c>(null, null)</c>,
    /// letting the caller's existing <c>--azure-from-env</c>/stub fallback (inside <c>RunAsync</c>) run unchanged.
    /// </summary>
    /// <param name="sut">The parsed <c>--sut</c> value, or <see langword="null"/> if not set.</param>
    /// <param name="targetOptions">Every registered target's own bound options, keyed by <see cref="ISutTarget.Sut"/>.</param>
    /// <param name="targets">The built-in targets registered for this verb (<c>bench</c>).</param>
    /// <param name="endpoint">The parsed <c>--endpoint</c> value, or <see langword="null"/> if not set.</param>
    /// <param name="model">The parsed <c>--model</c> value (required when <paramref name="endpoint"/> is set).</param>
    /// <param name="apiKey">The parsed <c>--api-key</c> value, or <see langword="null"/> to fall back to <c>OPENAI_API_KEY</c>.</param>
    /// <param name="subject">The bench subject name, used as the built agent's display name.</param>
    /// <param name="sutOverride">
    /// The credential-free test seam (mirrors <c>RedTeamCommand</c>/<c>EvalCommand</c>): forwarded into a
    /// selected <c>--sut</c> target's <see cref="ISutTarget.Build"/> call. <c>Program.cs</c> never passes this
    /// (always <see langword="null"/> in production); tests drive the real <c>CopilotStudioRedTeamTarget</c>
    /// end to end with a benign fake agent instead of a fake <see cref="ISutTarget"/>.
    /// </param>
    public static (IEvaluableAgent? Agent, string? Error) Resolve(
        string? sut,
        IReadOnlyDictionary<string, ISutTargetOptions?> targetOptions,
        IReadOnlyList<ISutTarget> targets,
        string? endpoint,
        string? model,
        string? apiKey,
        string subject,
        IEvaluableAgent? sutOverride = null)
    {
        if (sut is not null)
        {
            var common = new CommonTargetOptions { Sut = sut, TargetOptions = targetOptions };
            try
            {
                var (sutAgent, _) = SutTargetResolver.TryResolve(common, targets, sutOverride);
                return (sutAgent, null);
            }
            catch (InvalidOperationException ex)
            {
                return (null, ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return (null, "--model is required when using --endpoint.");
            }

            return (BuildFromOpenAiEndpoint(endpoint, model, apiKey, subject), null);
        }

        return (null, null);
    }

    /// <summary>
    /// A 3-line wrapper around the same <see cref="EndpointFactory.CreateOpenAICompatible"/> +
    /// <c>.AsEvaluableAgent(...)</c> pattern <c>EvalCommand</c> already uses — genuinely mechanical, no new
    /// abstraction (Part C of the design doc).
    /// </summary>
    internal static IEvaluableAgent BuildFromOpenAiEndpoint(string endpoint, string model, string? apiKey, string subject)
        => EndpointFactory.CreateOpenAICompatible(endpoint, model, apiKey).AsEvaluableAgent(name: subject);
}
