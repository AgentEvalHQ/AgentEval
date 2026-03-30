// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.NuGetConsumer;

namespace AgentEval.NuGetConsumer.Tests;

/// <summary>
/// Shared setup for all integration tests.
/// All tests in this project require Azure OpenAI credentials.
/// </summary>
internal static class TestSetup
{
    /// <summary>
    /// Skips the current test when Azure OpenAI credentials are not configured,
    /// keeping <c>dotnet test</c> green in environments without secrets (local dev, default CI).
    /// Tests are only executed when all three required environment variables are present.
    /// </summary>
    internal static void EnsureConfigured()
    {
        if (!Config.IsConfigured)
            throw Xunit.Sdk.SkipException.ForSkip(
                "Azure OpenAI not configured. " +
                "Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT environment variables.");
    }
}
