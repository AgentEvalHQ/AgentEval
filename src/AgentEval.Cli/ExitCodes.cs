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
}
