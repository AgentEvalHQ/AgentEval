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
/// <b>Known overload of code 2 (BUG-22).</b> System.CommandLine automatically returns
/// <see cref="UsageError"/> (2) for bad arguments, but the <c>bench</c>/<c>calibrate</c> command
/// family also returns 2 to mean "benchmark gate FAIL/WARN". CI therefore cannot distinguish
/// "invoked wrong" from "agent failed the gate" purely from code 2. The eval/red-team commands
/// correctly use 1 for failure and 3 for runtime errors.
/// </para>
/// <para>
/// The recommended cleanup is to map benchmark gate FAIL→1 / WARN→(a dedicated code) and route
/// judge build/config failures (JudgeFactory) to <see cref="RuntimeError"/> (3), reserving 2
/// strictly for argument-parse errors. However, both are BREAKING changes to the
/// verbatim-preserved CI contract above — the bench/calibrate gate-FAIL=2 and the
/// JudgeFactory-config-failure=2 behaviours are each relied upon by ~28+ tests and by external
/// CI — so the behavioural remap is intentionally DEFERRED pending explicit sign-off (BUG-22).
/// Until then, treat code 2 from a bench/calibrate command as
/// "gate not passed (FAIL/WARN) OR judge-config failure OR bad arguments".
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
    /// turn that would reach or exceed the cap never fires, and this exit code is returned instead.
    /// </summary>
    public const int BudgetExceeded = 8;
}
