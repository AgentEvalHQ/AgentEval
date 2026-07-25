// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, P0-2 + P0-5 + P0-6) — the safe composite builder. Installs run-scope, tool gates,
/// approval interop, and tracing together, in the correct order, in one call — replacing the pattern where a
/// developer manually chains <c>UseAgentEvalGate()</c> → <c>UseAgentEvalToolGate(...)</c> →
/// <c>UseAgentEvalToolApproval(...)</c> → <c>UseAgentEvalShadowJudge(...)</c> themselves and can get the order
/// wrong (confirmed real: <c>docs/gatekeeper/examples.md</c> carries an explicit comment explaining why
/// <c>UseAgentEvalGate()</c> must come first).
/// <para><b>No implicit enforcement default (P0-2).</b> <see cref="GatekeeperEnforcement"/> is a required
/// parameter, not a default — the previous, still-supported low-level <c>UseAgentEvalToolGate</c>/
/// <c>UseAgentEvalGate</c> silently defaulted to WarnOnly, so a gate's <c>Block</c> verdict was, by default,
/// only logged. There is no equivalent silent path here.</para>
/// <para><b>Composition order:</b> run gate (establishes <see cref="AgentRunScope"/>, runs pre/post chat
/// gates) → tool gate (blocks/mutates live tool calls) → approval interop (escalates borderline calls to a
/// human) → shadow judge (off-hot-path async judgement). Matches the order every runnable Gatekeeper sample in
/// <c>docs/gatekeeper/examples.md</c> already uses.</para>
/// </summary>
public static class AgentEvalGatekeeperExtensions
{
    /// <summary>
    /// The one-call Gatekeeper composite: run-scope + tool gates + approval interop + shadow judge, correctly
    /// ordered, sharing one trace. See <see cref="GatekeeperOptions"/> for what you can configure.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="enforcement">
    /// REQUIRED — how a gate's <c>Block</c> finding is enforced across every mechanism this builder composes.
    /// There is no default; see <see cref="GatekeeperEnforcement"/>.
    /// </param>
    /// <param name="configure">Populates a <see cref="GatekeeperOptions"/> — gates, trace, telemetry, shadow-judge pump, coverage check.</param>
    public static AIAgentBuilder UseGatekeeper(
        this AIAgentBuilder builder,
        GatekeeperEnforcement enforcement,
        Action<GatekeeperOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new GatekeeperOptions();
        configure(options);

        // Next-wave item: prompt-template drift, construction-time-only (a template doesn't change
        // mid-run, so this needs no runtime seam at all — see PromptTemplateDriftException remarks).
        // Opt-in: both PromptTemplates and PromptTemplateBaseline must be set together. Checked FIRST,
        // before any other validation below, for the same fail-fast-before-any-mutation reasoning as the
        // rest of this method — a tampered prompt template is the most fundamental thing to refuse to
        // build on. Setting exactly ONE of the two throws rather than silently no-op-ing — same
        // fail-LOUD-on-half-configuration discipline as RefuseUnprotectedHighRiskTools + missing
        // KnownTools below: a caller who sets PromptTemplates but forgets PromptTemplateBaseline (or vice
        // versa) almost certainly meant to enable the check, and a silent no-op would leave them believing
        // drift protection is active when it is not.
        if (options.PromptTemplates is null != options.PromptTemplateBaseline is null)
        {
            throw new InvalidOperationException(
                "UseGatekeeper: exactly one of GatekeeperOptions.PromptTemplates / PromptTemplateBaseline is " +
                "set — both must be set together for the prompt-template drift check to run (or neither, to " +
                "leave it disabled). Set the missing one, or clear the one you did set.");
        }

        if (options.PromptTemplates is not null && options.PromptTemplateBaseline is not null)
        {
            var driftFindings = PromptTemplateDriftGate.CheckDrift(options.PromptTemplates, options.PromptTemplateBaseline);
            var changed = driftFindings.Where(f => f.Kind == ManifestDriftKind.Changed).ToArray();
            if (changed.Length > 0)
            {
                throw new PromptTemplateDriftException(changed);
            }
        }

        // SkillGate Tier 1 (runtime governance) — the half-configuration VALIDATION stays here, same
        // fail-fast-before-any-mutation placement and fail-LOUD-on-half-configuration discipline as
        // PromptTemplates/PromptTemplateBaseline immediately above. Opt-in: both Skills and SkillBaselinePath
        // must be set together. The actual CheckAndEnforce call (which can WRITE to disk under
        // SkillGateMode.Bootstrap) is deliberately deferred to just before the first real mutation, below —
        // see that call site's own comment for why.
        if (options.Skills is null != options.SkillBaselinePath is null)
        {
            throw new InvalidOperationException(
                "UseGatekeeper: exactly one of GatekeeperOptions.Skills / SkillBaselinePath is set — both must " +
                "be set together for the SkillGate drift check to run (or neither, to leave it disabled). Set " +
                "the missing one, or clear the one you did set.");
        }

        // #2: compute the AUTHORITATIVE coverage report — against the SAME ToolGates snapshot that is actually
        // registered below (options.ToolGates.ToArray()), not a separately-tracked list a caller could pass to
        // GatekeeperCoverageAnalyzer.Analyze themselves and let drift. Populated whenever KnownTools is set,
        // regardless of RefuseUnprotectedHighRiskTools, so any caller can read options.CoverageReport after
        // this call returns instead of re-deriving it with their own (possibly stale) gate list.
        if (options.KnownTools is not null)
        {
            options.CoverageReport = GatekeeperCoverageAnalyzer.Analyze(options.KnownTools, options.ToolGates.ToArray(), options.CoverageAnalyzeOptions);
        }

        // P0-1 (bundled): refuse construction now, eagerly, if a high-risk tool has zero protecting gate.
        if (options.RefuseUnprotectedHighRiskTools)
        {
            if (options.KnownTools is null)
            {
                throw new InvalidOperationException(
                    "GatekeeperOptions.RefuseUnprotectedHighRiskTools is set but GatekeeperOptions.KnownTools is " +
                    "null — UseGatekeeper cannot read an agent's tool list at registration time (it runs before " +
                    ".Build()). Pass the same list you set on ChatOptions.Tools.");
            }

            GatekeeperCoverageAnalyzer.AnalyzeOrThrow(options.KnownTools, options.ToolGates.ToArray(), options.CoverageAnalyzeOptions);
        }

        // P0-6: will a run scope actually be established? Same condition that decides whether UseAgentEvalGate
        // gets called below — calling it ALWAYS establishes a scope (there is no way to call it "scopeless"),
        // so this is exact, not a heuristic.
        var scopeWillBeEstablished = options.EstablishRunScope || options.PreGates.Count > 0 || options.PostGates.Count > 0;

        if (!scopeWillBeEstablished && enforcement != GatekeeperEnforcement.Observe)
        {
            var offenders = options.ToolGates
                .Where(g => g.Requirements.HasFlag(GateRequirements.RunScope))
                .Select(g => g.PolicyName)
                // P2-6: result gates declare RunScope the same way and fall back to the same shared state — hold
                // them to the same construction-time refusal, not just the call gates.
                .Concat(options.ToolResultGates
                    .Where(g => g.Requirements.HasFlag(GateRequirements.RunScope))
                    .Select(g => g.PolicyName))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (offenders.Length > 0)
            {
                throw new InvalidOperationException(
                    $"UseGatekeeper: {string.Join(", ", offenders)} declare GateRequirements.RunScope, but " +
                    "GatekeeperOptions.EstablishRunScope is false, no pre/post gate implicitly establishes one, " +
                    $"and Enforcement is {enforcement} (not Observe). Without a run scope, these gates silently " +
                    "fall back to ONE process-wide shared state (self-documented on RunLedger/SequenceGate) — " +
                    "safe for advisory Observe mode, not for enforcement. Set EstablishRunScope = true (the " +
                    "default), or establish AgentRunScope yourself before this middleware runs.");
            }
        }

        var toolPolicy = ToToolGatePolicy(enforcement);
        var evalPolicy = ToEvalGatePolicy(enforcement);

        // Give a clear, Gatekeeper-contextualized error for a MinimumPolicy-floor conflict BEFORE the generic
        // one UseAgentEvalToolGate would throw below. This is the case where Observe's "zero behavior change,
        // safe to add anywhere" guarantee does NOT hold: a gate whose MinimumPolicy exceeds the resolved
        // toolPolicy (today, in practice, always a MinimumPolicy=ReplaceResult+ canary/honeypot gate under
        // Observe's WarnOnly) refuses to be silently downgraded — running a honeypot under WarnOnly would let
        // the forbidden action through while only logging it, defeating the trap.
        // P0-3: result gates share the exact same MinimumPolicy-floor mechanics as tool gates (see
        // IToolResultGate.MinimumPolicy remarks) — fold them into the same floor check rather than a second,
        // easy-to-forget copy.
        var flooredGates = options.ToolGates
            .Where(g => AgentEvalToolGateExtensions.EnforcementRank(g.MinimumPolicy) > AgentEvalToolGateExtensions.EnforcementRank(toolPolicy))
            .Select(g => $"{g.PolicyName} (needs {g.MinimumPolicy})")
            .Concat(options.ToolResultGates
                .Where(g => AgentEvalToolGateExtensions.EnforcementRank(g.MinimumPolicy) > AgentEvalToolGateExtensions.EnforcementRank(toolPolicy))
                .Select(g => $"{g.PolicyName} (needs {g.MinimumPolicy})"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (flooredGates.Length > 0)
        {
            throw new InvalidOperationException(
                $"UseGatekeeper: {string.Join(", ", flooredGates)} declare a MinimumPolicy stronger than the " +
                $"resolved enforcement ({enforcement} → {toolPolicy}). A gate with a MinimumPolicy floor cannot " +
                "be composed under a weaker policy — for Observe specifically, this means its \"safe to add " +
                "anywhere, zero behavior change\" guarantee does not extend to these gates. Exclude them from " +
                "this UseGatekeeper(...) call and add them separately (via UseAgentEvalToolGate, or a second, " +
                "stronger UseGatekeeper call) once you're ready to enforce at their MinimumPolicy or stronger.");
        }

        // Preflight the REST of UseAgentEvalToolGate's own validation (null elements, Network/Llm cost
        // rejection — the MinimumPolicy floor is already covered, more actionably, above) before any Use(...)
        // call below mutates the caller's builder. AIAgentBuilder.Use(...) mutates and returns the SAME
        // instance, not an immutable copy — a validation failure discovered only once UseAgentEvalToolGate
        // itself runs (after UseAgentEvalGate has already composed the run gate onto `result`, which IS
        // `builder`) would leave the caller's original builder half-composed with orphaned middleware and no
        // way to roll it back. Catching it here, before any mutation starts, means UseGatekeeper either fully
        // succeeds or leaves the caller's builder completely untouched.
        if (options.ToolGates.Count > 0)
        {
            AgentEvalToolGateExtensions.ValidateGates(options.ToolGates.ToArray(), toolPolicy);
        }

        // P0-3: same preflight-before-any-mutation discipline for result gates.
        if (options.ToolResultGates.Count > 0)
        {
            AgentEvalToolGateExtensions.ValidateResultGates(options.ToolResultGates.ToArray(), toolPolicy);
        }

        // Same reasoning for the approval gates' own validation (malformed PolicyName) — preflight it here too,
        // rather than let UseAgentEvalToolApproval discover it only after the run/tool gates already mutated
        // `result` (== `builder`).
        if (options.ApprovalGates.Count > 0)
        {
#pragma warning disable AEGK001 // validating, not yet composing — same experimental-opt-in reasoning as the actual UseAgentEvalToolApproval call below.
            AgentEvalToolApprovalExtensions.ValidateApprovalGates(options.ApprovalGates.ToArray());
#pragma warning restore AEGK001
        }

        // SkillGate Tier 1 enforcement (the actual check-and-possibly-persist-to-disk call) — regression fix:
        // this used to run immediately after the half-configuration validation above, BEFORE every other
        // preflight check in this method that can still throw (RefuseUnprotectedHighRiskTools, the run-scope
        // check, the MinimumPolicy floor check, ValidateGates/ValidateResultGates/ValidateApprovalGates). Under
        // SkillGateMode.Bootstrap, CheckAndEnforce can WRITE a newly-trusted skill to the baseline file — a
        // real disk mutation — so it must run LAST among the preflight checks, not first, to honor this
        // method's own "fail fast before ANY mutation" guarantee: a later validation failure must never leave
        // a skill trust-pinned to disk for an agent that was never actually built.
        if (options.Skills is not null && options.SkillBaselinePath is not null)
        {
            SkillGateConstructionCheck.CheckAndEnforce(options.Skills, options.SkillBaselinePath, options.SkillGateMode);
        }

        // P2-7 (F-E preflight): Observe's entire purpose is to RECORD gate findings as evidence. With neither a
        // Trace nor a Telemetry sink, every gate still runs but every finding is silently discarded — while the
        // banner below claims "findings are recorded to the trace." Refuse that misleading, silently-useless
        // configuration (fail-loud, like the rest of this preflight) rather than print a banner that lies. Real
        // enforcement modes are exempt: there, gates change behavior whether or not any evidence is captured.
        if (enforcement == GatekeeperEnforcement.Observe && options.Trace is null && options.Telemetry is null)
        {
            throw new InvalidOperationException(
                "UseGatekeeper: Observe mode records gate findings as evidence, but neither GatekeeperOptions.Trace " +
                "nor GatekeeperOptions.Telemetry is set — every finding would be silently discarded while the startup " +
                "banner claims they are recorded. Set Trace (an AgentTrace) and/or Telemetry (a GateTelemetry) so the " +
                "findings have somewhere to go, or use an enforcement mode (ReplaceResult/Terminate) if you want gates " +
                "to actually act.");
        }

        // Print the Observe banner only once every validation above has passed — a construction that's about
        // to throw must never first print "safe, nothing will ever block," which would be actively misleading
        // output right before the exception. Still strictly before any Use(...) mutation, so the banner reads
        // "here's what's about to be wired" rather than "here's what just ran."
        if (enforcement == GatekeeperEnforcement.Observe)
        {
            options.BannerWriter?.WriteLine("AGENTEVAL GATEKEEPER IS RUNNING IN OBSERVE-ONLY MODE. NO TOOL CALLS WILL BE BLOCKED.");
            options.BannerWriter?.WriteLine(
                $"  ({options.ToolGates.Count} tool gate(s), {options.ToolResultGates.Count} tool result gate(s), " +
                $"{options.PreGates.Count + options.PostGates.Count} run gate(s) " +
                $"registered — findings are recorded to the trace but never enforced.)");
        }

        // F-A (P1-4): inject the shared session-identity resolver into every ISessionIdentityAware gate, so a
        // deployment configures durable session identity ONCE (GatekeeperOptions.SessionIdentity) instead of per
        // gate. A gate with its own explicit resolver keeps it (UseSessionIdentityDefault is set-once). Done after
        // all preflight has passed but before any Use(...) mutation, same fail-fast-before-mutation placement.
        if (options.SessionIdentity is { } sessionIdentity)
        {
            foreach (var gate in options.PreGates.Cast<object>()
                .Concat(options.PostGates)
                .Concat(options.ToolGates)
                .Concat(options.ToolResultGates)
                .Concat(options.ApprovalGates))
            {
                if (gate is ISessionIdentityAware aware)
                {
                    aware.UseSessionIdentityDefault(sessionIdentity);
                }
            }
        }

        var result = builder;

        if (scopeWillBeEstablished)
        {
            result = result.UseAgentEvalGate(
                pre: options.PreGates.Count > 0 ? options.PreGates.ToArray() : null,
                post: options.PostGates.Count > 0 ? options.PostGates.ToArray() : null,
                policy: evalPolicy,
                trace: options.Trace,
                evidenceSink: options.EvidenceSink);
        }

        // P0-3: a result-gate-only configuration (ToolGates empty, ToolResultGates non-empty) is valid —
        // observing/protecting results doesn't require also gating the proposed calls — so this composes
        // whenever EITHER list is non-empty, not just when ToolGates is.
        if (options.ToolGates.Count > 0 || options.ToolResultGates.Count > 0)
        {
            result = result.UseAgentEvalToolGate(
                options.ToolGates.ToArray(), toolPolicy, options.Trace, options.Telemetry, options.MutationCaptureMode,
                options.ToolResultGates.Count > 0 ? options.ToolResultGates.ToArray() : null,
                options.EvidenceSink);
        }

        if (options.ApprovalGates.Count > 0)
        {
#pragma warning disable AEGK001 // Gatekeeper ⇄ MAF tool-approval interop is itself an AgentEval-experimental API — deliberately composed here when the caller opts in by registering an approval gate.
            result = result.UseAgentEvalToolApproval(options.ApprovalGates.ToArray(), options.Trace);
#pragma warning restore AEGK001
        }

        if (options.ShadowJudgePump is not null)
        {
            result = result.UseAgentEvalShadowJudge(options.ShadowJudgePump);
        }

        return result;
    }

    /// <summary>
    /// Sugar for <c>UseGatekeeper(builder, GatekeeperEnforcement.Observe, configure)</c> — the review's
    /// suggested split-API shape (P0-2). Nothing is ever blocked; a startup banner makes that explicit. See
    /// <see cref="GatekeeperEnforcement.Observe"/> remarks for the one exception: a gate with a
    /// <see cref="IToolGate.MinimumPolicy"/> floor above WarnOnly cannot be composed here at all — construction
    /// throws rather than silently downgrading it.
    /// </summary>
    public static AIAgentBuilder ObserveWithAgentEvalGates(this AIAgentBuilder builder, Action<GatekeeperOptions> configure)
        => builder.UseGatekeeper(GatekeeperEnforcement.Observe, configure);

    /// <summary>
    /// Sugar for <c>UseGatekeeper(builder, level, configure)</c> under a genuinely enforcing level (P0-2).
    /// Defaults to <see cref="GatekeeperEnforcement.Terminate"/> (the strongest level) — "enforce" unqualified
    /// should mean "actually protect me"; pass <see cref="GatekeeperEnforcement.ReplaceResult"/> explicitly for
    /// the softer level. <see cref="GatekeeperEnforcement.Observe"/> is rejected here — use
    /// <see cref="ObserveWithAgentEvalGates"/> for that (the whole point of the split is that the method name
    /// alone tells you which mode you're in).
    /// </summary>
    public static AIAgentBuilder EnforceAgentEvalGates(
        this AIAgentBuilder builder,
        Action<GatekeeperOptions> configure,
        GatekeeperEnforcement level = GatekeeperEnforcement.Terminate)
    {
        if (level == GatekeeperEnforcement.Observe)
        {
            throw new ArgumentException(
                $"EnforceAgentEvalGates requires an enforcing level (ReplaceResult or Terminate); got {level}. " +
                "Use ObserveWithAgentEvalGates for observe-only mode.", nameof(level));
        }

        return builder.UseGatekeeper(level, configure);
    }

    private static ToolGatePolicy ToToolGatePolicy(GatekeeperEnforcement enforcement) => enforcement switch
    {
        GatekeeperEnforcement.Observe => ToolGatePolicy.WarnOnly,
        GatekeeperEnforcement.ReplaceResult => ToolGatePolicy.ReplaceResult,
        GatekeeperEnforcement.Terminate => ToolGatePolicy.Terminate,
        _ => throw new ArgumentOutOfRangeException(nameof(enforcement), enforcement, "unrecognized GatekeeperEnforcement value."),
    };

    private static EvalGatePolicy ToEvalGatePolicy(GatekeeperEnforcement enforcement) => enforcement switch
    {
        GatekeeperEnforcement.Observe => EvalGatePolicy.WarnOnly,
        GatekeeperEnforcement.ReplaceResult => EvalGatePolicy.Redact,
        GatekeeperEnforcement.Terminate => EvalGatePolicy.ThrowOnFail,
        _ => throw new ArgumentOutOfRangeException(nameof(enforcement), enforcement, "unrecognized GatekeeperEnforcement value."),
    };
}
