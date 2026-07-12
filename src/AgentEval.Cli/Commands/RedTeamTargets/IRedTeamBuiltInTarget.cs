// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Cli.Commands.RedTeamTargets;

/// <summary>
/// Marker for a built-in target's parsed CLI options — the object a target's <see cref="IRedTeamBuiltInTarget.BindOptions"/>
/// returns and RedTeamCommand stores on <see cref="RedTeamOptions.TargetOptions"/>, keyed by
/// <see cref="IRedTeamBuiltInTarget.Sut"/>. Lets the command carry every target's options polymorphically without
/// naming any one target's option type — so a new built-in target with its own flags adds no branch to the command.
/// </summary>
internal interface IRedTeamTargetOptions { }

/// <summary>
/// A built-in red-team system-under-test selected via <c>--sut</c> (e.g. <c>gatekeeper-demo</c>,
/// <c>copilot-studio</c>). Encapsulates one SUT's CLI options, validation, construction, and reporting policy so
/// <c>RedTeamCommand</c> stays orchestration-only (replace-conditional-with-polymorphism — no per-SUT branch grows the
/// command). The endpoint/<c>--azure</c> path is deliberately NOT a built-in target: it is the red-team-core fallback
/// (system-prompt-canary embedding + tool-harness tiering), so it stays in the command as the default.
/// </summary>
/// <remarks>
/// Implementations are cheap value objects. A single instance handles one <c>ExecuteAsync</c> call end to end, so
/// <see cref="Validate"/> may memoize an expensive lookup (e.g. a loaded config) for the following
/// <see cref="ResolvedName"/> / <see cref="Build"/> on the same instance.
/// </remarks>
internal interface IRedTeamBuiltInTarget
{
    /// <summary>The <c>--sut</c> value that selects this target.</summary>
    string Sut { get; }

    /// <summary>Register this target's own CLI options on the redteam command. No-op if the target has none.</summary>
    void AddOptionsTo(Command command);

    /// <summary>
    /// Bind THIS target's flags from the parse result into its strongly-typed options object; <c>null</c> if the target
    /// has no flags. RedTeamCommand calls this for every built-in target and stores the result on
    /// <see cref="RedTeamOptions.TargetOptions"/> under <see cref="Sut"/>, so the command never special-cases one
    /// target's binding. A target reads its own options back with <see cref="RedTeamOptions.TargetOptionsFor{T}"/>.
    /// </summary>
    IRedTeamTargetOptions? BindOptions(ParseResult parseResult);

    /// <summary>
    /// Validate the flags for THIS target; throw <see cref="InvalidOperationException"/> on a bad combination (before
    /// any network call). May cache a loaded config for the subsequent <see cref="ResolvedName"/> / <see cref="Build"/>.
    /// </summary>
    void Validate(RedTeamOptions opts);

    /// <summary>The report-facing SUT name — used in reports and as the judge/attacker model-name fallback.</summary>
    string ResolvedName(RedTeamOptions opts);

    /// <summary>
    /// Build the system-under-test. <paramref name="sutOverride"/>, if non-null, is returned as-is (the test seam — a
    /// pre-built agent drives the whole scan credential-free). <paramref name="trace"/> lets a gated target record
    /// <c>gate.*</c> evidence.
    /// </summary>
    IEvaluableAgent Build(RedTeamOptions opts, IEvaluableAgent? sutOverride, AgentTrace trace);

    /// <summary>
    /// Whether raw request/response evidence is included in the report. <c>false</c> for a live, PII-carrying target
    /// so a secret can never land in a report or CI log.
    /// </summary>
    bool IncludeEvidence { get; }

    /// <summary>Optional post-scan summary line(s) written to <paramref name="err"/> (e.g. a gate-block count).</summary>
    void WritePostScanSummary(RedTeamResult result, AgentTrace trace, TextWriter err);
}
