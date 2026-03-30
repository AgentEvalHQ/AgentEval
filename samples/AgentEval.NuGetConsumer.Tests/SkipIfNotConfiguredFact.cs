// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Xunit;

namespace AgentEval.NuGetConsumer.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that auto-skips the test when Azure OpenAI credentials
/// are not configured. Works with all xUnit v2 runners including <c>dotnet test</c>
/// (VSTest), because the <see cref="FactAttribute.Skip"/> property is evaluated at
/// discovery time rather than at execution time.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SkipIfNotConfiguredFact : FactAttribute
{
    private const string SkipMessage =
        "Azure OpenAI not configured. " +
        "Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT environment variables.";

    public SkipIfNotConfiguredFact()
    {
        if (!NuGetConsumer.Config.IsConfigured)
            Skip = SkipMessage;
    }
}
