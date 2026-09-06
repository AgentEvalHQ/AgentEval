// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from the AgentEvalHQ/AgentEval.Cli repository (v0.2.0-alpha) as part of the
// v1.1 CLI consolidation. The external CLI's documented exit-code contract is preserved
// verbatim so that CI/CD pipelines depending on these values continue to work.

namespace AgentEval.Cli;

/// <summary>
/// Exit codes for CI/CD integration. Public so the CLI's documented contract
/// (0 = pass, 1 = test failure, 2 = usage error, 3 = runtime error) can be
/// asserted by external tooling and tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>BUG-22 — RESOLVED 2026-07-19.</b> Code 2 used to be overloaded: System.CommandLine
/// automatically returns <see cref="UsageError"/> (2) for bad arguments, but the
/// <c>bench</c>/<c>calibrate</c> command family also returned 2 to mean "benchmark gate
/// FAIL/WARN", AND <c>JudgeFactory</c> config failures also returned 2. CI could not
/// distinguish "invoked wrong" from "agent failed the gate" from "judge misconfigured"
/// purely from code 2.
/// </para>
/// <para>
/// <b>Resolution:</b> judge build/config failures (<c>JudgeFactory.Resolve</c> — missing/partial
/// Azure OpenAI credentials, or a thrown exception constructing the client) now return
/// <see cref="RuntimeError"/> (3) — it's a runtime/environment problem, not a bad CLI argument.
/// Benchmark/calibration gate outcomes now return one of three dedicated codes:
/// <see cref="GateFailed"/> (9), <see cref="GateWarning"/> (10), or
/// <see cref="GateIndeterminate"/> (11) — see those members for exactly which command paths
/// return which. <see cref="UsageError"/> (2) is now reserved strictly for actual bad-argument
/// paths (unknown flags, malformed input files, unresolvable judge axis, etc.).
/// </para>
/// <para>
/// <b>This is a breaking change to the CI contract</b> documented above: a <c>bench</c>/
/// <c>calibrate</c> command that used to exit 2 on gate FAIL/WARN or judge-config failure now
/// exits 9/10/11 or 3 respectively. External CI pipelines that branch on exit code 2 from these
/// commands must be updated to check the new codes. See CHANGELOG.md.
/// </para>
/// </remarks>
public static class ExitCodes
{
    /// <summary>All tests passed.</summary>
    public const int Success = 0;

    /// <summary>One or more tests failed.</summary>
    public const int TestFailure = 1;

    /// <summary>CLI usage error (bad arguments) — set by System.CommandLine automatically.</summary>
    public const int UsageError = 2;

    /// <summary>Runtime error (connection failure, file not found, etc.).</summary>
    public const int RuntimeError = 3;

    /// <summary>
    /// A regression was detected versus a supplied <c>--baseline</c> (new vulnerabilities, or a score/coverage drop
    /// beyond the comparison thresholds). Distinct from <see cref="TestFailure"/> (1) so CI can tell "a NEW finding
    /// appeared" apart from "pre-existing findings remain". Used by the red-team command's <c>--fail-on</c> gate.
    /// </summary>
    public const int RegressionFailure = 4;

    // ── gatekeeper CLI bridge (0–4 above are the verbatim-preserved contract; 5–7 are genuinely free) ──

    /// <summary>
    /// <c>gatekeeper inspect</c>: a gate returned <b>Block</b> on real evidence — a deterministic block, a judge block
    /// at/above threshold, or a judge fail-closed-inconclusive that the shipped gate itself renders as Block. Distinct
    /// from <see cref="RuntimeError"/> (3, a crash) and <see cref="Success"/> (0) so CI can gate on "policy blocked".
    /// </summary>
    public const int GateBlocked = 5;

    /// <summary>
    /// <c>gatekeeper inspect</c>: <b>fail-closed because the CLI could not evaluate</b> — e.g. a history-reading tool
    /// gate was given no/malformed <c>messages</c>, or empty text where the gate requires content. Distinct from
    /// <see cref="GateBlocked"/> (5) so CI can tell "blocked by policy" from "missing context — fix the payload".
    /// </summary>
    public const int GateInconclusive = 6;

    /// <summary>
    /// <c>gatekeeper inspect</c>: the <b>honesty guard refused</b> — a judge (or a judge child of a panel) is not
    /// certified inline-ready for the given model and <c>--allow-uncalibrated</c> was not passed. Distinct from
    /// <see cref="UsageError"/> (2, bad flags) and <see cref="GateBlocked"/> (5, a real Block) so CI can tell
    /// "run <c>calibrate --certify</c> first" apart from a typo or a policy block. Deliberately NOT another overload
    /// of 2 (the BUG-22 lesson).
    /// </summary>
    public const int NotCertified = 7;

