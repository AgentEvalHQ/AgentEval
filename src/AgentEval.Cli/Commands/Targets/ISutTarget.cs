// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using AgentEval.Core;

namespace AgentEval.Cli.Commands.Targets;

/// <summary>
/// Marker for a built-in SUT target's parsed CLI options under the shared <c>--sut</c> seam — the
/// non-redteam sibling of <c>AgentEval.Cli.Commands.RedTeamTargets.IRedTeamTargetOptions</c>. Deliberately
/// a DISTINCT type (not shared with the redteam interface) so `eval`/`bench`'s option shapes never
/// implicitly couple to redteam's.
/// </summary>
internal interface ISutTargetOptions { }

/// <summary>
/// A built-in system-under-test reachable from MULTIPLE verbs (<c>eval</c>, <c>bench</c>) via a shared
/// <c>--sut</c> flag — Track 2 of the Copilot Studio integration plan
/// (<c>strategy/CopilotStudio/Bench-Eval-Integration-and-Live-Connector-Plan.md</c> §3). Generalizes the
/// already-shipped <c>redteam --sut</c> pattern WITHOUT touching
/// <c>AgentEval.Cli.Commands.RedTeamTargets.IRedTeamBuiltInTarget</c>, <c>RedTeamOptions</c>, or
/// <c>RedTeamCommand.cs</c> — a target that wants BOTH surfaces implements both interfaces via EXPLICIT
/// interface implementation (the same C# idiom <c>IEnumerable</c>/<c>IEnumerable&lt;T&gt;</c> use), so the
/// two `Validate`/`Build` method bodies stay independently correct for each verb's option shape.
/// </summary>
/// <remarks>
/// <b>Deliberate scope decision (matches the design doc):</b> <c>gatekeeper-demo</c> is NOT part of this
/// interface — it stays <c>redteam</c>-only, because it requires an <c>AgentTrace</c> for its whole
/// point (recording <c>gate.tool.*</c> evidence), which `eval`/`bench` have no use for. Only
/// <c>copilot-studio</c> gets the shared treatment.
/// </remarks>
internal interface ISutTarget
{
    /// <summary>The <c>--sut</c> value that selects this target.</summary>
    string Sut { get; }

    /// <summary>Which verbs currently know how to drive this target — a target registers ONCE, not once per verb's own hand-maintained list.</summary>
    IReadOnlySet<string> SupportedVerbs { get; }

    /// <summary>Register this target's own CLI options on <paramref name="command"/>. No-op if the target has none.</summary>
    void AddOptionsTo(Command command);

    /// <summary>Bind THIS target's flags from the parse result; <c>null</c> if the target has no flags.</summary>
    ISutTargetOptions? BindOptions(ParseResult parseResult);

    /// <summary>Validate the flags for THIS target under THIS verb; throw <see cref="InvalidOperationException"/> on a bad combination (before any network call).</summary>
    void Validate(CommonTargetOptions common, ISutTargetOptions? own);

    /// <summary>The report-facing SUT name.</summary>
    string ResolvedName(CommonTargetOptions common, ISutTargetOptions? own);

    /// <summary>Build the system-under-test. <paramref name="sutOverride"/>, if non-null, is returned as-is (the credential-free test seam).</summary>
    IEvaluableAgent Build(CommonTargetOptions common, ISutTargetOptions? own, IEvaluableAgent? sutOverride);
}

/// <summary>
/// The subset of CLI options common across every verb that adopts the shared <c>--sut</c> seam — deliberately
/// a SEPARATE type from redteam's <c>RedTeamOptions</c> (which carries many redteam-only fields like
/// <c>Intensity</c>/<c>Parallelism</c>/judge-and-attacker config that `eval`/`bench` don't have).
/// </summary>
internal sealed record CommonTargetOptions
{
    public string? Endpoint { get; init; }
    public bool Azure { get; init; }
    public string? Model { get; init; }
    public string? DeploymentName { get; init; }
    public string? ApiKey { get; init; }
    public string? Sut { get; init; }
    public IReadOnlyDictionary<string, ISutTargetOptions?> TargetOptions { get; init; }
        = new Dictionary<string, ISutTargetOptions?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads back this target's own options (case-insensitive lookup by <see cref="ISutTarget.Sut"/>), or <c>null</c> if absent/wrong type.</summary>
    public T? TargetOptionsFor<T>(string sut) where T : class, ISutTargetOptions
    {
        foreach (var (key, value) in TargetOptions)
        {
            if (string.Equals(key, sut, StringComparison.OrdinalIgnoreCase))
            {
                return value as T;
            }
        }

        return null;
    }
}

/// <summary>
/// Resolves a <c>--sut</c> value to a live <see cref="IEvaluableAgent"/> for whichever verb calls it —
/// the single source of truth so `eval`/`bench` never duplicate this dispatch logic. Mirrors
/// <c>RedTeamCommand.BuildBuiltInTargets()</c>'s "fresh instances per call" convention: option-holding
/// targets must not be shared across command builds (a target may memoize a loaded config across its own
/// <c>Validate</c>/<c>ResolvedName</c>/<c>Build</c> calls within ONE command execution — see
/// <c>CopilotStudioRedTeamTarget</c> — so reusing an instance across TWO command executions would leak
/// stale memoized state).
/// </summary>
internal static class SutTargetResolver
{
    /// <summary>The built-in targets available under this shared seam. Only `copilot-studio` today (see <see cref="ISutTarget"/>'s scope note).</summary>
    internal static IReadOnlyList<ISutTarget> BuiltInTargets() =>
        [new global::AgentEval.Cli.CopilotStudio.CopilotStudioRedTeamTarget()];

    /// <summary>Registers <c>--sut</c> plus every applicable target's own options on <paramref name="command"/>, filtered to targets that support <paramref name="verb"/>.</summary>
    internal static (Option<string?> SutOption, IReadOnlyList<ISutTarget> Targets) AddOptionsTo(Command command, string verb)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(verb);

        var targets = BuiltInTargets().Where(t => t.SupportedVerbs.Contains(verb)).ToList();
        var sutOpt = new Option<string?>("--sut")
        {
            Description = $"Built-in system-under-test for this verb. Valid: {(targets.Count > 0 ? string.Join(", ", targets.Select(t => t.Sut)) : "(none registered for this verb)")}.",
        };
        command.Options.Add(sutOpt);
        foreach (var t in targets)
        {
            t.AddOptionsTo(command);
        }

        return (sutOpt, targets);
    }

    /// <summary>
    /// Resolves <paramref name="opts"/>.<c>Sut</c> to a live agent, or <c>(null, null)</c> when no
    /// <c>--sut</c> was supplied (the caller falls back to its own <c>--endpoint</c>/<c>--azure</c> path).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="opts"/>.<c>Sut</c> names a target not present in <paramref name="targets"/> (either
    /// unknown entirely, or not registered for the current verb).
    /// </exception>
    internal static (IEvaluableAgent? Agent, string? ResolvedName) TryResolve(
        CommonTargetOptions opts, IReadOnlyList<ISutTarget> targets, IEvaluableAgent? sutOverride)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(targets);

        if (opts.Sut is null)
        {
            return (null, null);
        }

        var target = targets.FirstOrDefault(t => string.Equals(t.Sut, opts.Sut.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Unknown --sut value: '{opts.Sut}'. Valid for this verb: {string.Join(", ", targets.Select(t => t.Sut))}.");

        var own = opts.TargetOptions.TryGetValue(target.Sut, out var o) ? o : null;
        target.Validate(opts, own);
        return (target.Build(opts, own, sutOverride), target.ResolvedName(opts, own));
    }
}
