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
