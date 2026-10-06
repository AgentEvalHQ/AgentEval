// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Providers;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>agenteval log-file replay --azure-from-env</c> resolves whichever provider <c>AI_INFERENCE_PROVIDER</c>
/// selects, so its help, its error and the target label in the report must not claim Azure OpenAI.
/// </summary>
/// <remarks>
/// Before this, the help listed the <c>AZURE_OPENAI_*</c> trio, the error said "Azure OpenAI env vars not
/// configured", and a replay served by Bitdeer or OpenAI was labelled <c>azure:&lt;model&gt;</c> in the report.
/// Constructing a client makes no network call, so these tests are offline.
/// </remarks>
[Collection("EnvVarTests")]   // every provider variable is cleared for this collection
public class LogFileReplayTargetTests
{
    private const string BitdeerKey = "bd-test-key-not-real";

    [Fact]
    public void ReplayHelp_AzureFromEnv_NamesTheProviderSelectorNotTheAzureTrio()
    {
        // Fails on the old help, which read "Replay against Azure OpenAI, configured via AZURE_OPENAI_ENDPOINT / ...".
        var replay = LogFileCommand.Create().Subcommands.Single(c => c.Name == "replay");
        var help = replay.Options.Single(o => o.Name == "--azure-from-env").Description;

        Assert.NotNull(help);
        Assert.Contains("AI_INFERENCE_PROVIDER", help, StringComparison.Ordinal);
        Assert.DoesNotContain("AZURE_OPENAI_ENDPOINT", help, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAgainst_AzureFromEnv_BitdeerSelected_LabelsTheTargetWithBitdeer()
    {
        // Fails on the old label, which was "azure:<model>" whichever provider built the client.
        using var _ = new ProviderEnvironmentScope(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("BITDEER_API_KEY", BitdeerKey));

        var (client, label, error) = LogFileCommand.ResolveAgainst(endpoint: null, model: null, apiKey: null, azureFromEnv: true);

        Assert.NotNull(client);
        Assert.Null(error);
        Assert.Equal($"bitdeer:{InferenceProviderEnvironment.BitdeerDefaultModel}", label);
    }

    [Fact]
    public void ResolveAgainst_AzureFromEnv_NoProviderConfigured_ErrorNamesTheSelectorNotAzure()
    {
        // Fails on the old error, "Azure OpenAI env vars not configured — see the message above for what's missing."
        using var _ = new ProviderEnvironmentScope();
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var (client, label, error) = LogFileCommand.ResolveAgainst(endpoint: null, model: null, apiKey: null, azureFromEnv: true);

            Assert.Null(client);
            Assert.Equal(string.Empty, label);
            Assert.NotNull(error);
            Assert.Contains("AI_INFERENCE_PROVIDER", error, StringComparison.Ordinal);
            Assert.DoesNotContain("Azure OpenAI env vars", error, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void EnvironmentTargetLabel_SettingsResolveToAnotherModel_SaysTheProviderIsUnknown()
    {
        // The provider is read back from the environment; when that no longer matches the model the client was
        // built for, the label must not name a provider it cannot vouch for. The old code wrote "azure:<model>"
        // here too, whatever had built the client.
        var settings = InferenceProviderEnvironment.Resolve(name => name switch
        {
            "AI_INFERENCE_PROVIDER" => "bitdeer",
            "BITDEER_API_KEY" => BitdeerKey,
            _ => null,
        });
        Assert.True(settings.IsConfigured);

        Assert.Equal("unknown-provider:some-other-model", LogFileCommand.EnvironmentTargetLabel("some-other-model", settings));
        Assert.Equal(
            $"bitdeer:{InferenceProviderEnvironment.BitdeerDefaultModel}",
            LogFileCommand.EnvironmentTargetLabel(InferenceProviderEnvironment.BitdeerDefaultModel, settings));
    }

    [Fact]
    public void EnvironmentTargetLabel_NothingConfigured_SaysTheProviderIsUnknown()
    {
        // The old code would have written "azure:gpt-4o" with no provider resolved at all.
        var label = LogFileCommand.EnvironmentTargetLabel("gpt-4o", InferenceProviderSettings.NotConfigured("none set"));

        Assert.Equal("unknown-provider:gpt-4o", label);
    }
}
