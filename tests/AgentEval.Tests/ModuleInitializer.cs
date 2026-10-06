// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using VerifyTests;

namespace AgentEval.Tests;

/// <summary>
/// Initializes Verify settings for the test project.
/// </summary>
public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        // Never launch a diff tool from a test run. On a failing snapshot Verify otherwise opens the first diff
        // tool it finds (on a maintainer machine that was Vim), popping a window per failure in the middle of a
        // build. The received/verified files and the test message already carry the difference.
        DiffEngine.DiffRunner.Disabled = true;

        // Use directory next to test file for snapshots
        UseProjectRelativeDirectory("Snapshots");
        
        // Common scrubbing patterns for AI responses
        VerifierSettings.ScrubInlineGuids();
        
        // Scrub common volatile fields that vary between runs
        VerifierSettings.ScrubMembers("Duration", "Timestamp", "StartTime", "EndTime", "ElapsedMs", "DurationMs");
    }
}