    // ── copilot studio live red-team target (0–7 above are taken; 8+ for new gated capabilities) ──

    /// <summary>
    /// <c>redteam --sut copilot-studio</c>: the run stopped because it hit the <c>--max-credits</c> budget cap — a
    /// live Copilot Studio target burns Copilot Credits on every turn, so the scan is capped to a spend ceiling.
    /// Distinct from a policy/gate outcome (5/6/7) and from <see cref="RuntimeError"/> (3, a crash) so CI can tell
    /// "raise the budget / it cost more than expected" apart from a failure. Deliberately 8, not another overload of 2.
    /// <b>Enforced as an estimate</b>: the Copilot Studio SDK exposes no real credit-cost field, so
    /// <c>CopilotStudioChatClient</c> counts turns (1 estimated credit each) rather than metering actual spend — a
    /// turn that would push spend PAST the cap never fires (spend may legitimately reach exactly the cap).
    /// <c>CopilotStudioBudgetExceededException</c> extends <c>AgentEval.Core.FatalEvaluationException</c> so the
    /// probe loop lets it propagate (rather than swallowing it into a per-probe error, which would just repeat
    /// the same root cause for every remaining probe) and this exit code is returned instead. <b>Scope note</b>:
    /// this exit code is <c>redteam</c>-specific — <c>eval</c>/<c>bench gdpr</c>/<c>bench eu-ai-act</c> also
    /// enforce and propagate the same condition (the scan/run stops rather than degrading), but return their own
    /// existing generic failure codes (<see cref="RuntimeError"/> for <c>eval</c>, <c>1</c> for <c>bench</c>),
    /// not this one.
    /// </summary>
    public const int BudgetExceeded = 8;

    // ── bench/calibrate gate outcomes (BUG-22 resolution; 9-11 are genuinely free) ──

    /// <summary>
    /// <c>bench &lt;family&gt;</c> / <c>bench &lt;regulation&gt; calibrate</c>: the composite gate (or, for
    /// calibration, one or more pillar's accuracy/kappa thresholds) evaluated to a hard <b>FAIL</b>. Distinct
    /// from <see cref="UsageError"/> (2, bad arguments) and <see cref="GateWarning"/> (10, a soft finding) —
    /// see <see cref="ExitCodes"/>'s BUG-22 remarks. Also returned by <c>bench workflow-trace-fidelity</c>
    /// (a binary pass/fail gate with no WARN state).
    /// </summary>
    public const int GateFailed = 9;

    /// <summary>
    /// <c>bench &lt;family&gt;</c>: the composite gate evaluated to <b>WARN</b> — below the ideal bar but not a
    /// hard failure. Distinct from <see cref="GateFailed"/> (9) so CI can choose to treat WARN as non-blocking
    /// while still surfacing it separately from a clean PASS (0). Calibration commands never return this code —
    /// their pillar thresholds are pass/fail binary.
    /// </summary>
    public const int GateWarning = 10;

    /// <summary>
    /// <c>bench &lt;family&gt;</c>: the composite gate could not produce a conclusive PASS/WARN/FAIL verdict
    /// (e.g. label <c>"skipped"</c> — no scoreable criteria matched). Distinct from <see cref="GateFailed"/> (9,
    /// a real failure) and mirrors <see cref="GateInconclusive"/> (6)'s "we couldn't evaluate" semantics for the
    /// gatekeeper bridge.
    /// </summary>
    public const int GateIndeterminate = 11;

    // ── compare (ADR-031 S5 / §8.4) ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>agenteval compare</c>: the two runs could NOT be shown comparable, so no delta was
    /// emitted. ADR-031 §8.4 reserved this code for exactly this meaning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This is a CI-READER CONTRACT ADDITION.</b> Before it, <see cref="ExitCodes"/> topped out
    /// at <see cref="GateIndeterminate"/> (11), and a reader that branches on "anything above 11 is
    /// impossible" now has a new case. It is deliberately NOT folded into
    /// <see cref="GateIndeterminate"/>: 11 means the run produced nothing scoreable, whereas 13
    /// means two runs each produced a perfectly good verdict and comparing them would have been
    /// meaningless. Collapsing them re-creates BUG-22's defect — one code with several meanings —
    /// and in the flattering direction, because a team seeing "indeterminate" investigates the run
    /// rather than the comparison.
    /// </para>
    /// <para>
    /// ⚠ <b>12 (<c>InstrumentVoid</c>) is reserved by ADR-031 §8.4 and is deliberately NOT added
    /// here.</b> It belongs to S4 (<c>controlLedger</c> + the <c>VOID</c> verdict), which is gated on
    /// an open user decision (Q5). Claiming the number before the feature exists would leave a
    /// documented code nothing can return.
    /// </para>
    /// </remarks>
    public const int Incomparable = 13;
}
