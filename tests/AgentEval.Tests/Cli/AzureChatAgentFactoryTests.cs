// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Pins the T0.2 honest fix for the "CLI scans stub agents" gap. The factory must:
/// 1) refuse to build when any of the 3 required env vars is missing (returns exit code 3, RuntimeError — BUG-22);
/// 2) print a precise error naming the missing vars;
/// 3) document its env-var convention identical to <see cref="JudgeFactory"/>'s.
/// </summary>
/// <remarks>
/// We do NOT test the success path here because it requires real Azure creds — that
/// path is covered transitively when the CLI is run with <c>--azure-from-env</c>
/// against a real deployment. See plan-13 T0.2 acceptance criteria.
///
/// T3.3 (2026-05-25): joined the "ConsoleIO" xUnit collection so the
/// stderr-capturing tests below do not race other Console-capturing tests
/// (MigrateCommandTests, CalibrationRunner regression tests, etc.) on
/// concurrent parallel workers.
/// </remarks>
[Collection("ConsoleTests")]
public class AzureChatAgentFactoryTests
{
    [Fact]
    public void TryBuildFromEnv_AllVarsMissing_ReturnsExitCode3WithFriendlyError()
    {
        // Arrange — clear all 3 vars
        using var _ = TempEnvVars(("AZURE_OPENAI_ENDPOINT", null), ("AZURE_OPENAI_API_KEY", null), ("AZURE_OPENAI_DEPLOYMENT", null));
        var stderr = new StringWriter();
        var prev = Console.Error;
        Console.SetError(stderr);
        try
        {
            // Act
            var (agent, exitCode) = AzureChatAgentFactory.TryBuildFromEnv("TestSubject");

            // Assert — null agent, exit 2, error names all 3 missing vars
            Assert.Null(agent);
            Assert.Equal(3, exitCode);
            var err = stderr.ToString();
            Assert.Contains("AZURE_OPENAI_ENDPOINT", err);
            Assert.Contains("AZURE_OPENAI_API_KEY", err);
            Assert.Contains("AZURE_OPENAI_DEPLOYMENT", err);
        }
        finally
        {
            Console.SetError(prev);
        }
    }

    [Fact]
    public void TryBuildFromEnv_PartialConfig_ReportsOnlyMissingVars()
    {
        // Arrange — set endpoint, leave key + deployment missing
        using var _ = TempEnvVars(
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", null),
            ("AZURE_OPENAI_DEPLOYMENT", null));
        var stderr = new StringWriter();
        var prev = Console.Error;
        Console.SetError(stderr);
        try
        {
            var (agent, exitCode) = AzureChatAgentFactory.TryBuildFromEnv("TestSubject");

            Assert.Null(agent);
            Assert.Equal(3, exitCode);
            var err = stderr.ToString();
            Assert.DoesNotContain("AZURE_OPENAI_ENDPOINT", err); // endpoint IS set, so should NOT be in the missing list
            Assert.Contains("AZURE_OPENAI_API_KEY", err);
            Assert.Contains("AZURE_OPENAI_DEPLOYMENT", err);
        }
        finally
        {
            Console.SetError(prev);
        }
    }

    [Fact]
    public void PrintStubAgentWarning_WritesBannerToStderr()
    {
        var stderr = new StringWriter();
        var prev = Console.Error;
        Console.SetError(stderr);
        try
        {
            AzureChatAgentFactory.PrintStubAgentWarning("OWASP", "TestStubAgent");

            var err = stderr.ToString();
            // The banner must mention the benchmark name, the stub agent, and the recovery path.
            Assert.Contains("OWASP", err);
            Assert.Contains("TestStubAgent", err);
            Assert.Contains("--azure-from-env", err);
            Assert.Contains("AZURE_OPENAI_ENDPOINT", err);
            // The banner is intended to be highly visible — sanity-check it includes the warning glyph.
            Assert.Contains("⚠", err);
        }
        finally
        {
            Console.SetError(prev);
        }
    }

    [Fact]
    public void TryBuildChatClientFromEnv_LogFileActive_ReturnedClientIsVerboseLogWrapped()
    {
        // AzureOpenAIClient construction (and .GetChatClient(...).AsIChatClient()) makes no network call — same
        // premise CopilotStudioAgentFactory's own credential-free construction tests rely on — so this proves
        // the --log-file wiring at this specific call site without needing real Azure credentials or a live call.
        using var _ = TempEnvVars(
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "fake-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "fake-deployment"));
        var path = Path.Combine(Path.GetTempPath(), "agenteval-azurechatagentfactory-logtest-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var logWriter = VerboseLog.Initialize(path);

            var (chatClient, deployment, exitCode) = AzureChatAgentFactory.TryBuildChatClientFromEnv();

            Assert.Equal(0, exitCode);
            Assert.Equal("fake-deployment", deployment);
            Assert.IsType<VerboseLoggingChatClient>(chatClient);
        }
        finally
        {
            VerboseLog.Initialize(null);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Temporarily sets env vars (or removes them when value is null), restoring them
    /// on dispose. Used to make these tests order-independent and parallel-safe within
    /// a single test class (xUnit runs tests within a class serially by default).
    /// </summary>
    private static IDisposable TempEnvVars(params (string Name, string? Value)[] vars)
    {
        var originals = vars
            .Select(v => (v.Name, Original: Environment.GetEnvironmentVariable(v.Name)))
            .ToArray();
        foreach (var v in vars)
            Environment.SetEnvironmentVariable(v.Name, v.Value);
        return new EnvVarScope(originals);
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly (string Name, string? Original)[] _originals;
        public EnvVarScope((string Name, string? Original)[] originals) => _originals = originals;
        public void Dispose()
        {
            foreach (var v in _originals)
                Environment.SetEnvironmentVariable(v.Name, v.Original);
        }
    }
}
