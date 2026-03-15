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
    /// Throws if Azure OpenAI credentials are not configured.
    /// Call at the start of every test to fail fast with a clear message
    /// instead of getting cryptic NullReferenceExceptions mid-test.
    /// </summary>
    internal static void EnsureConfigured()
    {
        if (!Config.IsConfigured)
            throw new InvalidOperationException(
                "Azure OpenAI not configured. " +
                "Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT environment variables.");
    }
}
