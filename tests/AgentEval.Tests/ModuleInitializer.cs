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
        // Use directory next to test file for snapshots
        UseProjectRelativeDirectory("Snapshots");
        
        // Common scrubbing patterns for AI responses
        VerifierSettings.ScrubInlineGuids();
        
        // Scrub common volatile fields that vary between runs
        VerifierSettings.ScrubMembers("Duration", "Timestamp", "StartTime", "EndTime", "ElapsedMs", "DurationMs");
    }
}
